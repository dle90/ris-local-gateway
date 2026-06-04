using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Medisync.RisLocalGateway.Core.Ris.Models;

/// <summary>
/// Request body cho POST /v1/lgs/local-gateway-server/actions/mpps-completed.
/// Gateway gửi khi modality báo chụp xong (DICOM MPPS N-SET với status="COMPLETED").
/// Match LgsMppsCompletedRequest trong his-core.
/// </summary>
public sealed class LgsMppsCompletedRequest
{
    [JsonPropertyName("sopInstanceUid")]
    public string SopInstanceUid { get; set; } = string.Empty;

    [JsonPropertyName("endDate")]
    public string EndDate { get; set; } = string.Empty;

    [JsonPropertyName("endTime")]
    public string EndTime { get; set; } = string.Empty;

    [JsonPropertyName("performedSeries")]
    public List<LgsMppsPerformedSeriesItem> PerformedSeries { get; set; } = new();
}
