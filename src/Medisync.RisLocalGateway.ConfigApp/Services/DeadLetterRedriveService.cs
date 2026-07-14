using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using FellowOakDicom;
using Medisync.RisLocalGateway.Core.Configuration;
using Medisync.RisLocalGateway.Core.Ris;
using Medisync.RisLocalGateway.Dicom;
using Medisync.RisLocalGateway.Dicom.Mappers;
using Medisync.RisLocalGateway.Dicom.Stats;
using Microsoft.Extensions.Logging.Abstractions;
using Serilog;

namespace Medisync.RisLocalGateway.ConfigApp.Services;

/// <summary>Tiến độ re-drive báo về UI.</summary>
public readonly record struct RedriveProgress(int Current, int Total, int Success, int Fail, string CurrentFile);

/// <summary>Kết quả tổng kết 1 lần re-drive.</summary>
public sealed record RedriveSummary(int Total, int Success, int Fail, bool StoppedEarly, string? StopReason);

/// <summary>
/// Đẩy lại (re-drive) các ảnh DICOM trong dead-letter (spool/failed) lên PACS. TÁI DÙNG
/// <see cref="PacsStowClient"/> + <see cref="HttpRisClient"/> nên transcode/STOW/phân loại lỗi
/// giống hệt SpoolForwarder của Service.
///
/// Đẩy TUẦN TỰ (1 ảnh/lần) → TỰ CHỐNG RACE tạo bản ghi cha ở Orthanc mà KHÔNG cần prime-lane
/// (race chỉ xảy ra khi forward SONG SONG như Service): mỗi ảnh gửi xong mới tới ảnh kế nên
/// ảnh đầu của study tạo Patient/Study/Series, các ảnh sau attach vào — không đua.
///
/// Study info: gửi RIS receive-study-info PER SERIES (dedup theo SeriesInstanceUID trong 1 lần
/// chạy). Với mỗi ảnh đẩy OK, nếu series CHƯA gửi → đọc snapshot study (patient + body part...) +
/// gửi RIS 1 lần (best-effort). Danh sách reset mỗi lần chạy → chạy lại có thể gọi RIS thừa,
/// nhưng HIS idempotent nên vô hại.
///
/// Đẩy lỗi <see cref="MaxConsecutiveFailures"/> file LIÊN TIẾP → dừng (PACS/mạng có thể đang lỗi).
/// Chạy trong tiến trình ConfigApp; KHÔNG đụng thư mục spool đang forward của Service (chỉ đọc failed).
/// </summary>
public sealed class DeadLetterRedriveService
{
    /// <summary>Số file lỗi LIÊN TIẾP tối đa trước khi dừng tự động.</summary>
    public const int MaxConsecutiveFailures = 10;

    private readonly ConfigStore _configStore;
    private readonly StudyStatsStore _stats;

    public DeadLetterRedriveService(ConfigStore configStore, StudyStatsStore stats)
    {
        _configStore = configStore;
        _stats = stats;
    }

    /// <summary>Thư mục dead-letter hiện hành (theo Dicom.StorageDirectory trong config).</summary>
    public string FailedDir => SpoolPaths.FailedDir(_configStore.Load().Dicom.StorageDirectory);

    private string FailedReasonsDir => SpoolPaths.FailedReasonsDir(_configStore.Load().Dicom.StorageDirectory);

    /// <summary>
    /// Liệt kê TÊN các file dead-letter — chỉ enumerate thư mục, KHÔNG mở file / KHÔNG đọc
    /// sidecar (rẻ kể cả khi dead-letter có hàng nghìn file).
    /// </summary>
    public IReadOnlyList<string> ListFailedFileNames()
    {
        var result = new List<string>();
        foreach (var path in ListFailedPaths())
        {
            result.Add(Path.GetFileName(path));
        }
        return result;
    }

    /// <summary>
    /// Đẩy lại tuần tự mọi file trong dead-letter. Báo tiến độ qua <paramref name="progress"/>.
    /// Ngừng ngay khi gặp <see cref="MaxConsecutiveFailures"/> lỗi liên tiếp (trả StoppedEarly=true).
    /// File đẩy thành công bị xoá khỏi failed + xoá sidecar; file lỗi giữ nguyên để thử lại sau.
    /// </summary>
    public async Task<RedriveSummary> RedriveAllAsync(IProgress<RedriveProgress> progress, CancellationToken ct)
    {
        var reasonsDir = FailedReasonsDir;
        var files = ListFailedPaths();
        int total = files.Count, success = 0, fail = 0, consecutiveFail = 0;

        if (total == 0)
        {
            Log.Information("Dead-letter re-drive: không có file nào để đẩy lại");
            return new RedriveSummary(0, 0, 0, false, null);
        }

        Log.Information("Dead-letter re-drive BẮT ĐẦU: {Total} file", total);

        // Client thật, dùng lại cơ chế token + STOW của gateway. Logger null (service tự log qua Serilog).
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        using var ris = new HttpRisClient(http, _configStore, NullLogger<HttpRisClient>.Instance);
        using var pacs = new PacsStowClient(ris, _configStore, NullLogger<PacsStowClient>.Instance);

        // Series đã gửi receive-study-info trong lần chạy này (theo SeriesInstanceUID) → gửi 1 lần/series.
        // Reset mỗi lần chạy: chạy lại có thể gọi RIS thừa nhưng HIS idempotent nên vô hại.
        var sentSeries = new HashSet<string>();

        for (int i = 0; i < total; i++)
        {
            ct.ThrowIfCancellationRequested();
            var path = files[i];
            var name = Path.GetFileName(path);
            progress.Report(new RedriveProgress(i, total, success, fail, name));

            var ok = await TryRedriveOneAsync(pacs, ris, path, reasonsDir, sentSeries, ct).ConfigureAwait(false);
            if (ok)
            {
                success++;
                consecutiveFail = 0;
            }
            else
            {
                fail++;
                consecutiveFail++;
                if (consecutiveFail >= MaxConsecutiveFailures)
                {
                    var reason = $"Đã dừng: {MaxConsecutiveFailures} file lỗi liên tiếp — PACS/mạng có thể đang lỗi. " +
                                 $"Đẩy OK {success}, lỗi {fail}/{total}. File lỗi vẫn ở dead-letter, thử lại sau khi PACS ổn.";
                    Log.Error("Dead-letter re-drive DỪNG SỚM — {Reason}", reason);
                    progress.Report(new RedriveProgress(i + 1, total, success, fail, name));
                    return new RedriveSummary(total, success, fail, true, reason);
                }
            }
            progress.Report(new RedriveProgress(i + 1, total, success, fail, name));
        }

        Log.Information("Dead-letter re-drive XONG: OK {Success}, lỗi {Fail}/{Total}", success, fail, total);
        return new RedriveSummary(total, success, fail, false, null);
    }

