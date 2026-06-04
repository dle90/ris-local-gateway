using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Medisync.RisLocalGateway.Core.Ris.Models;
using Microsoft.Extensions.Logging;

namespace Medisync.RisLocalGateway.Core.Ris;

/// <summary>
/// Stub implementation cho dev/test khi chưa có RIS thật. Trả empty hoặc fake data.
/// </summary>
public sealed class StubRisClient : IRisClient
{
    private readonly ILogger<StubRisClient> _logger;

    public StubRisClient(ILogger<StubRisClient> logger)
    {
        _logger = logger;
    }

    public Task<RisResponse<List<LgsGetWorkListResponse>>> FetchWorkListAsync(
        LgsGetWorkListRequest request,
        CancellationToken ct = default)
    {
        _logger.LogInformation("[stub] FetchWorkListAsync modality={Modality} ae={Ae} date={Date}",
            request.Modality, request.ScheduledStationAeTitle, request.ScheduledProcedureStepStartDate);

        // STUB: trả empty worklist. Khi nối RIS thật → HttpRisClient sẽ thay thế.
        return Task.FromResult(RisResponse<List<LgsGetWorkListResponse>>.Success(new List<LgsGetWorkListResponse>()));
    }

    public Task<RisResponse> NotifyMppsInProgressAsync(
        LgsMppsInProgressRequest request,
        CancellationToken ct = default)
    {
        _logger.LogInformation("[stub] NotifyMppsInProgressAsync sop={Sop} study={Study} aet={Aet}",
            request.SopInstanceUid, request.StudyInstanceUid, request.PerformedStationAeTitle);
        return Task.FromResult(RisResponse.Success());
    }

    public Task<RisResponse> NotifyMppsCompletedAsync(
        LgsMppsCompletedRequest request,
        CancellationToken ct = default)
    {
        _logger.LogInformation("[stub] NotifyMppsCompletedAsync sop={Sop} series={Count}",
            request.SopInstanceUid, request.PerformedSeries.Count);
        return Task.FromResult(RisResponse.Success());
    }

    public Task<RisResponse> NotifyMppsDiscontinuedAsync(
        LgsMppsDiscontinuedRequest request,
        CancellationToken ct = default)
    {
        _logger.LogInformation("[stub] NotifyMppsDiscontinuedAsync sop={Sop} reason={Reason}",
            request.SopInstanceUid, request.Reason);
        return Task.FromResult(RisResponse.Success());
    }

    public Task<string?> GetAccessTokenAsync(CancellationToken ct = default)
    {
        // Stub: không có token thật → STOW sẽ gửi không kèm Bearer (chỉ chạy nếu proxy auth off).
        _logger.LogWarning("[stub] GetAccessTokenAsync — trả null (chưa nối RIS thật)");
        return Task.FromResult<string?>(null);
    }
}
