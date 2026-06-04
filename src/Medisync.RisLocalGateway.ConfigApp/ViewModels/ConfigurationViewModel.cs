using System;
using System.Threading.Tasks;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Medisync.RisLocalGateway.ConfigApp.Services;
using Medisync.RisLocalGateway.ConfigApp.Views;
using Medisync.RisLocalGateway.Core.Configuration;

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

    // === RIS section ===

    [ObservableProperty] private string _baseUrl = string.Empty;
    [ObservableProperty] private string _username = string.Empty;
    [ObservableProperty] private string _plainPassword = string.Empty;
    [ObservableProperty] private string _risStatus = string.Empty;
    [ObservableProperty] private Brush _risStatusColor = Brushes.Black;

    // === DICOM section ===

    [ObservableProperty] private string _aeTitle = "RIS_GW";
    [ObservableProperty] private int _port = 11112;
    [ObservableProperty] private string _storageDirectory = string.Empty;
    [ObservableProperty] private string _dicomStatus = string.Empty;
    [ObservableProperty] private Brush _dicomStatusColor = Brushes.Black;

    // === PACS section (STOW forward) ===

    [ObservableProperty] private string _pacsStowUrl = string.Empty;
    [ObservableProperty] private int _pacsTimeoutSeconds = 120;
    [ObservableProperty] private bool _pacsCompressionEnabled = true;
    [ObservableProperty] private int _pacsScanIntervalSeconds = 15;
    [ObservableProperty] private int _pacsMaxRetries = 10;
    [ObservableProperty] private int _pacsMaxParallelForwards = 4;
    [ObservableProperty] private string _pacsStatus = string.Empty;
    [ObservableProperty] private Brush _pacsStatusColor = Brushes.Black;

    public void Load()
    {
        try
        {
            var cfg = _store.Load();
            BaseUrl = cfg.Ris.BaseUrl;
            Username = cfg.Ris.Username;
            PlainPassword = SecretProtector.Unprotect(cfg.Ris.ProtectedPassword);
            AeTitle = cfg.Dicom.AeTitle;
            Port = cfg.Dicom.Port;
            StorageDirectory = cfg.Dicom.StorageDirectory;
            SetRisStatus("Đã tải cấu hình.", ok: true);
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
            SetRisStatus("Không tải được cấu hình: " + ex.Message, ok: false);
            SetDicomStatus("Không tải được cấu hình: " + ex.Message, ok: false);
            SetPacsStatus("Không tải được cấu hình: " + ex.Message, ok: false);
        }
    }

    [RelayCommand]
    private void SaveRis()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(BaseUrl))
            {
                SetRisStatus("Base URL không được trống.", ok: false);
                return;
            }

            var normalizedUrl = NormalizeUrl(BaseUrl.Trim());
            if (normalizedUrl != BaseUrl.Trim())
            {
                BaseUrl = normalizedUrl;
            }

            // Đọc file hiện tại, chỉ replace RIS section → tránh ghi đè DICOM nếu user
            // đang edit dở DICOM section mà chưa save.
            var cfg = _store.Load();
            cfg.Ris = new RisConfig
            {
                BaseUrl = normalizedUrl,
                Username = Username.Trim(),
                ProtectedPassword = SecretProtector.Protect(PlainPassword ?? string.Empty),
            };

            _store.Save(cfg);
            SetRisStatus("Đã lưu cấu hình RIS. Service sẽ tự nạp lại.", ok: true);
        }
        catch (Exception ex)
        {
            SetRisStatus("Lưu thất bại: " + ex.Message, ok: false);
        }
    }

    [ObservableProperty] private bool _isTestingSignIn;

    [RelayCommand]
    private async Task TestSignInAsync()
    {
        try
        {
            IsTestingSignIn = true;
            if (string.IsNullOrWhiteSpace(BaseUrl))
            {
                SetRisStatus("Hãy nhập Base URL trước khi kiểm tra.", ok: false);
                return;
            }
            if (string.IsNullOrWhiteSpace(Username))
            {
                SetRisStatus("Hãy nhập tài khoản trước khi kiểm tra.", ok: false);
                return;
            }
            if (string.IsNullOrWhiteSpace(PlainPassword))
            {
                SetRisStatus("Hãy nhập mật khẩu trước khi kiểm tra.", ok: false);
                return;
            }

            // SignIn path từ saved config (vì edit ở window endpoints riêng).
            // Nếu chưa cấu hình → dùng default.
            var cfg = _store.Load();
            var signInPath = cfg.Ris.Endpoints?.SignIn;
            if (string.IsNullOrWhiteSpace(signInPath))
            {
                signInPath = RisEndpoints.CreateDefault().SignIn;
            }

            SetRisStatus("Đang kiểm tra đăng nhập…", ok: true);

            var (ok, message) = await _health.ProbeSignInAsync(
                BaseUrl.Trim(),
                signInPath,
                Username.Trim(),
                PlainPassword ?? string.Empty);

            SetRisStatus(message, ok: ok);
        }
        finally
        {
            IsTestingSignIn = false;
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
    }

    [RelayCommand]
    private void ReloadRis()
    {
        try
        {
            var cfg = _store.Load();
            BaseUrl = cfg.Ris.BaseUrl;
            Username = cfg.Ris.Username;
            PlainPassword = SecretProtector.Unprotect(cfg.Ris.ProtectedPassword);
            SetRisStatus("Đã tải lại cấu hình RIS từ file.", ok: true);
        }
        catch (Exception ex)
        {
            SetRisStatus("Tải lại thất bại: " + ex.Message, ok: false);
        }
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

            var cfg = _store.Load();
            cfg.Dicom = new DicomConfig
            {
                AeTitle = AeTitle.Trim(),
                Port = Port,
                StorageDirectory = StorageDirectory?.Trim() ?? string.Empty,
            };

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
            var cfg = _store.Load();
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
            var url = NormalizeUrl(PacsStowUrl.Trim());
            if (url != PacsStowUrl.Trim()) PacsStowUrl = url;
            if (PacsTimeoutSeconds < 1)
            {
                SetPacsStatus("Timeout phải > 0 giây.", ok: false);
                return;
            }

            // Chỉ replace PACS section, giữ nguyên RIS/DICOM.
            var cfg = _store.Load();
            cfg.Pacs = new PacsConfig
            {
                StowUrl = url,
                TimeoutSeconds = PacsTimeoutSeconds,
                ScanIntervalSeconds = PacsScanIntervalSeconds > 0 ? PacsScanIntervalSeconds : 15,
                MaxRetries = PacsMaxRetries > 0 ? PacsMaxRetries : 10,
                MaxParallelForwards = PacsMaxParallelForwards > 0 ? PacsMaxParallelForwards : 4,
                CompressionEnabled = PacsCompressionEnabled,
            };

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
            var cfg = _store.Load();
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

    private void SetRisStatus(string message, bool ok)
    {
        RisStatus = message;
        RisStatusColor = ok ? GreenBrush() : RedBrush();
    }

    private void SetDicomStatus(string message, bool ok)
    {
        DicomStatus = message;
        DicomStatusColor = ok ? GreenBrush() : RedBrush();
    }

    private static SolidColorBrush GreenBrush() => new(Color.FromRgb(0x2E, 0x7D, 0x32));
    private static SolidColorBrush RedBrush() => new(Color.FromRgb(0xC6, 0x28, 0x28));

    private static string NormalizeUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return url;
        if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return url;
        }
        return "http://" + url;
    }
}
