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
using Microsoft.Extensions.Logging;

namespace Medisync.RisLocalGateway.Dicom;

/// <summary>
/// Forward 1 instance DICOM tới PACS qua STOW-RS (đi qua dicomweb-proxy):
///   1) (tuỳ chọn) transcode sang JPEG 2000 Lossless nếu ảnh đang uncompressed,
///   2) STOW multipart/related kèm Bearer token của gateway — dicomweb-proxy verify
///      token (iss=HIS, aud=LOCAL-GATEWAY-SERVER) rồi tự gắn label tenant cho study.
/// Cấu hình tại GatewayConfig.Pacs (StowUrl + Compression).
/// </summary>
public interface IPacsStowClient
{
    Task<bool> ForwardAsync(DicomFile file, CancellationToken ct = default);
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
        _http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
    }

    public async Task<bool> ForwardAsync(DicomFile file, CancellationToken ct = default)
    {
        var pacs = _configStore.Load().Pacs;
        if (string.IsNullOrWhiteSpace(pacs.StowUrl))
        {
            _logger.LogError("PACS chưa cấu hình (Pacs.StowUrl rỗng) — không forward được");
            return false;
        }

        var sop = file.Dataset.GetSingleValueOrDefault(DicomTag.SOPInstanceUID, "(none)");
        var timeoutSec = pacs.TimeoutSeconds > 0 ? pacs.TimeoutSeconds : 120;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(timeoutSec));
        var sendCt = cts.Token;

        // 1) Transcode JPEG 2000 Lossless nếu bật (luôn chỉ nén ảnh đang uncompressed).
        var toSend = MaybeCompress(file, pacs.CompressionEnabled, sop);

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
            _logger.LogError(ex, "Serialize DICOM lỗi sop={Sop}", sop);
            return false;
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
                return true;
            }

            var body = await resp.Content.ReadAsStringAsync(sendCt).ConfigureAwait(false);
            _logger.LogError("STOW ← {Code} {Reason} ({Ms}ms) sop={Sop} — {Body}",
                code, resp.ReasonPhrase, sw.ElapsedMilliseconds, sop, Truncate(body, 500));
            return false;
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            _logger.LogError("STOW timeout ({Sec}s) sop={Sop} → {Url}", timeoutSec, sop, pacs.StowUrl);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "STOW exception sop={Sop} → {Url}", sop, pacs.StowUrl);
            return false;
        }
    }

    /// <summary>Transcode sang JPEG 2000 Lossless nếu bật + ảnh đang uncompressed.</summary>
    private DicomFile MaybeCompress(DicomFile file, bool enabled, string sop)
    {
        if (!enabled) return file;

        var current = file.Dataset.InternalTransferSyntax;
        if (current.IsEncapsulated) // đã nén (kể cả JPEG 2000) → giữ nguyên, chỉ nén ảnh uncompressed
        {
            _logger.LogDebug("sop={Sop} đã nén ({Cur}) — giữ nguyên", sop, current.UID.Name);
            return file;
        }

        var target = DicomTransferSyntax.JPEG2000Lossless;
        try
        {
            var transcoded = new DicomTranscoder(current, target).Transcode(file);
            _logger.LogDebug("Transcode sop={Sop}: {From} → JPEG2000Lossless", sop, current.UID.Name);
            return transcoded;
        }
        catch (Exception ex)
        {
            // Lossless transcode lỗi (vd thiếu codec native) → forward bản gốc, không mất ảnh.
            _logger.LogWarning(ex, "Transcode sop={Sop} {From} → JPEG2000Lossless lỗi — forward bản gốc",
                sop, current.UID.Name);
            return file;
        }
    }

    private static string Truncate(string s, int max)
        => string.IsNullOrEmpty(s) ? string.Empty : (s.Length <= max ? s : s.Substring(0, max) + "…");

    public void Dispose() => _http.Dispose();
}
