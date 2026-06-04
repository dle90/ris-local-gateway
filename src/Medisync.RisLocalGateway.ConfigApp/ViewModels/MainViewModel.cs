using System;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Medisync.RisLocalGateway.ConfigApp.Services;
using Medisync.RisLocalGateway.Core.Configuration;

namespace Medisync.RisLocalGateway.ConfigApp.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly DispatcherTimer _statusTimer;
    private readonly DispatcherTimer _logTimer;

    public OverviewViewModel Overview { get; }
    public ConfigurationViewModel Configuration { get; }
    public LogViewerViewModel LogViewer { get; }
    public AboutViewModel About { get; }

    public MainViewModel()
    {
        var configStore = new ConfigStore();
        var serviceManager = new GatewayServiceManager();
        var health = new HealthChecker();

        Overview = new OverviewViewModel(serviceManager, health, configStore);
        Configuration = new ConfigurationViewModel(configStore, health);
        LogViewer = new LogViewerViewModel();
        About = new AboutViewModel();

        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _statusTimer.Tick += async (_, _) => await Overview.RefreshAsync();

        _logTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
        _logTimer.Tick += (_, _) => LogViewer.PollNewLines();
    }

    public async void Initialize()
    {
        Configuration.Load();
        await Overview.RefreshAsync();
        LogViewer.PollNewLines();

        _statusTimer.Start();
        _logTimer.Start();
    }

    public void Shutdown()
    {
        _statusTimer.Stop();
        _logTimer.Stop();
    }
}
