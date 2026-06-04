using System;
using System.Linq;
using System.ServiceProcess;
using System.Threading;
using System.Threading.Tasks;

namespace Medisync.RisLocalGateway.ConfigApp.Services;

public enum GatewayServiceState
{
    NotInstalled,
    Stopped,
    StartPending,
    Running,
    StopPending,
    Paused,
    Unknown,
}

public sealed class GatewayServiceManager
{
    private const string ServiceName = "Medisync RIS Local Gateway";

    public GatewayServiceState GetState()
    {
        try
        {
            using var sc = ServiceController.GetServices()
                .FirstOrDefault(s => string.Equals(s.ServiceName, ServiceName, StringComparison.OrdinalIgnoreCase)
                                  || string.Equals(s.DisplayName, ServiceName, StringComparison.OrdinalIgnoreCase));
            if (sc is null) return GatewayServiceState.NotInstalled;

            return sc.Status switch
            {
                ServiceControllerStatus.Stopped => GatewayServiceState.Stopped,
                ServiceControllerStatus.StartPending => GatewayServiceState.StartPending,
                ServiceControllerStatus.Running => GatewayServiceState.Running,
                ServiceControllerStatus.StopPending => GatewayServiceState.StopPending,
                ServiceControllerStatus.Paused => GatewayServiceState.Paused,
                _ => GatewayServiceState.Unknown,
            };
        }
        catch
        {
            return GatewayServiceState.Unknown;
        }
    }

    public async Task<(bool ok, string? error)> StartAsync(CancellationToken ct = default)
    {
        return await ChangeStateAsync(sc =>
        {
            if (sc.Status == ServiceControllerStatus.Running) return;
            sc.Start();
            sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
        }, ct);
    }

    public async Task<(bool ok, string? error)> StopAsync(CancellationToken ct = default)
    {
        return await ChangeStateAsync(sc =>
        {
            if (sc.Status == ServiceControllerStatus.Stopped) return;
            sc.Stop();
            sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
        }, ct);
    }

    public async Task<(bool ok, string? error)> RestartAsync(CancellationToken ct = default)
    {
        var stop = await StopAsync(ct);
        if (!stop.ok) return stop;
        await Task.Delay(500, ct);
        return await StartAsync(ct);
    }

    private async Task<(bool ok, string? error)> ChangeStateAsync(Action<ServiceController> action, CancellationToken ct)
    {
        try
        {
            await Task.Run(() =>
            {
                using var sc = new ServiceController(ServiceName);
                action(sc);
            }, ct);
            return (true, null);
        }
        catch (InvalidOperationException ex) when (ex.InnerException is System.ComponentModel.Win32Exception w32 && w32.NativeErrorCode == 5)
        {
            return (false, "Cần chạy ConfigApp as Administrator để điều khiển service.");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }
}
