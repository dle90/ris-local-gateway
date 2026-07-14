using System.Text.Json.Serialization;

namespace Medisync.RisLocalGateway.Dicom;

/// <summary>
/// Nội dung file sidecar lý do dead-letter (spool/failed-reasons/&lt;sop&gt;.error.json).
/// SpoolStore ghi khi chuyển 1 instance sang dead-letter; ConfigApp đọc lại để hiển thị + re-drive.
/// Khai báo typed (không bóc field động từ JSON) — dùng chung 2 phía.
/// </summary>
public sealed class SpoolFailureSidecar
{
    /// <summary>Tên file .dcm trong thư mục failed.</summary>
    [JsonPropertyName("file")]
    public string File { get; set; } = string.Empty;

    /// <summary>Loại lỗi: Permanent / Retryable / Corrupt.</summary>
    [JsonPropertyName("category")]
    public string Category { get; set; } = string.Empty;

    /// <summary>Số lần đã thử forward trước khi dead-letter.</summary>
    [JsonPropertyName("attempts")]
    public int Attempts { get; set; }

    /// <summary>SeriesInstanceUID (nếu biết).</summary>
    [JsonPropertyName("series")]
    public string? Series { get; set; }

    /// <summary>Chi tiết lỗi cuối cùng (human-readable).</summary>
    [JsonPropertyName("reason")]
    public string Reason { get; set; } = string.Empty;

    /// <summary>Thời điểm dead-letter (ISO-8601 UTC).</summary>
    [JsonPropertyName("failedAtUtc")]
    public string FailedAtUtc { get; set; } = string.Empty;
}
