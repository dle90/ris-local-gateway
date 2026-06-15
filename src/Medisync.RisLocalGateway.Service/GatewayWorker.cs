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
    // SCP có thể chết ngầm (exception trong fo-dicom server, lỗi I/O cổng) mà tiến trình vẫn sống.
    // Watchdog quét mỗi WatchdogInterval và tự dựng lại listener nếu phát hiện không còn LISTEN.
    private static readonly TimeSpan WatchdogInterval = TimeSpan.FromSeconds(30);

    private readonly ILogger<GatewayWorker> _logger;
    private readonly ConfigStore _configStore;
    private readonly DicomServerHost _dicomServer;
    private readonly SpoolStore _spool;
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1); // serialize Start/Stop SCP (watchdog vs config-reload)
    private FileSystemWatcher? _watcher;
    private GatewayConfig _currentConfig = GatewayConfig.CreateDefault();

    public GatewayWorker(
        ILogger<GatewayWorker> logger,
        ConfigStore configStore,
        DicomServerHost dicomServer,
        SpoolStore spool)
    {
        _logger = logger;
        _configStore = configStore;
        _dicomServer = dicomServer;
        _spool = spool;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("GatewayWorker starting");

        _currentConfig = _configStore.Load();
        await _dicomServer.StartAsync(_currentConfig.Dicom, stoppingToken).ConfigureAwait(false);

        SetupConfigWatcher();

        // Watchdog loop: định kỳ kiểm tra SCP còn LISTEN không, tự dựng lại nếu chết ngầm.
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(WatchdogInterval, stoppingToken).ConfigureAwait(false);
                if (stoppingToken.IsCancellationRequested) break;

                if (!_dicomServer.IsRunning)
                {
                    _logger.LogWarning("Watchdog: DICOM SCP không còn LISTEN — đang khởi động lại");
                    try
                    {
                        await RestartScpAsync(stoppingToken).ConfigureAwait(false);
                        _logger.LogInformation("Watchdog: SCP đã khởi động lại");
                    }
                    catch (OperationCanceledException) { break; }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Watchdog: khởi động lại SCP thất bại — sẽ thử lại vòng sau");
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
    }

    /// <summary>Dừng rồi khởi động lại SCP, serialize qua _lifecycleLock để watchdog và config-reload không đua.</summary>
    private async Task RestartScpAsync(CancellationToken ct = default)
    {
        await _lifecycleLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _dicomServer.StopAsync(ct).ConfigureAwait(false);
            await _dicomServer.StartAsync(_currentConfig.Dicom, ct).ConfigureAwait(false);
        }
        finally
        {
            _lifecycleLock.Release();
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
            var storageChanged = !string.Equals(
                newCfg.Dicom.StorageDirectory, _currentConfig.Dicom.StorageDirectory, StringComparison.OrdinalIgnoreCase);

            _currentConfig = newCfg;

            if (storageChanged)
            {
                _logger.LogInformation("StorageDirectory thay đổi — cập nhật thư mục spool");
                _spool.Reconfigure(_currentConfig.Dicom.StorageDirectory);
            }

            if (dicomChanged)
            {
                _logger.LogInformation("DICOM config changed — restarting SCP");
                await RestartScpAsync().ConfigureAwait(false);
            }
            else
            {
                _logger.LogInformation("Config reloaded (no SCP restart)");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to reload config");
        }
    }
}
