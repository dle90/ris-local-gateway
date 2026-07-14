using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using FellowOakDicom;
using FellowOakDicom.Imaging.Codec;
using Medisync.RisLocalGateway.Core.Configuration;
using Medisync.RisLocalGateway.Core.Ris;
using Medisync.RisLocalGateway.Core.Utils;
using Microsoft.Extensions.Logging;

namespace Medisync.RisLocalGateway.Dicom;

/// <summary>
/// Forward 1 instance DICOM tới PACS qua STOW-RS (đi qua dicomweb-proxy):
///   1) (tuỳ chọn) transcode ảnh uncompressed → JPEG-LS Lossless (mặc định, decode nhanh);
///      RIÊNG modality ảnh lớn (MG=mammo/DBT, SM=pathology) → HTJ2K Lossless RPCL (progressive),
///   2) STOW multipart/related kèm Bearer token của gateway — dicomweb-proxy verify
///      token (iss=HIS, aud=LOCAL-GATEWAY-SERVER) rồi tự gắn label tenant cho study.
/// Cấu hình tại GatewayConfig.Pacs (StowUrl + Compression + CompressionCodec + Htj2kModalities).
/// </summary>
/// <summary>
/// Kết quả forward 1 instance — để SpoolForwarder quyết định dead-letter ĐÚNG loại lỗi:
///   • Success         → xoá khỏi spool.
///   • RetryableError  → lỗi HẠ TẦNG tạm thời (PACS/RIS/mạng down, thiếu token, 401/403/timeout/5xx).
///                       KHÔNG dead-letter sớm; thử lại tới cap an toàn rất cao (Pacs.MaxRetries).
///   • PermanentError  → ảnh bị từ chối VĨNH VIỄN (HTTP 400/409/413/415/422) hoặc file hỏng.
///                       Dead-letter NGAY vì thử lại vô ích.
/// </summary>
public enum ForwardOutcome { Success, RetryableError, PermanentError }

/// <summary>
/// Kết quả forward kèm CHI TIẾT lỗi (để ghi sidecar lý do dead-letter). Detail là chuỗi ngắn
/// human-readable: "HTTP 400 …", "STOW timeout 120s", "exception …"; null khi Success.
/// </summary>
public readonly record struct ForwardResult(ForwardOutcome Outcome, string? Detail)
{
    public static readonly ForwardResult Ok = new(ForwardOutcome.Success, null);
    public static ForwardResult Retryable(string detail) => new(ForwardOutcome.RetryableError, detail);
    public static ForwardResult Permanent(string detail) => new(ForwardOutcome.PermanentError, detail);
}

public interface IPacsStowClient
{
    Task<ForwardResult> ForwardAsync(DicomFile file, CancellationToken ct = default);
}

public sealed class PacsStowClient : IPacsStowClient, IDisposable
{
    private readonly IRisClient _ris;
    private readonly ConfigStore _configStore;
    private readonly ILogger<PacsStowClient> _logger;
    private readonly HttpClient _http;

