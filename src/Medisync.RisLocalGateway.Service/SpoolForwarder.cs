using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FellowOakDicom;
using Medisync.RisLocalGateway.Core.Configuration;
using Medisync.RisLocalGateway.Dicom;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Medisync.RisLocalGateway.Service;

/// <summary>
/// Nền: quét spool, STOW tới PACS, có retry + dead-letter. Chống ĐUA tạo bản ghi cha của
/// Orthanc bằng PRIME-PER-SERIES:
///   • Mỗi series: gửi 1 instance đầu QUA "prime lane" (tuần tự toàn cục) + chờ thành công
///     → Orthanc tạo Patient/Study/Series.
///   • Sau đó instance còn lại của series gửi SONG SONG (parent đã có → không đua).
///   • Song song GIỮA các series khác nhau (mỗi series có gate prime riêng).
/// Trạng thái prime giữ in-memory trong SpoolStore (không persist → restart re-prime;
/// cleanup khi pendingCount=0 hoặc quá ExpireAt 30').
/// </summary>
public sealed class SpoolForwarder : BackgroundService
{
    private readonly IPacsStowClient _pacs;
    private readonly SpoolStore _spool;
    private readonly ConfigStore _configStore;
    private readonly ILogger<SpoolForwarder> _logger;

    private readonly ConcurrentDictionary<string, int> _attempts = new(); // path -> số lần fail (retry)
    private readonly SemaphoreSlim _primeLane = new(1, 1);                 // serialize MỌI prime → không 2 lần tạo parent đồng thời

