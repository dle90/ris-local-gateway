using System.Text.Json.Serialization;

namespace Medisync.RisLocalGateway.Core.Ris.Models;

/// <summary>
/// Request body cho POST /v1/lgs/local-gateway-server/actions/receive-study-info.
/// Gateway gửi PER SERIES sau khi forward thành công ảnh của series đó — mang snapshot study
/// + bộ phận chụp. HIS: study có trong worklist → merge body part; không có → lưu orphan study.
/// Match LgsReceiveStudyInfoRequest trong his-core.
/// </summary>
public sealed class LgsReceiveStudyInfoRequest
{
    /// <summary>(0020,000D) Study Instance UID — khoá tìm study/orphan bên HIS.</summary>
    [JsonPropertyName("studyInstanceUid")]
    public string StudyInstanceUid { get; set; } = string.Empty;

    /// <summary>(0020,000E) Series Instance UID — trace/log.</summary>
    [JsonPropertyName("seriesInstanceUid")]
    public string? SeriesInstanceUid { get; set; }

    /// <summary>(0018,0015) BodyPartExamined của series này (optional).</summary>
    [JsonPropertyName("bodyPart")]
    public string? BodyPart { get; set; }

    // === Snapshot hành chính bệnh nhân (DICOM headers) — HIS dùng khi tạo orphan ===

    [JsonPropertyName("patientId")]
    public string? PatientId { get; set; }

    [JsonPropertyName("patientName")]
    public string? PatientName { get; set; }

    [JsonPropertyName("patientBirthDate")]
    public string? PatientBirthDate { get; set; }

    [JsonPropertyName("patientSex")]
    public string? PatientSex { get; set; }

    [JsonPropertyName("accessionNumber")]
    public string? AccessionNumber { get; set; }

    // === Mô tả dịch vụ chụp ===

    [JsonPropertyName("modality")]
    public string? Modality { get; set; }

    [JsonPropertyName("studyDescription")]
    public string? StudyDescription { get; set; }

    [JsonPropertyName("studyDate")]
    public string? StudyDate { get; set; }

    [JsonPropertyName("studyTime")]
    public string? StudyTime { get; set; }
}