    public PacsStowClient(IRisClient ris, ConfigStore configStore, ILogger<PacsStowClient> logger)
    {
        _ris = ris;
        _configStore = configStore;
        _logger = logger;
        // Timeout vô hạn ở HttpClient; timeout thực thi qua CancellationToken mỗi request
        // (đổi HttpClient.Timeout sau request đầu tiên sẽ ném lỗi).
        // PooledConnectionLifetime: recycle connection 5' để re-resolve DNS khi PACS/proxy đổi IP
        // (service chạy 24/7, không có dịp restart để làm mới connection pool).
        _http = new HttpClient(new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        })
        { Timeout = Timeout.InfiniteTimeSpan };
    }

    public async Task<ForwardResult> ForwardAsync(DicomFile file, CancellationToken ct = default)
    {
        var pacs = _configStore.Load().Pacs;
        if (string.IsNullOrWhiteSpace(pacs.StowUrl))
        {
            // Config chưa sẵn (admin sẽ điền) — coi là tạm thời để KHÔNG dead-letter mất ảnh.
            _logger.LogError("PACS chưa cấu hình (Pacs.StowUrl rỗng) — không forward được");
            return ForwardResult.Retryable("PACS chưa cấu hình (Pacs.StowUrl rỗng)");
        }

        var sop = file.Dataset.GetSingleValueOrDefault(DicomTag.SOPInstanceUID, "(none)");
        var timeoutSec = pacs.TimeoutSeconds > 0 ? pacs.TimeoutSeconds : 120;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(timeoutSec));
        var sendCt = cts.Token;

        // 1) Transcode nếu bật (luôn chỉ nén ảnh đang uncompressed): JPEG-LS mặc định,
        //    modality ảnh lớn → HTJ2K RPCL.
        var toSend = MaybeCompress(file, pacs, sop);

        // 2) Serialize DICOM → bytes.
        byte[] bytes;
        try
        {
            using var ms = new MemoryStream();
            toSend.Save(ms);
            bytes = ms.ToArray();
        }
        catch (Exception ex)
        {
            // Serialize bản gốc lỗi = file hỏng → thử lại vô ích.
            _logger.LogError(ex, "Serialize DICOM lỗi sop={Sop}", sop);
            return ForwardResult.Permanent("Serialize DICOM lỗi: " + ex.Message);
        }

        // 3) Token gateway (cùng token gọi RIS — mang companyUuid/facilityUuid cho proxy label).
        string? token;
        try { token = await _ris.GetAccessTokenAsync(sendCt).ConfigureAwait(false); }
        catch (Exception ex) { _logger.LogWarning(ex, "Lấy token lỗi"); token = null; }
        if (string.IsNullOrEmpty(token))
            _logger.LogWarning("Không có access token — STOW gửi KHÔNG kèm Bearer (proxy auth on sẽ 401)");

        // 4) STOW multipart/related.
        try
        {
            var boundary = "stow-" + Guid.NewGuid().ToString("N");
            using var multipart = new MultipartContent("related", boundary);
            var part = new ByteArrayContent(bytes);
            part.Headers.ContentType = new MediaTypeHeaderValue("application/dicom");
            multipart.Add(part);
            // STOW-RS yêu cầu tham số type trong Content-Type của multipart.
            if (multipart.Headers.ContentType is not null)
                multipart.Headers.ContentType.Parameters.Add(
                    new NameValueHeaderValue("type", "\"application/dicom\""));

            using var req = new HttpRequestMessage(HttpMethod.Post, pacs.StowUrl) { Content = multipart };
            if (!string.IsNullOrEmpty(token))
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/dicom+json"));

            var sw = System.Diagnostics.Stopwatch.StartNew();
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseContentRead, sendCt).ConfigureAwait(false);
            sw.Stop();
            var code = (int)resp.StatusCode;

            if (code == 200 || code == 202)
            {
                _logger.LogInformation("STOW ← {Code} ({Ms}ms) sop={Sop} {Size}B ts={Ts}",
                    code, sw.ElapsedMilliseconds, sop, bytes.Length, toSend.Dataset.InternalTransferSyntax.UID.Name);
                return ForwardResult.Ok;
            }

            var body = await resp.Content.ReadAsStringAsync(sendCt).ConfigureAwait(false);
            var outcome = ClassifyFailure(code);
            var detail = $"HTTP {code} {resp.ReasonPhrase}: {TextUtil.Truncate(body, 300)}";
            _logger.LogError("STOW ← {Code} {Reason} ({Ms}ms) sop={Sop} [{Outcome}] — {Body}",
                code, resp.ReasonPhrase, sw.ElapsedMilliseconds, sop, outcome, TextUtil.Truncate(body, 500));
            return new ForwardResult(outcome, detail);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            // Timeout STOW (PACS/mạng chậm) = tạm thời → thử lại.
            _logger.LogError("STOW timeout ({Sec}s) sop={Sop} → {Url}", timeoutSec, sop, pacs.StowUrl);
            return ForwardResult.Retryable($"STOW timeout {timeoutSec}s");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // shutdown thật — để caller dừng vòng quét.
        }
        catch (Exception ex)
        {
            // Lỗi mạng/socket/HTTP = tạm thời → thử lại, không mất ảnh.
            _logger.LogError(ex, "STOW exception sop={Sop} → {Url}", sop, pacs.StowUrl);
            return ForwardResult.Retryable("STOW exception: " + ex.Message);
        }
    }

    /// <summary>
    /// Phân loại HTTP code lỗi → outcome. Chỉ các mã "ảnh bị từ chối vĩnh viễn" (request/content
    /// sai, gửi lại vô ích) mới PermanentError; mọi mã còn lại (401/403/408/429/5xx + mã lạ) coi là
    /// tạm thời để ƯU TIÊN GIỮ ẢNH — thà thử lại nhiều còn hơn dead-letter oan khi PACS/RIS chợt lỗi.
    /// </summary>
    private static ForwardOutcome ClassifyFailure(int code) => code switch
    {
        400 or 409 or 413 or 415 or 422 => ForwardOutcome.PermanentError,
        _                               => ForwardOutcome.RetryableError,
    };

    /// <summary>
    /// Transcode ảnh uncompressed sang codec đích nếu bật. Quy tắc:
    ///   • Modality ∈ Pacs.Htj2kModalities (mặc định MG, SM) → HTJ2K Lossless RPCL (progressive,
    ///     hợp ảnh rất lớn: mammo/DBT/pathology).
    ///   • Còn lại → Pacs.CompressionCodec (mặc định JPEG-LS Lossless — decode nhanh).
    /// Ảnh đã nén (encapsulated) → giữ nguyên, không re-encode. Lỗi transcode → forward bản gốc.
    /// </summary>
    private DicomFile MaybeCompress(DicomFile file, PacsConfig pacs, string sop)
    {
        if (!pacs.CompressionEnabled) return file;

        var current = file.Dataset.InternalTransferSyntax;
        if (current.IsEncapsulated) // đã nén (kể cả JPEG 2000/JPEG-LS) → giữ nguyên
        {
            _logger.LogDebug("sop={Sop} đã nén ({Cur}) — giữ nguyên", sop, current.UID.Name);
            return file;
        }

        var modality = file.Dataset.GetSingleValueOrDefault(DicomTag.Modality, string.Empty);
        var useHtj2k = !string.IsNullOrEmpty(modality)
            && pacs.Htj2kModalities is { Length: > 0 }
            && Array.Exists(pacs.Htj2kModalities, m => string.Equals(m, modality, StringComparison.OrdinalIgnoreCase));
        var target = useHtj2k ? DicomTransferSyntax.HTJ2KLosslessRPCL : CodecToTs(pacs.CompressionCodec);

        try
        {
            var transcoded = new DicomTranscoder(current, target).Transcode(file);
            _logger.LogDebug("Transcode sop={Sop} mod={Mod}: {From} → {To}",
                sop, modality, current.UID.Name, target.UID.Name);
            return transcoded;
        }
        catch (Exception ex)
        {
            // Lossless transcode lỗi (vd thiếu codec native) → forward bản gốc, không mất ảnh.
            _logger.LogWarning(ex, "Transcode sop={Sop} {From} → {To} lỗi — forward bản gốc",
                sop, current.UID.Name, target.UID.Name);
            return file;
        }
    }

    /// <summary>Map tên codec (config) → transfer syntax. Mặc định JPEG-LS Lossless.</summary>
    private static DicomTransferSyntax CodecToTs(string? codec) => (codec ?? string.Empty).Trim().ToUpperInvariant() switch
    {
        "JPEG2000" or "J2K"          => DicomTransferSyntax.JPEG2000Lossless,
        "HTJ2K"                      => DicomTransferSyntax.HTJ2KLossless,
        "HTJ2KRPCL" or "HTJ2K-RPCL"  => DicomTransferSyntax.HTJ2KLosslessRPCL,
        _                            => DicomTransferSyntax.JPEGLSLossless,
    };

    public void Dispose() => _http.Dispose();
}
