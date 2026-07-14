using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Medisync.RisLocalGateway.ConfigApp.Services;

namespace Medisync.RisLocalGateway.ConfigApp.ViewModels;

/// <summary>
/// Tab "Đẩy lại ảnh lỗi": liệt kê tên file dead-letter (spool/failed) + re-drive lên PACS.
/// Chạy nền qua Task.Run, báo tiến độ (cần đẩy / thành công / lỗi) về UI qua Progress;
/// 10 lỗi liên tiếp thì tự dừng + báo đỏ (service tự ghi log).
/// </summary>
public partial class DeadLetterViewModel : ObservableObject
{
    private readonly DeadLetterRedriveService _service;
    private CancellationTokenSource? _cts;

    public DeadLetterViewModel(DeadLetterRedriveService service)
    {
        _service = service;
    }

    // Gán nguyên danh sách (1 lần notify) — dead-letter có thể hàng nghìn file.
    [ObservableProperty] private IReadOnlyList<string> _failedFiles = Array.Empty<string>();

    [ObservableProperty] private string _failedDirPath = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRedrive))]
    private int _failedCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRedrive))]
    private bool _isRunning;

    // === Tiến độ re-drive ===

    [ObservableProperty] private double _progressValue;
    [ObservableProperty] private double _progressMax = 1;
    [ObservableProperty] private int _totalToPush;   // tổng số file cần đẩy (chốt lúc bấm nút)
    [ObservableProperty] private int _successCount;  // đã đẩy thành công
    [ObservableProperty] private int _failCount;     // đã đẩy lỗi
    [ObservableProperty] private string _currentFileName = string.Empty;

    [ObservableProperty] private string _statusText = string.Empty;
    [ObservableProperty] private Brush _statusColor = Brushes.Black;

    public bool CanRedrive => !IsRunning && FailedCount > 0;

    /// <summary>Liệt kê lại tên file dead-letter (chỉ enumerate thư mục — rẻ, không mở file).</summary>
    [RelayCommand]
    private void Refresh()
    {
        try
        {
            FailedDirPath = _service.FailedDir;
            var list = _service.ListFailedFileNames();
            FailedFiles = list;
            FailedCount = list.Count;

            if (!IsRunning)
            {
                if (FailedCount == 0) SetStatus("Không có ảnh nào trong dead-letter. 🎉", ok: true);
                else SetStatus($"{FailedCount} ảnh đang ở dead-letter, chờ đẩy lại.", ok: true);
            }
        }
        catch (Exception ex)
        {
            SetStatus("Không đọc được dead-letter: " + ex.Message, ok: false);
        }
    }

    [RelayCommand]
    private async Task RedriveAllAsync()
    {
        if (IsRunning) return;
        IsRunning = true;
        _cts = new CancellationTokenSource();
        ProgressValue = 0;
        TotalToPush = FailedCount;
        SuccessCount = 0;
        FailCount = 0;
        CurrentFileName = string.Empty;
        SetStatus("Đang đẩy lại…", ok: true);

        try
        {
            var progress = new Progress<RedriveProgress>(p =>
            {
                ProgressMax = p.Total > 0 ? p.Total : 1;
                ProgressValue = p.Current;
                TotalToPush = p.Total;
                SuccessCount = p.Success;
                FailCount = p.Fail;
                CurrentFileName = p.CurrentFile;
            });

            var token = _cts.Token;
            var summary = await Task.Run(() => _service.RedriveAllAsync(progress, token), token).ConfigureAwait(true);

            if (summary.Total == 0)
                SetStatus("Không có ảnh nào trong dead-letter.", ok: true);
            else if (summary.StoppedEarly)
                SetStatus(summary.StopReason ?? "Đã dừng sớm.", ok: false);
            else if (summary.Fail == 0)
                SetStatus($"Hoàn tất: đẩy lại thành công toàn bộ {summary.Success}/{summary.Total} ảnh. ✅", ok: true);
            else
                SetStatus($"Hoàn tất: OK {summary.Success}, lỗi {summary.Fail}/{summary.Total}. Ảnh lỗi vẫn ở dead-letter.", ok: false);
        }
        catch (OperationCanceledException)
        {
            SetStatus("Đã hủy đẩy lại. Ảnh chưa đẩy vẫn ở dead-letter.", ok: false);
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "Dead-letter re-drive lỗi không mong đợi");
            SetStatus("Lỗi: " + ex.Message, ok: false);
        }
        finally
        {
            IsRunning = false;
            _cts?.Dispose();
            _cts = null;
            CurrentFileName = string.Empty;
            Refresh(); // ảnh đẩy OK đã bị xoá → cập nhật lại danh sách + count
        }
    }

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    [RelayCommand]
    private void OpenFailedFolder()
    {
        try
        {
            var dir = _service.FailedDir;
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = dir,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            SetStatus("Không mở được thư mục: " + ex.Message, ok: false);
        }
    }

    private void SetStatus(string message, bool ok)
    {
        StatusText = message;
        StatusColor = ok ? new SolidColorBrush(Color.FromRgb(0x2E, 0x7D, 0x32))
                         : new SolidColorBrush(Color.FromRgb(0xC6, 0x28, 0x28));
    }
}
