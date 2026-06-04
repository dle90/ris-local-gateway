using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Medisync.RisLocalGateway.Core.Configuration;
using Medisync.RisLocalGateway.Dicom;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Medisync.RisLocalGateway.Service;

public sealed class GatewayWorker : BackgroundService
{
    private readonly ILogger<GatewayWorker> _logger;
    private readonly ConfigStore _configStore;
    private readonly DicomServerHost _dicomServer;
    private FileSystemWatcher? _watcher;
    private GatewayConfig _currentConfig = GatewayConfig.CreateDefault();

    public GatewayWorker(
        ILogger<GatewayWorker> logger,
        ConfigStore configStore,
        DicomServerHost dicomServer)
    {
        _logger = logger;
        _configStore = configStore;
        _dicomServer = dicomServer;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("GatewayWorker starting");

        _currentConfig = _configStore.Load();
        await _dicomServer.StartAsync(_currentConfig.Dicom, stoppingToken).ConfigureAwait(false);

        SetupConfigWatcher();

        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("GatewayWorker stopping");
        _watcher?.Dispose();
        await _dicomServer.StopAsync(cancellationToken).ConfigureAwait(false);
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    private void SetupConfigWatcher()
    {
        var dir = ConfigPaths.BaseDirectory;
        var file = Path.GetFileName(ConfigPaths.ConfigFile);

        _watcher = new FileSystemWatcher(dir, file)
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.CreationTime,
            EnableRaisingEvents = true,
        };
        _watcher.Changed += OnConfigChanged;
        _watcher.Created += OnConfigChanged;
    }

    private async void OnConfigChanged(object sender, FileSystemEventArgs e)
    {
        try
        {
            // file might still be locked by writer; small delay
            await Task.Delay(300).ConfigureAwait(false);
            var newCfg = _configStore.Load();

            var dicomChanged =
                newCfg.Dicom.AeTitle != _currentConfig.Dicom.AeTitle ||
                newCfg.Dicom.Port != _currentConfig.Dicom.Port;

            _currentConfig = newCfg;

            if (dicomChanged)
            {
                _logger.LogInformation("DICOM config changed — restarting SCP");
                await _dicomServer.StopAsync().ConfigureAwait(false);
                await _dicomServer.StartAsync(_currentConfig.Dicom).ConfigureAwait(false);
            }
            else
            {
                _logger.LogInformation("Config reloaded (RIS settings only — no SCP restart)");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to reload config");
        }
    }
}
