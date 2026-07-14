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
    private readonly SemaphoreSlim _reloadLock = new(1, 1);    // serialize reload config (watcher event vs watchdog fallback)
    private FileSystemWatcher? _watcher;
    private GatewayConfig _currentConfig = GatewayConfig.CreateDefault();
    // LastWriteTimeUtc của config.json lần áp dụng gần nhất — watchdog so sánh để bắt
    // trường hợp FileSystemWatcher trượt event (ghi lúc service mới khởi động, buffer overflow).
    // Vì config giờ CACHE in-memory, miss event = kẹt config cũ vĩnh viễn nếu không có fallback này.
    private DateTime _lastConfigWriteUtc;

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
        _lastConfigWriteUtc = GetConfigWriteTimeUtc();
        await _dicomServer.StartAsync(_currentConfig.Dicom, stoppingToken).ConfigureAwait(false);

        SetupConfigWatcher();

        // Watchdog loop: định kỳ (1) kiểm tra SCP còn LISTEN không, tự dựng lại nếu chết ngầm;
        // (2) fallback bắt thay đổi config.json mà FileSystemWatcher trượt event.
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

                // Fallback reload: file đổi mà watcher không bắn (miss event) → vẫn nạp lại sau ≤30s.
                if (GetConfigWriteTimeUtc() != _lastConfigWriteUtc)
                {
                    _logger.LogInformation("Watchdog: phát hiện config.json thay đổi (watcher có thể đã trượt event) — nạp lại");
                    await ReloadAndApplyConfigAsync().ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
    }

    private static DateTime GetConfigWriteTimeUtc()
    {
        try { return File.GetLastWriteTimeUtc(ConfigPaths.ConfigFile); }
        catch { return DateTime.MinValue; }
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
            await ReloadAndApplyConfigAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to reload config");
        }
    }

    /// <summary>
    /// Đọc lại config.json từ đĩa (invalidate cache in-memory) + áp thay đổi (spool dir, restart SCP).
    /// Gọi từ 2 nơi: FileSystemWatcher event và watchdog fallback — serialize qua _reloadLock
    /// để 2 đường không đua nhau restart SCP / Reconfigure spool.
    /// </summary>
    private async Task ReloadAndApplyConfigAsync()
    {
        await _reloadLock.WaitAsync().ConfigureAwait(false);
        try
        {
            // Ghi nhận write-time TRƯỚC khi đọc: nếu file bị ghi tiếp ngay sau đó, write-time mới
            // sẽ khác → watchdog vòng sau reload lại (thà thừa còn hơn sót).
            var writeTimeUtc = GetConfigWriteTimeUtc();

            // BẮT BUỘC Reload() (không phải Load()): Load() trả bản cache CŨ nên so sánh old/new
            // sẽ trùng và không nhận ra thay đổi. Reload() đọc lỗi → giữ bản cũ (xem ConfigStore).
            var oldCfg = _currentConfig;
            var newCfg = _configStore.Reload();
            _lastConfigWriteUtc = writeTimeUtc;

            if (ReferenceEquals(newCfg, oldCfg))
            {
                // Reload thất bại giữ bản cũ (file đang bị writer giữ) — watchdog sẽ thử lại vòng sau.
                _logger.LogWarning("Config reload: chưa đọc được file (writer đang giữ?) — giữ config hiện hành, sẽ thử lại");
                _lastConfigWriteUtc = DateTime.MinValue; // ép watchdog vòng sau reload lại
                return;
            }

            var dicomChanged =
                newCfg.Dicom.AeTitle != oldCfg.Dicom.AeTitle ||
                newCfg.Dicom.Port != oldCfg.Dicom.Port;
            var storageChanged = !string.Equals(
                newCfg.Dicom.StorageDirectory, oldCfg.Dicom.StorageDirectory, StringComparison.OrdinalIgnoreCase);

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
        finally
        {
            _reloadLock.Release();
        }
    }
}
