using System;
using System.Threading.Tasks;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Medisync.RisLocalGateway.ConfigApp.Services;
using Medisync.RisLocalGateway.Core.Configuration;

namespace Medisync.RisLocalGateway.ConfigApp.ViewModels;

/// <summary>
/// Cấu hình TÀI KHOẢN kết nối RIS (username + mật khẩu) trong window riêng — giống window endpoint.
/// Base URL nằm ở window "Cấu hình endpoint API"; ở đây chỉ hiển thị Base URL read-only để biết
/// "Kiểm tra tài khoản" đăng nhập tới đâu.
/// </summary>
public partial class RisAccountViewModel : ObservableObject
{
    private readonly ConfigStore _store;
    private readonly HealthChecker _health;

    public RisAccountViewModel(ConfigStore store, HealthChecker health)
    {
        _store = store;
        _health = health;
    }

    [ObservableProperty] private string _username = string.Empty;
    [ObservableProperty] private string _plainPassword = string.Empty;
    [ObservableProperty] private string _baseUrlInfo = string.Empty; // read-only: Base URL từ config (đặt ở window endpoint)

    [ObservableProperty] private string _status = string.Empty;
    [ObservableProperty] private Brush _statusColor = Brushes.Black;
    [ObservableProperty] private bool _shouldClose;
    [ObservableProperty] private bool _isTestingSignIn;

    public void Load()
    {
        try
        {
            var cfg = _store.Load();
            Username = cfg.Ris.Username;
            PlainPassword = SecretProtector.Unprotect(cfg.Ris.ProtectedPassword);
            BaseUrlInfo = string.IsNullOrWhiteSpace(cfg.Ris.BaseUrl)
                ? "(Base URL chưa cấu hình — đặt ở \"Cấu hình endpoint API\")"
                : cfg.Ris.BaseUrl;
            SetStatus("Đã tải tài khoản.", ok: true);
        }
        catch (Exception ex)
        {
            SetStatus("Không tải được: " + ex.Message, ok: false);
        }
    }

    [RelayCommand]
    private void Save()
    {
        try
        {
            // Mutate cfg.Ris IN-PLACE → giữ nguyên BaseUrl + Endpoints (không reset).
            var cfg = _store.Load();
            cfg.Ris.Username = Username?.Trim() ?? string.Empty;
            cfg.Ris.ProtectedPassword = SecretProtector.Protect(PlainPassword ?? string.Empty);
            _store.Save(cfg);
            SetStatus("Đã lưu tài khoản RIS. Service sẽ tự nạp lại.", ok: true);
            ShouldClose = true;
        }
        catch (Exception ex)
        {
            SetStatus("Lưu thất bại: " + ex.Message, ok: false);
        }
    }

    [RelayCommand]
    private async Task TestSignInAsync()
    {
        try
        {
            IsTestingSignIn = true;
            if (string.IsNullOrWhiteSpace(Username))
            {
                SetStatus("Hãy nhập tài khoản trước khi kiểm tra.", ok: false);
                return;
            }
            if (string.IsNullOrWhiteSpace(PlainPassword))
            {
                SetStatus("Hãy nhập mật khẩu trước khi kiểm tra.", ok: false);
                return;
            }

            // Base URL + SignIn path lấy từ config đã lưu (đặt ở window endpoint).
            var cfg = _store.Load();
            var baseUrl = cfg.Ris.BaseUrl;
            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                SetStatus("Base URL chưa cấu hình — đặt ở \"Cấu hình endpoint API\" và lưu trước khi kiểm tra.", ok: false);
                return;
            }
            var signInPath = cfg.Ris.Endpoints?.SignIn;
            if (string.IsNullOrWhiteSpace(signInPath))
            {
                signInPath = RisEndpoints.CreateDefault().SignIn;
            }

            SetStatus("Đang kiểm tra đăng nhập…", ok: true);

            var (ok, message) = await _health.ProbeSignInAsync(
                baseUrl.Trim(),
                signInPath,
                Username.Trim(),
                PlainPassword ?? string.Empty);

            SetStatus(message, ok: ok);
        }
        finally
        {
            IsTestingSignIn = false;
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        ShouldClose = true;
    }

    private void SetStatus(string message, bool ok)
    {
        Status = message;
        StatusColor = ok ? new SolidColorBrush(Color.FromRgb(0x2E, 0x7D, 0x32))
                         : new SolidColorBrush(Color.FromRgb(0xC6, 0x28, 0x28));
    }
}
