using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Medisync.RisLocalGateway.Core.Ris.Models;

/// <summary>
/// Request body cho POST /v1/lgs/local-gateway-server/actions/mpps-discontinued.
/// Gateway gửi khi ca chụp bị hủy (DICOM MPPS N-SET với status="DISCONTINUED").
/// Match LgsMppsDiscontinuedRequest trong his-core.
/// </summary>
public sealed class LgsMppsDiscontinuedRequest
{
    [JsonPropertyName("sopInstanceUid")]
    public string SopInstanceUid { get; set; } = string.Empty;

    [JsonPropertyName("endDate")]
    public string EndDate { get; set; } = string.Empty;

    [JsonPropertyName("endTime")]
    public string EndTime { get; set; } = string.Empty;

    [JsonPropertyName("reason")]
    public string? Reason { get; set; }

    [JsonPropertyName("reasonCode")]
    public LgsMppsDiscontinuedRequestDiscontinuationReasonCode? ReasonCode { get; set; }

    [JsonPropertyName("performedSeries")]
    public List<LgsMppsPerformedSeriesItem> PerformedSeries { get; set; } = new();
}

/// <summary>
/// 1 item trong (0008,0114) Procedure Discontinuation Reason Code Sequence.
/// DICOM coded entry chuẩn (PS3.3 §8.8).
/// </summary>
public sealed class LgsMppsDiscontinuedRequestDiscontinuationReasonCode
{
    [JsonPropertyName("codeValue")]
    public string CodeValue { get; set; } = string.Empty;

    [JsonPropertyName("codingSchemeDesignator")]
    public string CodingSchemeDesignator { get; set; } = string.Empty;

    [JsonPropertyName("codeMeaning")]
    public string CodeMeaning { get; set; } = string.Empty;
}
