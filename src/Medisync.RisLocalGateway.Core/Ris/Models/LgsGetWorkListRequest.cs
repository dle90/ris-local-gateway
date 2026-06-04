using System.Text.Json.Serialization;

namespace Medisync.RisLocalGateway.Core.Ris.Models;

/// <summary>
/// Request body cho POST /v1/lgs/local-gateway-server/actions/get-work-list.
/// Match LgsGetWorkListRequest trong his-core (10 field theo DICOM C-FIND MWL matching keys).
/// </summary>
public sealed class LgsGetWorkListRequest
{
    // === Patient ===

    [JsonPropertyName("patientName")]
    public string? PatientName { get; set; }

    [JsonPropertyName("patientId")]
    public string? PatientId { get; set; }

    // === Imaging Service Request ===

    [JsonPropertyName("accessionNumber")]
    public string? AccessionNumber { get; set; }

    [JsonPropertyName("studyInstanceUid")]
    public string? StudyInstanceUid { get; set; }

    // === Requested Procedure ===

    [JsonPropertyName("requestedProcedureId")]
    public string? RequestedProcedureId { get; set; }

    // === Scheduled Procedure Step ===

    [JsonPropertyName("scheduledStationAeTitle")]
    public string ScheduledStationAeTitle { get; set; } = string.Empty;

    [JsonPropertyName("scheduledProcedureStepStartDate")]
    public string? ScheduledProcedureStepStartDate { get; set; }

    [JsonPropertyName("scheduledProcedureStepStartTime")]
    public string? ScheduledProcedureStepStartTime { get; set; }

    [JsonPropertyName("modality")]
    public string? Modality { get; set; }

    [JsonPropertyName("scheduledProcedureStepId")]
    public string? ScheduledProcedureStepId { get; set; }
}
