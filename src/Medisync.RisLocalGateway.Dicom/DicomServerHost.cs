using System;
using System.Threading;
using System.Threading.Tasks;
using FellowOakDicom;
using FellowOakDicom.Network;
using Medisync.RisLocalGateway.Core.Configuration;
using Microsoft.Extensions.Logging;

namespace Medisync.RisLocalGateway.Dicom;

public sealed class DicomServerHost : IAsyncDisposable
{
    private readonly ILogger<DicomServerHost> _logger;
    private IDicomServer? _server;

    public DicomServerHost(ILogger<DicomServerHost> logger)
    {
        _logger = logger;
    }

    public bool IsRunning => _server is { IsListening: true };

    public Task StartAsync(DicomConfig config, CancellationToken ct = default)
    {
        if (_server is not null)
        {
            _logger.LogWarning("DicomServer already started");
            return Task.CompletedTask;
        }

        _logger.LogInformation("Starting DICOM SCP: AET={Aet} Port={Port}", config.AeTitle, config.Port);

        _server = DicomServerFactory.Create<RisGatewayDicomProvider>(port: config.Port);

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct = default)
    {
        _server?.Stop();
        _server?.Dispose();
        _server = null;
        _logger.LogInformation("DICOM SCP stopped");
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        _server?.Dispose();
        _server = null;
        return ValueTask.CompletedTask;
    }
}
