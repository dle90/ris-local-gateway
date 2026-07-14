using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Text.Json;
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
    private static readonly JsonSerializerOptions FailureJsonOptions = new() { WriteIndented = true };

    private readonly ILogger<SpoolStore> _logger;
    private string _spoolDir;
    private string _failedDir;
    private string _failedReasonDir;

    // In-memory (mất khi restart — chấp nhận, dựng lại lazily):
    private readonly ConcurrentDictionary<string, string> _index = new();          // path -> seriesUid
    private readonly ConcurrentDictionary<string, int> _pending = new();            // seriesUid -> #instance còn pending
    private readonly ConcurrentDictionary<string, DateTimeOffset> _primed = new();  // seriesUid -> ExpireAt (CÓ MẶT = đã prime)

    public SpoolStore(ConfigStore configStore, ILogger<SpoolStore> logger)
    {
        _logger = logger;
        ApplyStorage(configStore.Load().Dicom.StorageDirectory);
    }

    [MemberNotNull(nameof(_spoolDir), nameof(_failedDir), nameof(_failedReasonDir))]
    private void ApplyStorage(string? storageDirectory)
    {
        _spoolDir = SpoolPaths.ResolveRoot(storageDirectory);
        _failedDir = SpoolPaths.FailedDir(storageDirectory);
        _failedReasonDir = SpoolPaths.FailedReasonsDir(storageDirectory);
        Directory.CreateDirectory(_spoolDir);
        Directory.CreateDirectory(_failedDir);
        Directory.CreateDirectory(_failedReasonDir);
        _logger.LogInformation("Spool dir: {Dir} (failed: {Failed}, reasons: {Reasons})",
            _spoolDir, _failedDir, _failedReasonDir);
    }

    /// <summary>
    /// Đổi thư mục spool khi config (Dicom.StorageDirectory) thay đổi lúc đang chạy — vì SpoolStore
    /// là singleton, đọc dir 1 lần lúc khởi tạo. File ĐANG nằm ở dir CŨ sẽ KHÔNG tự di chuyển
    /// (forwarder chỉ quét dir mới) → cảnh báo để admin tự copy/drain dir cũ nếu còn file.
    /// </summary>
    public void Reconfigure(string? storageDirectory)
    {
        var newRoot = SpoolPaths.ResolveRoot(storageDirectory);
        if (string.Equals(newRoot, _spoolDir, StringComparison.OrdinalIgnoreCase)) return;

        var old = _spoolDir;
        var leftover = 0;
        try { leftover = Directory.GetFiles(old, "*.dcm", SearchOption.TopDirectoryOnly).Length; }
        catch { /* dir cũ có thể đã bị xoá — bỏ qua */ }

        ApplyStorage(storageDirectory);
        _logger.LogWarning(
            "StorageDirectory đổi: spool {Old} → {New}. {N} file còn ở dir CŨ sẽ KHÔNG tự forward — " +
            "copy thủ công sang dir mới nếu cần.", old, _spoolDir, leftover);
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

    /// <summary>
    /// Dead-letter: chuyển .dcm sang spool/failed + ghi sidecar lý do fail sang spool/failed-reasons
    /// (folder RIÊNG, không lẫn .dcm) + dọn state.
    /// </summary>
    public void MoveToFailed(string path, string category, int attempts, string? reason)
    {
        var series = SeriesOf(path); // lấy series TRƯỚC khi OnRemoved xoá index
        OnRemoved(path);
        try
        {
            var fileName = Path.GetFileName(path);
            var dest = Path.Combine(_failedDir, fileName);
            File.Move(path, dest, overwrite: true);
            WriteFailureSidecar(fileName, category, attempts, reason, series);
            _logger.LogError("Spool → FAILED (dead-letter) [{Category}]: {Dest}", category, dest);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Move to failed lỗi: {Path}", path); }
    }

    /// <summary>Ghi lý do dead-letter ra spool/failed-reasons/&lt;sop&gt;.error.json (tách khỏi file .dcm).</summary>
    private void WriteFailureSidecar(string failedFileName, string category, int attempts, string? reason, string? series)
    {
        try
        {
            var baseName = Path.GetFileNameWithoutExtension(failedFileName);
            var sidecar = Path.Combine(_failedReasonDir, baseName + ".error.json");
            var info = new SpoolFailureSidecar
            {
                File = failedFileName,
                Category = category,
                Attempts = attempts,
                Series = series,
                Reason = reason ?? string.Empty,
                FailedAtUtc = DateTimeOffset.UtcNow.ToString("o"),
            };
            File.WriteAllText(sidecar, JsonSerializer.Serialize(info, FailureJsonOptions));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Ghi sidecar lý do fail lỗi cho {File}", failedFileName);
        }
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
