using System.Text.Json.Serialization;

namespace Medisync.RisLocalGateway.Core.Ris.Models;

/// <summary>
/// 1 worklist item trong response của POST /v1/lgs/local-gateway-server/actions/get-work-list.
/// Match LgsGetWorkListResponse trong his-core (response = mảng các item này).
/// Mỗi field tương ứng với 1 DICOM tag để gateway build C-FIND-RSP gửi cho modality.
/// </summary>
public sealed class LgsGetWorkListResponse
{
    // === Patient ===

    [JsonPropertyName("patientId")]
    public string PatientId { get; set; } = string.Empty;

    [JsonPropertyName("patientName")]
    public string PatientName { get; set; } = string.Empty;

    [JsonPropertyName("patientBirthDate")]
    public string? PatientBirthDate { get; set; }

    [JsonPropertyName("patientSex")]
    public string? PatientSex { get; set; }

    [JsonPropertyName("patientWeight")]
    public double? PatientWeight { get; set; }

    [JsonPropertyName("pregnancyStatus")]
    public int? PregnancyStatus { get; set; }

    // === Patient Medical ===

    [JsonPropertyName("medicalAlerts")]
    public string? MedicalAlerts { get; set; }

    [JsonPropertyName("contrastAllergies")]
    public string? ContrastAllergies { get; set; }

    // === Visit Identification ===

    [JsonPropertyName("admissionId")]
    public string? AdmissionId { get; set; }

    // === Imaging Service Request ===

    [JsonPropertyName("accessionNumber")]
    public string AccessionNumber { get; set; } = string.Empty;

    [JsonPropertyName("referringPhysicianName")]
    public string? ReferringPhysicianName { get; set; }

    [JsonPropertyName("requestedProcedureId")]
    public string RequestedProcedureId { get; set; } = string.Empty;

    [JsonPropertyName("requestedProcedureDescription")]
    public string? RequestedProcedureDescription { get; set; }

    [JsonPropertyName("requestedProcedurePriority")]
    public string? RequestedProcedurePriority { get; set; }

    [JsonPropertyName("requestedProcedureComments")]
    public string? RequestedProcedureComments { get; set; }

    [JsonPropertyName("studyInstanceUid")]
    public string StudyInstanceUid { get; set; } = string.Empty;

    // === Scheduled Procedure Step ===

    [JsonPropertyName("scheduledStationAeTitle")]
    public string ScheduledStationAeTitle { get; set; } = string.Empty;

    [JsonPropertyName("scheduledProcedureStepStartDate")]
    public string ScheduledProcedureStepStartDate { get; set; } = string.Empty;

    [JsonPropertyName("scheduledProcedureStepStartTime")]
    public string ScheduledProcedureStepStartTime { get; set; } = string.Empty;

    [JsonPropertyName("modality")]
    public string Modality { get; set; } = string.Empty;

    [JsonPropertyName("scheduledPerformingPhysicianName")]
    public string? ScheduledPerformingPhysicianName { get; set; }

    [JsonPropertyName("scheduledProcedureStepDescription")]
    public string? ScheduledProcedureStepDescription { get; set; }

    [JsonPropertyName("scheduledProcedureStepId")]
    public string ScheduledProcedureStepId { get; set; } = string.Empty;
}
