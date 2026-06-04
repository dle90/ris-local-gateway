using System.Text.Json.Serialization;

namespace Medisync.RisLocalGateway.Core.Ris.Models;

/// <summary>
/// 1 item trong (0040,0340) PerformedSeriesSequence. Dùng chung cho cả
/// MPPS COMPLETED và DISCONTINUED requests (ca dừng giữa chừng vẫn có
/// thể đã sinh series).
/// </summary>
public sealed class LgsMppsPerformedSeriesItem
{
    [JsonPropertyName("retrieveAeTitle")]
    public string? RetrieveAeTitle { get; set; }

    [JsonPropertyName("seriesDescription")]
    public string? SeriesDescription { get; set; }

    [JsonPropertyName("seriesInstanceUid")]
    public string SeriesInstanceUid { get; set; } = string.Empty;

    [JsonPropertyName("performedProcedureStepStartDate")]
    public string? PerformedProcedureStepStartDate { get; set; }

    [JsonPropertyName("modality")]
    public string? Modality { get; set; }

    [JsonPropertyName("protocolName")]
    public string? ProtocolName { get; set; }
}
