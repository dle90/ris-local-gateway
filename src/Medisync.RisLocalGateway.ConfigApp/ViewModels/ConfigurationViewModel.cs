using System;
using System.Threading.Tasks;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Medisync.RisLocalGateway.ConfigApp.Services;
using Medisync.RisLocalGateway.ConfigApp.Views;
using Medisync.RisLocalGateway.Core.Configuration;
using Medisync.RisLocalGateway.Core.Ris;

namespace Medisync.RisLocalGateway.ConfigApp.ViewModels;

public partial class ConfigurationViewModel : ObservableObject
{
    private readonly ConfigStore _store;
    private readonly HealthChecker _health;

    public ConfigurationViewModel(ConfigStore store, HealthChecker health)
    {
        _store = store;
        _health = health;
    }

    // === RIS section — chỉ summary read-only; chỉnh sửa ở 2 window riêng:
    //   • Base URL + endpoint API → EndpointsWindow
    //   • Tài khoản (username/mật khẩu) → RisAccountWindow ===

    [ObservableProperty] private string _risBaseUrlSummary = string.Empty;
    [ObservableProperty] private string _risUsernameSummary = string.Empty;

    // === DICOM section ===

    [ObservableProperty] private string _aeTitle = "RIS_GW";
    [ObservableProperty] private int _port = 4646;
    [ObservableProperty] private string _storageDirectory = string.Empty;
    [ObservableProperty] private string _dicomStatus = string.Empty;
    [ObservableProperty] private Brush _dicomStatusColor = Brushes.Black;

    // === PACS section (STOW forward) ===

    [ObservableProperty] private string _pacsStowUrl = string.Empty;
    [ObservableProperty] private int _pacsTimeoutSeconds = 120;
    [ObservableProperty] private bool _pacsCompressionEnabled = true;
    [ObservableProperty] private int _pacsScanIntervalSeconds = 15;
    [ObservableProperty] private int _pacsMaxRetries = 1000; // khớp default PacsConfig.MaxRetries
    [ObservableProperty] private int _pacsMaxParallelForwards = 4;
    [ObservableProperty] private string _pacsStatus = string.Empty;
    [ObservableProperty] private Brush _pacsStatusColor = Brushes.Black;

    public void Load()
    {
        try
        {
            var cfg = _store.Load();
            ApplyRisSummary(cfg);
            AeTitle = cfg.Dicom.AeTitle;
            Port = cfg.Dicom.Port;
            StorageDirectory = cfg.Dicom.StorageDirectory;
            SetDicomStatus("Đã tải cấu hình.", ok: true);

            PacsStowUrl = cfg.Pacs.StowUrl;
            PacsTimeoutSeconds = cfg.Pacs.TimeoutSeconds;
            PacsCompressionEnabled = cfg.Pacs.CompressionEnabled;
            PacsScanIntervalSeconds = cfg.Pacs.ScanIntervalSeconds;
            PacsMaxRetries = cfg.Pacs.MaxRetries;
            PacsMaxParallelForwards = cfg.Pacs.MaxParallelForwards;
            SetPacsStatus("Đã tải cấu hình.", ok: true);
        }
        catch (Exception ex)
        {
            SetDicomStatus("Không tải được cấu hình: " + ex.Message, ok: false);
            SetPacsStatus("Không tải được cấu hình: " + ex.Message, ok: false);
        }
    }

    [RelayCommand]
    private void OpenEndpoints()
    {
        var vm = new EndpointsViewModel(_store);
        var window = new EndpointsWindow(vm)
        {
            Owner = System.Windows.Application.Current.MainWindow,
        };
        window.ShowDialog();
        RefreshRisSummary();
    }

    [RelayCommand]
    private void OpenRisAccount()
    {
        var vm = new RisAccountViewModel(_store, _health);
        var window = new RisAccountWindow(vm)
        {
            Owner = System.Windows.Application.Current.MainWindow,
        };
        window.ShowDialog();
        RefreshRisSummary();
    }

    private void RefreshRisSummary()
    {
        try { ApplyRisSummary(_store.Load()); }
        catch { /* giữ summary cũ nếu đọc lỗi */ }
    }

    private void ApplyRisSummary(GatewayConfig cfg)
    {
        RisBaseUrlSummary = string.IsNullOrWhiteSpace(cfg.Ris.BaseUrl) ? "(chưa cấu hình)" : cfg.Ris.BaseUrl;
        RisUsernameSummary = string.IsNullOrWhiteSpace(cfg.Ris.Username) ? "(chưa cấu hình)" : cfg.Ris.Username;
    }

