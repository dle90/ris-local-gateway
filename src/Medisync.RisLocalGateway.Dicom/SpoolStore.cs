using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FellowOakDicom;
using Medisync.RisLocalGateway.Core.Configuration;
using Microsoft.Extensions.Logging;

namespace Medisync.RisLocalGateway.Dicom;

/// <summary>
/// Spool đĩa cho ảnh C-STORE chờ forward (STOW) + index IN-MEMORY để gom theo series và
/// quản lý trạng thái "prime" (đã tạo bản ghi cha ở Orthanc chưa).
///
/// State in-memory KHÔNG persist — restart thì dựng lại lazily (mỗi series mất ≤1 prime).
/// Cleanup state per-series có 2 cửa:
///   • pendingCount về 0 (đường chính: series đã gửi hết),
///   • quá ExpireAt = lúc-prime + 30' (failsafe chống leak nếu counter bị drift).
/// File gửi quá số lần → chuyển spool/failed (dead-letter).
/// </summary>
public sealed class SpoolStore
{
    private const string NoSeries = "(no-series)";
    private static readonly TimeSpan PrimeTtl = TimeSpan.FromMinutes(30);

    private readonly ILogger<SpoolStore> _logger;
    private readonly string _spoolDir;
    private readonly string _failedDir;

    // In-memory (mất khi restart — chấp nhận, dựng lại lazily):
    private readonly ConcurrentDictionary<string, string> _index = new();          // path -> seriesUid
    private readonly ConcurrentDictionary<string, int> _pending = new();            // seriesUid -> #instance còn pending
    private readonly ConcurrentDictionary<string, DateTimeOffset> _primed = new();  // seriesUid -> ExpireAt (CÓ MẶT = đã prime)

    public SpoolStore(ConfigStore configStore, ILogger<SpoolStore> logger)
    {
        _logger = logger;
        var storage = configStore.Load().Dicom.StorageDirectory;
        var root = string.IsNullOrWhiteSpace(storage)
            ? Path.Combine(ConfigPaths.BaseDirectory, "spool")
            : Path.Combine(storage, "spool");
        _spoolDir = root;
        _failedDir = Path.Combine(root, "failed");
        Directory.CreateDirectory(_spoolDir);
        Directory.CreateDirectory(_failedDir);
        _logger.LogInformation("Spool dir: {Dir}", _spoolDir);
    }

    // ---------- Disk + enqueue ----------

    /// <summary>Ghi 1 instance vào spool (atomic .tmp→rename) + ghi nhận series vào index. Trả path.</summary>
    public async Task<string> EnqueueAsync(DicomFile file)
    {
        var series = file.Dataset.GetSingleValueOrDefault(DicomTag.SeriesInstanceUID, NoSeries);
        var sop = file.Dataset.GetSingleValueOrDefault(DicomTag.SOPInstanceUID, Guid.NewGuid().ToString("N"));
        var dest = Path.Combine(_spoolDir, Sanitize(sop) + ".dcm");
        var tmp = dest + ".tmp";
        await file.SaveAsync(tmp).ConfigureAwait(false);
        File.Move(tmp, dest, overwrite: true); // trùng SOP (re-send) → ghi đè
        Track(dest, series);
        return dest;
    }

    public IReadOnlyList<string> ListPending()
    {
        try { return Directory.GetFiles(_spoolDir, "*.dcm", SearchOption.TopDirectoryOnly); }
        catch (Exception ex) { _logger.LogError(ex, "List spool lỗi"); return Array.Empty<string>(); }
    }

    // ---------- In-memory: group theo series + prime state ----------

    /// <summary>
    /// Gắn file vào series (đếm pending nếu là path MỚI). Dùng cả lúc enqueue lẫn lúc
    /// forwarder phát hiện file leftover sau restart.
    /// </summary>
    public void Track(string path, string? series)
    {
        if (string.IsNullOrEmpty(series)) series = NoSeries;
        if (_index.TryAdd(path, series))
            _pending.AddOrUpdate(series, 1, (_, v) => v + 1);
        else
            _index[path] = series; // đã đếm rồi
    }

    public string? SeriesOf(string path) => _index.TryGetValue(path, out var s) ? s : null;

    public bool IsPrimed(string series) => _primed.ContainsKey(series);

    /// <summary>Đánh dấu series đã prime + đặt ExpireAt = now + 30' (reset mỗi lần prime lại).</summary>
    public void MarkPrimed(string series) => _primed[series] = DateTimeOffset.UtcNow + PrimeTtl;

    /// <summary>Failsafe chống leak: xoá state của series đã quá ExpireAt. Trả số series bị dọn.</summary>
    public int SweepExpired()
    {
        var now = DateTimeOffset.UtcNow;
        var n = 0;
        foreach (var kv in _primed)
        {
            if (kv.Value <= now)
            {
                _primed.TryRemove(kv.Key, out _);
                _pending.TryRemove(kv.Key, out _); // bỏ luôn counter (chống drift)
                n++;
            }
        }
        return n;
    }

    // ---------- Hoàn tất 1 instance (forward OK / dead-letter) ----------

    /// <summary>Forward thành công: xoá file khỏi spool + dọn state.</summary>
    public void Delete(string path)
    {
        OnRemoved(path);
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) { _logger.LogWarning(ex, "Xoá spool file lỗi: {Path}", path); }
    }

    /// <summary>Dead-letter: chuyển sang spool/failed + dọn state.</summary>
    public void MoveToFailed(string path)
    {
        OnRemoved(path);
        try
        {
            var dest = Path.Combine(_failedDir, Path.GetFileName(path));
            File.Move(path, dest, overwrite: true);
            _logger.LogError("Spool → FAILED (dead-letter): {Dest}", dest);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Move to failed lỗi: {Path}", path); }
    }

    /// <summary>Bỏ index của path; trừ pendingCount; về 0 thì dọn sạch state series (cửa cleanup chính).</summary>
    private void OnRemoved(string path)
    {
        if (!_index.TryRemove(path, out var series)) return;
        var n = _pending.AddOrUpdate(series, 0, (_, v) => v - 1);
        if (n <= 0)
        {
            _pending.TryRemove(series, out _);
            _primed.TryRemove(series, out _);
        }
    }

    private static string Sanitize(string s)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(s.Select(c => Array.IndexOf(invalid, c) >= 0 ? '_' : c).ToArray());
    }
}
