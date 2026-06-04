using System;
using System.Threading.Tasks;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Medisync.RisLocalGateway.ConfigApp.Services;
using Medisync.RisLocalGateway.Core.Configuration;

namespace Medisync.RisLocalGateway.ConfigApp.ViewModels;

public partial class OverviewViewModel : ObservableObject
{
    private readonly GatewayServiceManager _serviceManager;
    private readonly HealthChecker _health;
    private readonly ConfigStore _configStore;

    public OverviewViewModel(GatewayServiceManager serviceManager, HealthChecker health, ConfigStore configStore)
    {
        _serviceManager = serviceManager;
        _health = health;
        _configStore = configStore;
    }

    // === Service status ===

    [ObservableProperty] private string _serviceStateText = "Đang kiểm tra…";
    [ObservableProperty] private Brush _serviceStateColor = Brushes.Gray;
    [ObservableProperty] private bool _canStart;
    [ObservableProperty] private bool _canStop;
    [ObservableProperty] private bool _canRestart;
    [ObservableProperty] private string _serviceName = "Medisync RIS Local Gateway";

    // === DICOM listener ===

    [ObservableProperty] private string _dicomListenerText = "—";
    [ObservableProperty] private Brush _dicomListenerColor = Brushes.Gray;
    [ObservableProperty] private string _aeTitle = "RIS_GW";
    [ObservableProperty] private int _port = 11112;

    // Trạng thái listen lần trước — chỉ log ERROR khi CHUYỂN sang fail (tránh spam mỗi 5s).
    private bool? _lastDicomListening;

    // === RIS connection ===

    [ObservableProperty] private string _risUrl = "(chưa cấu hình)";
    [ObservableProperty] private string _risStatusText = "—";
    [ObservableProperty] private Brush _risStatusColor = Brushes.Gray;
    [ObservableProperty] private string _risLastChecked = "—";

    // === PACS connection ===

    [ObservableProperty] private string _pacsUrl = "(chưa cấu hình)";
    [ObservableProperty] private string _pacsStatusText = "—";
    [ObservableProperty] private Brush _pacsStatusColor = Brushes.Gray;
    [ObservableProperty] private string _pacsLastChecked = "—";

    // === Action message ===

    [ObservableProperty] private string _actionMessage = string.Empty;
    [ObservableProperty] private Brush _actionMessageColor = Brushes.Black;

    public async Task RefreshAsync()
    {
        var config = _configStore.Load();
        AeTitle = config.Dicom.AeTitle;
        Port = config.Dicom.Port;
        RisUrl = string.IsNullOrWhiteSpace(config.Ris.BaseUrl) ? "(chưa cấu hình)" : config.Ris.BaseUrl;
        PacsUrl = string.IsNullOrWhiteSpace(config.Pacs.StowUrl) ? "(chưa cấu hình)" : config.Pacs.StowUrl;

        // Service state
        var state = _serviceManager.GetState();
        UpdateServiceStateUi(state);

        // DICOM listener probe. Listen OK → KHÔNG ghi log. Listen fail → log ERROR rõ ràng,
        // chỉ khi CHUYỂN trạng thái (tránh spam mỗi 5s khi gateway down lâu).
        var (listening, msg) = await _health.ProbeDicomListenerAsync(config.Dicom.Port).ConfigureAwait(true);
        DicomListenerText = msg;
        DicomListenerColor = listening ? GreenBrush() : RedBrush();
        if (!listening && _lastDicomListening != false)
        {
            Serilog.Log.Error("DICOM Listener KHÔNG nghe — AE Title {AeTitle} - Port {Port} — {Detail}",
                config.Dicom.AeTitle, config.Dicom.Port, msg);
        }
        _lastDicomListening = listening;
    }

    [RelayCommand]
    private async Task TestRisAsync()
    {
        var config = _configStore.Load();
        if (string.IsNullOrWhiteSpace(config.Ris.BaseUrl))
        {
            RisStatusText = "Hãy nhập Base URL ở tab Cấu hình";
            RisStatusColor = RedBrush();
            return;
        }

        RisStatusText = "Đang kiểm tra…";
        RisStatusColor = Brushes.Gray;
        var (ok, latency, message) = await _health.ProbeRisAsync(config.Ris.BaseUrl).ConfigureAwait(true);
        RisStatusText = message;
        RisStatusColor = ok ? GreenBrush() : RedBrush();
        RisLastChecked = DateTime.Now.ToString("HH:mm:ss");
    }

