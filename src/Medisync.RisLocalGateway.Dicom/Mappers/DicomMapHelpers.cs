using FellowOakDicom;

namespace Medisync.RisLocalGateway.Dicom.Mappers;

/// <summary>
/// Helper dùng chung cho các mapper DICOM ↔ request/response (WorklistDicomMapper, MppsDicomMapper).
/// Gom NullIfEmpty / AddIfPresent một chỗ thay vì mỗi mapper tự khai báo lại.
/// </summary>
internal static class DicomMapHelpers
{
    /// <summary>Chuỗi rỗng/whitespace → null (để field optional trong request model đúng "không có giá trị").</summary>
    public static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    /// <summary>Chỉ Add/Update tag khi value có giá trị (bỏ qua tag rỗng khi build dataset trả về modality).</summary>
    public static void AddIfPresent(DicomDataset ds, DicomTag tag, string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            ds.AddOrUpdate(tag, value);
        }
    }
}
