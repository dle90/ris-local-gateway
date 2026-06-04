using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Medisync.RisLocalGateway.Core.Ris.Models;

namespace Medisync.RisLocalGateway.Core.Ris;

/// <summary>
/// Client gọi RIS API. Implementations:
/// - HttpRisClient: gọi HTTP thật, có token cache + 401 retry
/// - StubRisClient: trả dữ liệu giả cho dev/test
/// </summary>
public interface IRisClient
{
    /// <summary>
    /// POST /v1/lgs/local-gateway-server/actions/get-work-list
    /// Lấy danh sách worklist từ HIS, gateway dùng để build C-FIND-RSP cho modality.
    /// </summary>
    Task<RisResponse<List<LgsGetWorkListResponse>>> FetchWorkListAsync(
        LgsGetWorkListRequest request,
        CancellationToken ct = default);

    /// <summary>
    /// POST /v1/lgs/local-gateway-server/actions/mpps-in-progress
    /// Forward DICOM MPPS N-CREATE (status="IN PROGRESS"). HIS tạo record mới
    /// trong diagnostic_imaging_mpps + update study status.
    /// </summary>
    Task<RisResponse> NotifyMppsInProgressAsync(
        LgsMppsInProgressRequest request,
        CancellationToken ct = default);

    /// <summary>
    /// POST /v1/lgs/local-gateway-server/actions/mpps-completed
    /// Forward DICOM MPPS N-SET (status="COMPLETED"). HIS update MPPS record +
    /// insert performed series + update study status.
    /// </summary>
    Task<RisResponse> NotifyMppsCompletedAsync(
        LgsMppsCompletedRequest request,
        CancellationToken ct = default);

    /// <summary>
    /// POST /v1/lgs/local-gateway-server/actions/mpps-discontinued
    /// Forward DICOM MPPS N-SET (status="DISCONTINUED"). HIS update MPPS record
    /// với reason + insert partial series (nếu có) + update study status.
    /// </summary>
    Task<RisResponse> NotifyMppsDiscontinuedAsync(
        LgsMppsDiscontinuedRequest request,
        CancellationToken ct = default);

    /// <summary>
    /// Access token Bearer hiện hành của gateway (đăng nhập ITG-LGS). Dùng cho STOW
    /// tới PACS: token mang iss=HIS, aud=LOCAL-GATEWAY-SERVER, companyUuid, facilityUuid
    /// nên dicomweb-proxy verify được và tự gắn label tenant cho study.
    /// Trả null nếu chưa đăng nhập được.
    /// </summary>
    Task<string?> GetAccessTokenAsync(CancellationToken ct = default);
}