    [RelayCommand]
    private async Task TestPacsAsync()
    {
        var config = _configStore.Load();
        if (string.IsNullOrWhiteSpace(config.Pacs.StowUrl))
        {
            PacsStatusText = "Hãy nhập STOW URL ở tab Cấu hình";
            PacsStatusColor = RedBrush();
            return;
        }

        PacsStatusText = "Đang kiểm tra…";
        PacsStatusColor = Brushes.Gray;
        var (ok, latency, message) = await _health.ProbePacsAsync(config.Pacs.StowUrl).ConfigureAwait(true);
        PacsStatusText = message;
        PacsStatusColor = ok ? GreenBrush() : RedBrush();
        PacsLastChecked = DateTime.Now.ToString("HH:mm:ss");
    }

    [RelayCommand]
    private async Task StartServiceAsync()
    {
        ActionMessage = "Đang khởi động service…";
        ActionMessageColor = Brushes.Gray;
        var (ok, error) = await _serviceManager.StartAsync().ConfigureAwait(true);
        if (ok)
        {
            ActionMessage = "Đã khởi động service.";
            ActionMessageColor = GreenBrush();
        }
        else
        {
            ActionMessage = $"Khởi động thất bại: {error}";
            ActionMessageColor = RedBrush();
        }
        await RefreshAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task StopServiceAsync()
    {
        ActionMessage = "Đang dừng service…";
        ActionMessageColor = Brushes.Gray;
        var (ok, error) = await _serviceManager.StopAsync().ConfigureAwait(true);
        if (ok)
        {
            ActionMessage = "Đã dừng service.";
            ActionMessageColor = GreenBrush();
        }
        else
        {
            ActionMessage = $"Dừng thất bại: {error}";
            ActionMessageColor = RedBrush();
        }
        await RefreshAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task RestartServiceAsync()
    {
        ActionMessage = "Đang khởi động lại service…";
        ActionMessageColor = Brushes.Gray;
        var (ok, error) = await _serviceManager.RestartAsync().ConfigureAwait(true);
        if (ok)
        {
            ActionMessage = "Đã khởi động lại service.";
            ActionMessageColor = GreenBrush();
        }
        else
        {
            ActionMessage = $"Restart thất bại: {error}";
            ActionMessageColor = RedBrush();
        }
        await RefreshAsync().ConfigureAwait(true);
    }

    private void UpdateServiceStateUi(GatewayServiceState state)
    {
        switch (state)
        {
            case GatewayServiceState.Running:
                ServiceStateText = "🟢 Đang chạy";
                ServiceStateColor = GreenBrush();
                CanStart = false; CanStop = true; CanRestart = true;
                break;
            case GatewayServiceState.Stopped:
                ServiceStateText = "🔴 Đã dừng";
                ServiceStateColor = RedBrush();
                CanStart = true; CanStop = false; CanRestart = false;
                break;
            case GatewayServiceState.StartPending:
                ServiceStateText = "⏳ Đang khởi động…";
                ServiceStateColor = Brushes.DarkOrange;
                CanStart = false; CanStop = false; CanRestart = false;
                break;
            case GatewayServiceState.StopPending:
                ServiceStateText = "⏳ Đang dừng…";
                ServiceStateColor = Brushes.DarkOrange;
                CanStart = false; CanStop = false; CanRestart = false;
                break;
            case GatewayServiceState.NotInstalled:
                ServiceStateText = "⚠️ Service chưa được cài (chạy dev mode?)";
                ServiceStateColor = Brushes.DarkOrange;
                CanStart = false; CanStop = false; CanRestart = false;
                break;
            default:
                ServiceStateText = "❓ Không xác định";
                ServiceStateColor = Brushes.Gray;
                CanStart = false; CanStop = false; CanRestart = false;
                break;
        }
    }

    private static SolidColorBrush GreenBrush() => new(Color.FromRgb(0x2E, 0x7D, 0x32));
    private static SolidColorBrush RedBrush() => new(Color.FromRgb(0xC6, 0x28, 0x28));
}
