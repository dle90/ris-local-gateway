using System.Text.Json.Serialization;

namespace Medisync.RisLocalGateway.Core.Ris.Models;

/// <summary>
/// Request body cho POST /v1/lgs/local-gateway-server/actions/mpps-in-progress.
/// Gateway gửi lên HIS khi modality báo bắt đầu chụp (DICOM MPPS N-CREATE
/// với status="IN PROGRESS"). Match LgsMppsInProgressRequest trong his-core.
/// </summary>
public sealed class LgsMppsInProgressRequest
{
    // === MPPS identity ===

    [JsonPropertyName("sopInstanceUid")]
    public string SopInstanceUid { get; set; } = string.Empty;

    [JsonPropertyName("performedProcedureStepId")]
    public string? PerformedProcedureStepId { get; set; }

    // === Linkage tới Scheduled Procedure Step ===

    [JsonPropertyName("scheduledProcedureStepId")]
    public string? ScheduledProcedureStepId { get; set; }

    [JsonPropertyName("accessionNumber")]
    public string? AccessionNumber { get; set; }

    [JsonPropertyName("studyInstanceUid")]
    public string StudyInstanceUid { get; set; } = string.Empty;

    // === Time ===

    [JsonPropertyName("startDate")]
    public string StartDate { get; set; } = string.Empty;

    [JsonPropertyName("startTime")]
    public string StartTime { get; set; } = string.Empty;

    // === Modality info ===

    [JsonPropertyName("modality")]
    public string? Modality { get; set; }

    [JsonPropertyName("performedStationAeTitle")]
    public string? PerformedStationAeTitle { get; set; }

    [JsonPropertyName("performedStationName")]
    public string? PerformedStationName { get; set; }

    // === Description ===

    [JsonPropertyName("performedProcedureStepDescription")]
    public string? PerformedProcedureStepDescription { get; set; }

    [JsonPropertyName("performedProcedureTypeDescription")]
    public string? PerformedProcedureTypeDescription { get; set; }

    // === Patient snapshot ===

    [JsonPropertyName("patientId")]
    public string? PatientId { get; set; }

    [JsonPropertyName("patientName")]
    public string? PatientName { get; set; }

    [JsonPropertyName("patientBirthDate")]
    public string? PatientBirthDate { get; set; }

    [JsonPropertyName("patientSex")]
    public string? PatientSex { get; set; }
}