    public SpoolForwarder(
        IPacsStowClient pacs,
        SpoolStore spool,
        ConfigStore configStore,
        ILogger<SpoolForwarder> logger)
    {
        _pacs = pacs;
        _spool = spool;
        _configStore = configStore;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("SpoolForwarder started (prime-per-series)");

        while (!stoppingToken.IsCancellationRequested)
        {
            var pacs = _configStore.Load().Pacs;
            var interval = TimeSpan.FromSeconds(pacs.ScanIntervalSeconds > 0 ? pacs.ScanIntervalSeconds : 15);
            var maxParallel = pacs.MaxParallelForwards > 0 ? pacs.MaxParallelForwards : 4;
            var maxRetries = pacs.MaxRetries > 0 ? pacs.MaxRetries : 10;

            try
            {
                var swept = _spool.SweepExpired(); // failsafe: dọn series quá hạn 30'
                if (swept > 0) _logger.LogDebug("SweepExpired: dọn {N} series quá hạn", swept);

                await ForwardScanAsync(maxParallel, maxRetries, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _logger.LogError(ex, "SpoolForwarder vòng quét lỗi"); }

            try { await Task.Delay(interval, stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }

        _logger.LogInformation("SpoolForwarder stopped");
    }

    private async Task ForwardScanAsync(int maxParallel, int maxRetries, CancellationToken ct)
    {
        var files = _spool.ListPending();
        if (files.Count == 0) return;

        // Gom theo series. File chưa biết series (leftover sau restart) → mở đọc 1 lần rồi Track.
        var groups = new Dictionary<string, List<string>>();
        foreach (var path in files)
        {
            var series = _spool.SeriesOf(path);
            if (series is null)
            {
                series = await DiscoverSeriesAsync(path).ConfigureAwait(false);
                _spool.Track(path, series);
            }
            if (!groups.TryGetValue(series, out var list)) { list = new List<string>(); groups[series] = list; }
            list.Add(path);
        }

        // Mỗi series xử lý độc lập, SONG SONG giữa các series.
        using var sem = new SemaphoreSlim(maxParallel);
        var tasks = groups.Select(g => ProcessSeriesAsync(g.Key, g.Value, sem, maxRetries, ct));
        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private async Task ProcessSeriesAsync(string series, List<string> paths, SemaphoreSlim sem, int maxRetries, CancellationToken ct)
    {
        // 1) PRIME nếu series chưa prime: gửi 1 instance qua lane tuần tự + chờ thành công.
        if (!_spool.IsPrimed(series))
        {
            var primed = await PrimeAsync(series, paths[0], maxRetries, ct).ConfigureAwait(false);
            // Prime fail (PACS down) → chờ scan sau. Nếu prime instance bị dead-letter → scan sau
            // file đó biến mất, paths[0] là instance khác → tự thử làm prime tiếp.
            if (!primed) return;
            paths = paths.Skip(1).ToList();
        }

        // 2) Phần còn lại của series: SONG SONG (giới hạn maxParallel).
        if (paths.Count == 0) return;
        var tasks = paths.Select(async p =>
        {
            await sem.WaitAsync(ct).ConfigureAwait(false);
            try { await ForwardOneAsync(p, maxRetries, ct).ConfigureAwait(false); }
            finally { sem.Release(); }
        });
        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private async Task<bool> PrimeAsync(string series, string path, int maxRetries, CancellationToken ct)
    {
        await _primeLane.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var ok = await ForwardOneAsync(path, maxRetries, ct).ConfigureAwait(false);
            if (ok)
            {
                _spool.MarkPrimed(series);
                _logger.LogDebug("Primed series {Series}", series);
            }
            return ok;
        }
        finally { _primeLane.Release(); }
    }

    /// <summary>
    /// STOW 1 file. OK → Delete (dọn state). Lỗi VĨNH VIỄN (ảnh bị từ chối / file hỏng) → dead-letter
    /// NGAY. Lỗi HẠ TẦNG tạm thời (PACS/RIS/mạng down) → retry tới cap an toàn cao (maxRetries) rồi
    /// mới dead-letter. Trả về true CHỈ khi forward thành công (để prime-gate biết series đã prime).
    /// </summary>
    private async Task<bool> ForwardOneAsync(string path, int maxRetries, CancellationToken ct)
    {
        DicomFile file;
        try
        {
            file = await DicomFile.OpenAsync(path).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Mở spool file lỗi (hỏng?) → dead-letter: {Path}", path);
            _spool.MoveToFailed(path, "Corrupt", 0, "Mở DICOM lỗi: " + ex.Message);
            _attempts.TryRemove(path, out _);
            return false;
        }

        ForwardResult result;
        try { result = await _pacs.ForwardAsync(file, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Forward spool file exception: {Path}", path);
            result = ForwardResult.Retryable("Forward exception: " + ex.Message);
        }

        if (result.Outcome == ForwardOutcome.Success)
        {
            _spool.Delete(path);
            _attempts.TryRemove(path, out _);
            return true;
        }

        // Lỗi VĨNH VIỄN (ảnh bị từ chối: 400/409/413/415/422, hoặc file hỏng) → dead-letter NGAY.
        if (result.Outcome == ForwardOutcome.PermanentError)
        {
            var prior = _attempts.TryGetValue(path, out var a) ? a : 0;
            _logger.LogError("Forward lỗi vĩnh viễn (ảnh bị từ chối) → dead-letter ngay: {Path} — {Detail}", path, result.Detail);
            _spool.MoveToFailed(path, "Permanent", prior, result.Detail);
            _attempts.TryRemove(path, out _);
            return false;
        }

        // Lỗi HẠ TẦNG tạm thời: KHÔNG dead-letter sớm — chỉ chuyển failed khi vượt cap an toàn rất
        // cao (maxRetries) để sự cố PACS/RIS/mạng thoáng qua không làm mất ảnh.
        var n = _attempts.AddOrUpdate(path, 1, (_, v) => v + 1);
        if (n >= maxRetries)
        {
            _logger.LogError("Forward lỗi hạ tầng {N} lần (vượt cap an toàn {Max}) → dead-letter: {Path} — {Detail}", n, maxRetries, path, result.Detail);
            _spool.MoveToFailed(path, "Retryable", n, result.Detail);
            _attempts.TryRemove(path, out _);
        }
        else
        {
            _logger.LogWarning("Forward lỗi hạ tầng lần {N}/{Max}, sẽ thử lại vòng sau (PACS/RIS tạm lỗi?): {Path} — {Detail}", n, maxRetries, path, result.Detail);
        }
        return false;
    }

    /// <summary>Đọc SeriesInstanceUID của file leftover (sau restart) để gom nhóm.</summary>
    private static async Task<string> DiscoverSeriesAsync(string path)
    {
        try
        {
            var f = await DicomFile.OpenAsync(path).ConfigureAwait(false);
            return f.Dataset.GetSingleValueOrDefault(DicomTag.SeriesInstanceUID, "(no-series)");
        }
        catch
        {
            return "(unreadable)"; // ForwardOne mở lại sẽ fail → dead-letter
        }
    }
}
