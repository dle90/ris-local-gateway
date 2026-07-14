using System;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Medisync.RisLocalGateway.ConfigApp.Services;
using Medisync.RisLocalGateway.Core.Configuration;
using Medisync.RisLocalGateway.Dicom.Stats;
using Microsoft.Extensions.Logging;
using Serilog.Extensions.Logging;

namespace Medisync.RisLocalGateway.ConfigApp.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly DispatcherTimer _statusTimer;
    private readonly DispatcherTimer _logTimer;
    private readonly StudyStatsStore _statsStore;

    public OverviewViewModel Overview { get; }
    public ConfigurationViewModel Configuration { get; }
    public ReceivedStudiesViewModel ReceivedStudies { get; }
    public DeadLetterViewModel DeadLetter { get; }
    public LogViewerViewModel LogViewer { get; }
    public AboutViewModel About { get; }

    public MainViewModel()
    {
        var configStore = new ConfigStore();
        var serviceManager = new GatewayServiceManager();
        var health = new HealthChecker();
        // Store thống kê dùng chung: tab "Study đã nhận" đọc; re-drive dead-letter ghi PUSHED.
        // enablePurge=false: dọn dữ liệu quá hạn là việc của Service, ConfigApp không purge.
        _statsStore = new StudyStatsStore(
            new SerilogLoggerFactory(Serilog.Log.Logger).CreateLogger<StudyStatsStore>(),
            enablePurge: false);
        var deadLetterService = new DeadLetterRedriveService(configStore, _statsStore);

        Overview = new OverviewViewModel(serviceManager, health, configStore);
        Configuration = new ConfigurationViewModel(configStore, health);
        ReceivedStudies = new ReceivedStudiesViewModel(_statsStore);
        DeadLetter = new DeadLetterViewModel(deadLetterService);
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
        DeadLetter.RefreshCommand.Execute(null);
        ReceivedStudies.RefreshCommand.Execute(null);
        await Overview.RefreshAsync();
        LogViewer.PollNewLines();

        _statusTimer.Start();
        _logTimer.Start();
    }

    public void Shutdown()
    {
        _statusTimer.Stop();
        _logTimer.Stop();
        _statsStore.Dispose(); // flush event PUSHED của re-drive còn trong queue
    }
}