    [RelayCommand]
    private void SaveDicom()
    {
        try
        {
            if (Port is < 1 or > 65535)
            {
                SetDicomStatus("Port DICOM phải trong khoảng 1..65535.", ok: false);
                return;
            }
            if (string.IsNullOrWhiteSpace(AeTitle) || AeTitle.Length > 16)
            {
                SetDicomStatus("AE Title phải có 1..16 ký tự.", ok: false);
                return;
            }

            // LoadCopy: sửa trên bản copy — cache in-memory chỉ đổi khi Save thành công.
            var cfg = _store.LoadCopy();
            cfg.Dicom.AeTitle = AeTitle.Trim();
            cfg.Dicom.Port = Port;
            cfg.Dicom.StorageDirectory = StorageDirectory?.Trim() ?? string.Empty;

            _store.Save(cfg);
            SetDicomStatus("Đã lưu cấu hình DICOM. SCP sẽ restart nếu AE/Port đổi.", ok: true);
        }
        catch (Exception ex)
        {
            SetDicomStatus("Lưu thất bại: " + ex.Message, ok: false);
        }
    }

    [RelayCommand]
    private void ReloadDicom()
    {
        try
        {
            var cfg = _store.Reload(); // ép đọc lại từ file (bỏ chỉnh sửa chưa lưu)
            AeTitle = cfg.Dicom.AeTitle;
            Port = cfg.Dicom.Port;
            StorageDirectory = cfg.Dicom.StorageDirectory;
            SetDicomStatus("Đã tải lại cấu hình DICOM từ file.", ok: true);
        }
        catch (Exception ex)
        {
            SetDicomStatus("Tải lại thất bại: " + ex.Message, ok: false);
        }
    }

    [RelayCommand]
    private void BrowseStorageDirectory()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Chọn thư mục lưu DICOM (spool)",
        };
        if (!string.IsNullOrWhiteSpace(StorageDirectory) && System.IO.Directory.Exists(StorageDirectory))
        {
            dlg.InitialDirectory = StorageDirectory;
        }
        if (dlg.ShowDialog() == true)
        {
            StorageDirectory = dlg.FolderName;
        }
    }

    [RelayCommand]
    private void SavePacs()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(PacsStowUrl))
            {
                SetPacsStatus("STOW URL không được trống.", ok: false);
                return;
            }
            var url = UrlHelper.NormalizeBaseUrl(PacsStowUrl.Trim());
            if (url != PacsStowUrl.Trim()) PacsStowUrl = url;
            if (PacsTimeoutSeconds < 1)
            {
                SetPacsStatus("Timeout phải > 0 giây.", ok: false);
                return;
            }

            // LoadCopy + mutate TỪNG field UI quản lý — GIỮ NGUYÊN các field không có trên UI
            // (CompressionCodec, Htj2kModalities admin chỉnh tay trong config.json) và RIS/DICOM.
            var cfg = _store.LoadCopy();
            cfg.Pacs.StowUrl = url;
            cfg.Pacs.TimeoutSeconds = PacsTimeoutSeconds;
            cfg.Pacs.ScanIntervalSeconds = PacsScanIntervalSeconds > 0 ? PacsScanIntervalSeconds : 15;
            cfg.Pacs.MaxRetries = PacsMaxRetries > 0 ? PacsMaxRetries : 1000;
            cfg.Pacs.MaxParallelForwards = PacsMaxParallelForwards > 0 ? PacsMaxParallelForwards : 4;
            cfg.Pacs.CompressionEnabled = PacsCompressionEnabled;

            _store.Save(cfg);
            SetPacsStatus("Đã lưu cấu hình PACS. Service sẽ tự nạp lại.", ok: true);
        }
        catch (Exception ex)
        {
            SetPacsStatus("Lưu thất bại: " + ex.Message, ok: false);
        }
    }

    [RelayCommand]
    private void ReloadPacs()
    {
        try
        {
            var cfg = _store.Reload(); // ép đọc lại từ file (bỏ chỉnh sửa chưa lưu)
            PacsStowUrl = cfg.Pacs.StowUrl;
            PacsTimeoutSeconds = cfg.Pacs.TimeoutSeconds;
            PacsCompressionEnabled = cfg.Pacs.CompressionEnabled;
            PacsScanIntervalSeconds = cfg.Pacs.ScanIntervalSeconds;
            PacsMaxRetries = cfg.Pacs.MaxRetries;
            PacsMaxParallelForwards = cfg.Pacs.MaxParallelForwards;
            SetPacsStatus("Đã tải lại cấu hình PACS từ file.", ok: true);
        }
        catch (Exception ex)
        {
            SetPacsStatus("Tải lại thất bại: " + ex.Message, ok: false);
        }
    }

    private void SetPacsStatus(string message, bool ok)
    {
        PacsStatus = message;
        PacsStatusColor = ok ? GreenBrush() : RedBrush();
    }

    private void SetDicomStatus(string message, bool ok)
    {
        DicomStatus = message;
        DicomStatusColor = ok ? GreenBrush() : RedBrush();
    }

    private static SolidColorBrush GreenBrush() => new(Color.FromRgb(0x2E, 0x7D, 0x32));
    private static SolidColorBrush RedBrush() => new(Color.FromRgb(0xC6, 0x28, 0x28));
}
