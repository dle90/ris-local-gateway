using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Medisync.RisLocalGateway.Dicom.Stats;

namespace Medisync.RisLocalGateway.ConfigApp.ViewModels;

/// <summary>
/// Tab "Study đã nhận": danh sách study đã gửi ảnh tới gateway (đọc từ SQLite thống kê local
/// do Service ghi), filter theo khoảng ngày nhận. Query chạy nền qua Task.Run — WAL cho phép
/// đọc trong khi Service đang ghi.
/// </summary>
public partial class ReceivedStudiesViewModel : ObservableObject
{
    private readonly StudyStatsStore _stats;

    public ReceivedStudiesViewModel(StudyStatsStore stats)
    {
        _stats = stats;
        var today = DateTime.Today;
        _fromDate = today;
        _toDate = today;
    }

    [ObservableProperty] private DateTime? _fromDate;
    [ObservableProperty] private DateTime? _toDate;

    // Gán nguyên danh sách (1 lần notify) — ngày bận có thể hàng trăm study.
    [ObservableProperty] private IReadOnlyList<StudyStatsRow> _rows = Array.Empty<StudyStatsRow>();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    private bool _isLoading;

    [ObservableProperty] private string _statusText = string.Empty;
    [ObservableProperty] private Brush _statusColor = Brushes.Black;

    private bool CanRefresh => !IsLoading;

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private async Task RefreshAsync()
    {
        var from = (FromDate ?? DateTime.Today).Date;
        var toExclusive = (ToDate ?? DateTime.Today).Date.AddDays(1);
        if (toExclusive <= from)
        {
            SetStatus("Khoảng ngày không hợp lệ: 'Từ ngày' phải ≤ 'Đến ngày'.", ok: false);
            return;
        }

        IsLoading = true;
        try
        {
            var rows = await Task.Run(() =>
                _stats.QueryStudies(new DateTimeOffset(from), new DateTimeOffset(toExclusive)));
            Rows = rows;
            SetStatus($"{rows.Count} study nhận ảnh từ {from:dd/MM/yyyy} đến {toExclusive.AddDays(-1):dd/MM/yyyy}.",
                ok: true);
        }
        catch (Exception ex)
        {
            Rows = Array.Empty<StudyStatsRow>();
            SetStatus("Không đọc được dữ liệu thống kê: " + ex.Message, ok: false);
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void SetStatus(string message, bool ok)
    {
        StatusText = message;
        StatusColor = ok ? new SolidColorBrush(Color.FromRgb(0x2E, 0x7D, 0x32))
                         : new SolidColorBrush(Color.FromRgb(0xC6, 0x28, 0x28));
    }
}