    private async Task<bool> TryRedriveOneAsync(
        IPacsStowClient pacs, IRisClient ris, string path, string reasonsDir,
        HashSet<string> sentSeries, CancellationToken ct)
    {
        DicomFile file;
        try
        {
            file = await DicomFile.OpenAsync(path).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Dead-letter mở file lỗi (bỏ qua): {File}", Path.GetFileName(path));
            return false;
        }

        ForwardResult result;
        try
        {
            result = await pacs.ForwardAsync(file, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Dead-letter forward exception: {File}", Path.GetFileName(path));
            return false;
        }

        if (result.Outcome != ForwardOutcome.Success)
        {
            Log.Warning("Dead-letter đẩy lại LỖI [{Outcome}] {File} — {Detail}",
                result.Outcome, Path.GetFileName(path), result.Detail);
            return false;
        }

        DeleteFileAndSidecar(path, reasonsDir);
        Log.Information("Dead-letter đẩy lại OK: {File}", Path.GetFileName(path));

        // Thống kê: instance FAILED → PUSHED (tab "Study đã nhận" tự giảm số dead-letter).
        _stats.TryRecord(StudyStatsEvent.FromDataset(StudyStatsEventKind.Pushed, file.Dataset));

        // Study info theo SERIES: lần ĐẦU thấy series này (trong lần chạy) → gửi RIS 1 lần.
        // Series đã đẩy OK ≥1 ảnh nên bản ghi series chắc chắn đã có ở Orthanc. sentSeries.Add
        // trả true = lần đầu → gửi; các ảnh sau cùng series trả false → bỏ qua (không gửi trùng).
        var seriesUid = file.Dataset.GetSingleValueOrDefault(DicomTag.SeriesInstanceUID, string.Empty);
        if (!string.IsNullOrEmpty(seriesUid) && sentSeries.Add(seriesUid))
        {
            await NotifyReceiveStudyInfoBestEffortAsync(ris, file.Dataset, ct).ConfigureAwait(false);
        }
        return true;
    }

    /// <summary>
    /// Đọc snapshot study từ ảnh + gửi RIS (receive-study-info). BEST-EFFORT: lỗi chỉ log warn,
    /// không retry, không ảnh hưởng kết quả re-drive ảnh (HIS idempotent nên lần sau có thể bù).
    /// Thiếu StudyInstanceUID → mapper trả null → bỏ qua, không gọi RIS.
    /// </summary>
    private static async Task NotifyReceiveStudyInfoBestEffortAsync(IRisClient ris, DicomDataset ds, CancellationToken ct)
    {
        var request = ImageDicomMapper.ToReceiveStudyInfoRequest(ds);
        if (request is null) return;

        try
        {
            var result = await ris.NotifyReceiveStudyInfoAsync(request, ct).ConfigureAwait(false);
            if (result.IsSuccess)
                Log.Information("Study info → RIS OK (study={Study} series={Series} bodyPart={BodyPart})",
                    request.StudyInstanceUid, request.SeriesInstanceUid, request.BodyPart);
            else
                Log.Warning("Study info → RIS FAIL HTTP {Code} — {Message} (study={Study} series={Series}) — bỏ qua",
                    result.HttpStatusCode, result.ErrorMessage, request.StudyInstanceUid, request.SeriesInstanceUid);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Study info → RIS exception (study={Study} series={Series}) — bỏ qua",
                request.StudyInstanceUid, request.SeriesInstanceUid);
        }
    }

    private List<string> ListFailedPaths()
    {
        var dir = FailedDir;
        if (!Directory.Exists(dir)) return new List<string>();
        try { return new List<string>(Directory.GetFiles(dir, "*.dcm", SearchOption.TopDirectoryOnly)); }
        catch (Exception ex) { Log.Warning(ex, "Liệt kê dead-letter lỗi: {Dir}", dir); return new List<string>(); }
    }

    private static void DeleteFileAndSidecar(string dcmPath, string reasonsDir)
    {
        try { if (File.Exists(dcmPath)) File.Delete(dcmPath); }
        catch (Exception ex) { Log.Warning(ex, "Xoá file dead-letter lỗi: {File}", dcmPath); }

        var sidecar = Path.Combine(reasonsDir, Path.GetFileNameWithoutExtension(dcmPath) + ".error.json");
        try { if (File.Exists(sidecar)) File.Delete(sidecar); }
        catch (Exception ex) { Log.Warning(ex, "Xoá sidecar dead-letter lỗi: {File}", sidecar); }
    }
}
