using FellowOakDicom;
using Medisync.RisLocalGateway.Core.Ris.Models;
using static Medisync.RisLocalGateway.Dicom.Mappers.DicomMapHelpers;

namespace Medisync.RisLocalGateway.Dicom.Mappers;

/// <summary>
/// Mapper bóc tag từ IMAGE instance (ảnh C-STORE) → request gửi RIS. Dùng cho luồng
/// receive-study-info: gateway gửi snapshot study + bộ phận chụp per series khi forward ảnh.
/// </summary>
public static class ImageDicomMapper
{
    /// <summary>
    /// Bóc snapshot study từ dataset ảnh → LgsReceiveStudyInfoRequest.
    /// Trả null nếu thiếu StudyInstanceUID (không có khoá thì không gửi được).
    /// BodyPart / các field khác optional (series không có BodyPartExamined vẫn gửi để HIS
    /// đăng ký orphan).
    /// </summary>
    public static LgsReceiveStudyInfoRequest? ToReceiveStudyInfoRequest(DicomDataset ds)
    {
        var studyUid = NullIfEmpty(ds.GetSingleValueOrDefault(DicomTag.StudyInstanceUID, string.Empty));
        if (studyUid is null) return null;

        var bodyPart = NullIfEmpty(ds.GetSingleValueOrDefault(DicomTag.BodyPartExamined, string.Empty));

        return new LgsReceiveStudyInfoRequest
        {
            StudyInstanceUid  = studyUid,
            SeriesInstanceUid = NullIfEmpty(ds.GetSingleValueOrDefault(DicomTag.SeriesInstanceUID, string.Empty)),
            BodyPart          = bodyPart?.Trim().ToUpperInvariant(),

            PatientId         = NullIfEmpty(ds.GetSingleValueOrDefault(DicomTag.PatientID, string.Empty)),
            PatientName       = NullIfEmpty(ds.GetSingleValueOrDefault(DicomTag.PatientName, string.Empty)),
            PatientBirthDate  = NullIfEmpty(ds.GetSingleValueOrDefault(DicomTag.PatientBirthDate, string.Empty)),
            PatientSex        = NullIfEmpty(ds.GetSingleValueOrDefault(DicomTag.PatientSex, string.Empty)),
            AccessionNumber   = NullIfEmpty(ds.GetSingleValueOrDefault(DicomTag.AccessionNumber, string.Empty)),

            Modality          = NullIfEmpty(ds.GetSingleValueOrDefault(DicomTag.Modality, string.Empty)),
            StudyDescription  = NullIfEmpty(ds.GetSingleValueOrDefault(DicomTag.StudyDescription, string.Empty)),
            StudyDate         = NullIfEmpty(ds.GetSingleValueOrDefault(DicomTag.StudyDate, string.Empty)),
            StudyTime         = NullIfEmpty(ds.GetSingleValueOrDefault(DicomTag.StudyTime, string.Empty)),
        };
    }
}
