using System.Linq;
using FellowOakDicom;
using FellowOakDicom.Network;
using Medisync.RisLocalGateway.Core.Ris.Models;
using static Medisync.RisLocalGateway.Dicom.Mappers.DicomMapHelpers;

namespace Medisync.RisLocalGateway.Dicom.Mappers;

/// <summary>
/// Mapper Modality Worklist (MWL):
///   • C-FIND request (DICOM) → <see cref="LgsGetWorkListRequest"/> (query gửi RIS).
///   • <see cref="LgsGetWorkListResponse"/> (RIS) → DicomDataset (1 worklist item trả modality).
/// Toàn bộ việc bóc/ghép DICOM tag nằm ở đây; provider chỉ điều phối (không tự đọc field từ dataset).
/// </summary>
public static class WorklistDicomMapper
{
    /// <summary>Bóc các khoá lọc worklist từ C-FIND request → request model gửi RIS.</summary>
    public static LgsGetWorkListRequest ToLgsGetWorkListRequest(DicomCFindRequest request)
    {
        var sps = request.Dataset
            .GetSequence(DicomTag.ScheduledProcedureStepSequence)?
            .FirstOrDefault();

        return new LgsGetWorkListRequest
        {
            PatientName          = NullIfEmpty(request.Dataset.GetSingleValueOrDefault(DicomTag.PatientName, string.Empty)),
            PatientId            = NullIfEmpty(request.Dataset.GetSingleValueOrDefault(DicomTag.PatientID, string.Empty)),
            AccessionNumber      = NullIfEmpty(request.Dataset.GetSingleValueOrDefault(DicomTag.AccessionNumber, string.Empty)),
            StudyInstanceUid     = NullIfEmpty(request.Dataset.GetSingleValueOrDefault(DicomTag.StudyInstanceUID, string.Empty)),
            RequestedProcedureId = NullIfEmpty(request.Dataset.GetSingleValueOrDefault(DicomTag.RequestedProcedureID, string.Empty)),

            ScheduledStationAeTitle         = sps?.GetSingleValueOrDefault(DicomTag.ScheduledStationAETitle, string.Empty) ?? string.Empty,
            ScheduledProcedureStepStartDate = NullIfEmpty(sps?.GetSingleValueOrDefault(DicomTag.ScheduledProcedureStepStartDate, string.Empty)),
            ScheduledProcedureStepStartTime = NullIfEmpty(sps?.GetSingleValueOrDefault(DicomTag.ScheduledProcedureStepStartTime, string.Empty)),
            Modality                        = NullIfEmpty(sps?.GetSingleValueOrDefault(DicomTag.Modality, string.Empty)),
            ScheduledProcedureStepId        = NullIfEmpty(sps?.GetSingleValueOrDefault(DicomTag.ScheduledProcedureStepID, string.Empty)),
        };
    }

    /// <summary>Ghép 1 worklist item (từ RIS) thành DicomDataset để trả C-FIND-RSP cho modality.</summary>
    public static DicomDataset ToWorklistDataset(LgsGetWorkListResponse item)
    {
        // Top-level Patient + ISR + RequestedProcedure attributes
        var ds = new DicomDataset
        {
            { DicomTag.SpecificCharacterSet, "ISO_IR 192" }, // UTF-8 hỗ trợ tiếng Việt
            { DicomTag.PatientID, item.PatientId },
            { DicomTag.PatientName, item.PatientName },
            { DicomTag.AccessionNumber, item.AccessionNumber },
            { DicomTag.StudyInstanceUID, item.StudyInstanceUid },
            { DicomTag.RequestedProcedureID, item.RequestedProcedureId },
        };

        AddIfPresent(ds, DicomTag.PatientBirthDate, item.PatientBirthDate);
        AddIfPresent(ds, DicomTag.PatientSex, item.PatientSex);
        AddIfPresent(ds, DicomTag.MedicalAlerts, item.MedicalAlerts);
        AddIfPresent(ds, DicomTag.Allergies, item.ContrastAllergies);
        AddIfPresent(ds, DicomTag.AdmissionID, item.AdmissionId);
        AddIfPresent(ds, DicomTag.ReferringPhysicianName, item.ReferringPhysicianName);
        AddIfPresent(ds, DicomTag.RequestedProcedureDescription, item.RequestedProcedureDescription);
        AddIfPresent(ds, DicomTag.RequestedProcedurePriority, item.RequestedProcedurePriority);
        AddIfPresent(ds, DicomTag.RequestedProcedureComments, item.RequestedProcedureComments);

        if (item.PatientWeight.HasValue)
        {
            ds.AddOrUpdate(DicomTag.PatientWeight, item.PatientWeight.Value);
        }
        if (item.PregnancyStatus.HasValue)
        {
            ds.AddOrUpdate(DicomTag.PregnancyStatus, (ushort)item.PregnancyStatus.Value);
        }

        // Scheduled Procedure Step Sequence (0040,0100)
        var sps = new DicomDataset
        {
            { DicomTag.ScheduledStationAETitle, item.ScheduledStationAeTitle },
            { DicomTag.ScheduledProcedureStepStartDate, item.ScheduledProcedureStepStartDate },
            { DicomTag.ScheduledProcedureStepStartTime, item.ScheduledProcedureStepStartTime },
            { DicomTag.Modality, item.Modality },
            { DicomTag.ScheduledProcedureStepID, item.ScheduledProcedureStepId },
        };
        AddIfPresent(sps, DicomTag.ScheduledPerformingPhysicianName, item.ScheduledPerformingPhysicianName);
        AddIfPresent(sps, DicomTag.ScheduledProcedureStepDescription, item.ScheduledProcedureStepDescription);

        ds.Add(new DicomSequence(DicomTag.ScheduledProcedureStepSequence, sps));

        return ds;
    }
}
