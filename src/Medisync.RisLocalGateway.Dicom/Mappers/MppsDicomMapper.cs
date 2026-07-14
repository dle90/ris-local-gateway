using System.Collections.Generic;
using System.Linq;
using FellowOakDicom;
using Medisync.RisLocalGateway.Core.Ris.Models;
using static Medisync.RisLocalGateway.Dicom.Mappers.DicomMapHelpers;

namespace Medisync.RisLocalGateway.Dicom.Mappers;

/// <summary>
/// Mapper MPPS (DICOM N-CREATE / N-SET dataset) → request model gửi RIS:
///   • N-CREATE (IN PROGRESS) → <see cref="LgsMppsInProgressRequest"/>
///   • N-SET COMPLETED        → <see cref="LgsMppsCompletedRequest"/>
///   • N-SET DISCONTINUED     → <see cref="LgsMppsDiscontinuedRequest"/>
/// Toàn bộ việc bóc DICOM tag nằm ở đây; provider chỉ điều phối.
/// </summary>
public static class MppsDicomMapper
{
    public static LgsMppsInProgressRequest ToMppsInProgressRequest(string sopInstanceUid, DicomDataset ds)
    {
        // (0040,0270) ScheduledStepAttributesSequence — chứa linkage tới SPS
        var sps = ds.GetSequence(DicomTag.ScheduledStepAttributesSequence)?.FirstOrDefault();

        return new LgsMppsInProgressRequest
        {
            SopInstanceUid                    = sopInstanceUid,
            PerformedProcedureStepId          = NullIfEmpty(ds.GetSingleValueOrDefault(DicomTag.PerformedProcedureStepID, string.Empty)),
            ScheduledProcedureStepId          = NullIfEmpty(sps?.GetSingleValueOrDefault(DicomTag.ScheduledProcedureStepID, string.Empty)),
            AccessionNumber                   = NullIfEmpty(sps?.GetSingleValueOrDefault(DicomTag.AccessionNumber, string.Empty)),
            StudyInstanceUid                  = sps?.GetSingleValueOrDefault(DicomTag.StudyInstanceUID, string.Empty) ?? string.Empty,
            StartDate                         = ds.GetSingleValueOrDefault(DicomTag.PerformedProcedureStepStartDate, string.Empty),
            StartTime                         = ds.GetSingleValueOrDefault(DicomTag.PerformedProcedureStepStartTime, string.Empty),
            Modality                          = NullIfEmpty(ds.GetSingleValueOrDefault(DicomTag.Modality, string.Empty)),
            PerformedStationAeTitle           = NullIfEmpty(ds.GetSingleValueOrDefault(DicomTag.PerformedStationAETitle, string.Empty)),
            PerformedStationName              = NullIfEmpty(ds.GetSingleValueOrDefault(DicomTag.PerformedStationName, string.Empty)),
            PerformedProcedureStepDescription = NullIfEmpty(ds.GetSingleValueOrDefault(DicomTag.PerformedProcedureStepDescription, string.Empty)),
            PerformedProcedureTypeDescription = NullIfEmpty(ds.GetSingleValueOrDefault(DicomTag.PerformedProcedureTypeDescription, string.Empty)),
            PatientId                         = NullIfEmpty(ds.GetSingleValueOrDefault(DicomTag.PatientID, string.Empty)),
            PatientName                       = NullIfEmpty(ds.GetSingleValueOrDefault(DicomTag.PatientName, string.Empty)),
            PatientBirthDate                  = NullIfEmpty(ds.GetSingleValueOrDefault(DicomTag.PatientBirthDate, string.Empty)),
            PatientSex                        = NullIfEmpty(ds.GetSingleValueOrDefault(DicomTag.PatientSex, string.Empty)),
        };
    }

    public static LgsMppsCompletedRequest ToMppsCompletedRequest(string sopInstanceUid, DicomDataset ds)
    {
        return new LgsMppsCompletedRequest
        {
            SopInstanceUid  = sopInstanceUid,
            EndDate         = ds.GetSingleValueOrDefault(DicomTag.PerformedProcedureStepEndDate, string.Empty),
            EndTime         = ds.GetSingleValueOrDefault(DicomTag.PerformedProcedureStepEndTime, string.Empty),
            PerformedSeries = ExtractPerformedSeries(ds),
        };
    }

    public static LgsMppsDiscontinuedRequest ToMppsDiscontinuedRequest(string sopInstanceUid, DicomDataset ds)
    {
        // (0040,0280) Comments on the Performed Procedure Step — free-text reason.
        // (0040,0281) PerformedProcedureStepDiscontinuationReasonCodeSequence — structured.
        var reason = NullIfEmpty(ds.GetSingleValueOrDefault(DicomTag.CommentsOnThePerformedProcedureStep, string.Empty));

        LgsMppsDiscontinuedRequestDiscontinuationReasonCode? reasonCode = null;
        var reasonSeq = ds.GetSequence(DicomTag.PerformedProcedureStepDiscontinuationReasonCodeSequence)?.FirstOrDefault();
        if (reasonSeq is not null)
        {
            reasonCode = new LgsMppsDiscontinuedRequestDiscontinuationReasonCode
            {
                CodeValue              = reasonSeq.GetSingleValueOrDefault(DicomTag.CodeValue, string.Empty),
                CodingSchemeDesignator = reasonSeq.GetSingleValueOrDefault(DicomTag.CodingSchemeDesignator, string.Empty),
                CodeMeaning            = reasonSeq.GetSingleValueOrDefault(DicomTag.CodeMeaning, string.Empty),
            };
        }

        return new LgsMppsDiscontinuedRequest
        {
            SopInstanceUid  = sopInstanceUid,
            EndDate         = ds.GetSingleValueOrDefault(DicomTag.PerformedProcedureStepEndDate, string.Empty),
            EndTime         = ds.GetSingleValueOrDefault(DicomTag.PerformedProcedureStepEndTime, string.Empty),
            Reason          = reason,
            ReasonCode      = reasonCode,
            PerformedSeries = ExtractPerformedSeries(ds),
        };
    }

    private static List<LgsMppsPerformedSeriesItem> ExtractPerformedSeries(DicomDataset ds)
    {
        var series = new List<LgsMppsPerformedSeriesItem>();
        var seq = ds.GetSequence(DicomTag.PerformedSeriesSequence);
        if (seq is null) return series;

        foreach (var item in seq)
        {
            series.Add(new LgsMppsPerformedSeriesItem
            {
                RetrieveAeTitle                 = NullIfEmpty(item.GetSingleValueOrDefault(DicomTag.RetrieveAETitle, string.Empty)),
                SeriesDescription               = NullIfEmpty(item.GetSingleValueOrDefault(DicomTag.SeriesDescription, string.Empty)),
                SeriesInstanceUid               = item.GetSingleValueOrDefault(DicomTag.SeriesInstanceUID, string.Empty),
                PerformedProcedureStepStartDate = NullIfEmpty(item.GetSingleValueOrDefault(DicomTag.PerformedProcedureStepStartDate, string.Empty)),
                Modality                        = NullIfEmpty(item.GetSingleValueOrDefault(DicomTag.Modality, string.Empty)),
                ProtocolName                    = NullIfEmpty(item.GetSingleValueOrDefault(DicomTag.ProtocolName, string.Empty)),
            });
        }
        return series;
    }
}
