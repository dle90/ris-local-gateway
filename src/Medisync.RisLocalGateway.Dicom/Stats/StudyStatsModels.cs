using System;
using FellowOakDicom;

namespace Medisync.RisLocalGateway.Dicom.Stats;

/// <summary>Loại sự kiện thống kê ảnh: nhận qua C-STORE / đẩy PACS OK / vào dead-letter.</summary>
public enum StudyStatsEventKind
{
    Received,
    Pushed,
    Failed,
}

/// <summary>
/// Sự kiện thống kê 1 SOP instance — đầu vào của <see cref="StudyStatsStore"/>. Số liệu trên tab
/// "Study đã nhận" KHÔNG đếm bằng counter mà đếm theo TRẠNG THÁI instance (PENDING/PUSHED/FAILED)
/// nên re-send trùng SOP hay re-drive dead-letter đều không làm lệch số.
/// </summary>
public sealed record StudyStatsEvent
{
    public required StudyStatsEventKind Kind { get; init; }
    public required string SopUid { get; init; }
    public string StudyUid { get; init; } = string.Empty;
    public string AccessionNumber { get; init; } = string.Empty;
    public string PatientId { get; init; } = string.Empty;
    public string PatientName { get; init; } = string.Empty;
    public string StudyDescription { get; init; } = string.Empty;
    public string Modality { get; init; } = string.Empty;
    public string CallingAe { get; init; } = string.Empty;
    public string? FailedReason { get; init; }
    public DateTimeOffset At { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Bóc metadata study từ dataset ảnh. Trả null nếu thiếu SOPInstanceUID (không có khoá thì
    /// không đếm được — bỏ qua, không ảnh hưởng pipeline ảnh).
    /// </summary>
    public static StudyStatsEvent? FromDataset(
        StudyStatsEventKind kind, DicomDataset ds, string callingAe = "", string? failedReason = null)
    {
        var sop = ds.GetSingleValueOrDefault(DicomTag.SOPInstanceUID, string.Empty);
        if (string.IsNullOrEmpty(sop)) return null;

        return new StudyStatsEvent
        {
            Kind = kind,
            SopUid = sop,
            StudyUid = ds.GetSingleValueOrDefault(DicomTag.StudyInstanceUID, string.Empty),
            AccessionNumber = ds.GetSingleValueOrDefault(DicomTag.AccessionNumber, string.Empty),
            PatientId = ds.GetSingleValueOrDefault(DicomTag.PatientID, string.Empty),
            PatientName = ds.GetSingleValueOrDefault(DicomTag.PatientName, string.Empty),
            StudyDescription = ds.GetSingleValueOrDefault(DicomTag.StudyDescription, string.Empty),
            Modality = ds.GetSingleValueOrDefault(DicomTag.Modality, string.Empty),
            CallingAe = callingAe,
            FailedReason = failedReason,
        };
    }
}

/// <summary>
/// Row hiển thị tab "Danh sách study": metadata study + số instance theo trạng thái.
/// FirstReceivedText đã convert sẵn sang giờ local (dd/MM/yyyy HH:mm:ss) để bind thẳng;
/// FirstReceivedUtc giữ text UTC ISO gốc làm khoá SORT theo thời gian (string sort = đúng
/// thứ tự thời gian, khác với sort chuỗi dd/MM/yyyy đã format).
/// </summary>
public sealed record StudyStatsRow(
    string StudyUid,
    string AccessionNumber,
    string PatientId,
    string PatientName,
    string StudyDescription,
    string Modality,
    string CallingAe,
    string FirstReceivedUtc,
    string FirstReceivedText,
    string LastReceivedText,
    int ReceivedCount,
    int PushedCount,
    int FailedCount,
    int PendingCount);
