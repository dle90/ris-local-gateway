using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using FellowOakDicom;
using FellowOakDicom.Network;
using Medisync.RisLocalGateway.Core.Ris;
using Medisync.RisLocalGateway.Core.Ris.Models;
using Microsoft.Extensions.Logging;

namespace Medisync.RisLocalGateway.Dicom;

/// <summary>
/// One provider per incoming DICOM association. fo-dicom instantiates this for every connection.
/// Implements C-ECHO, C-FIND (MWL → RIS), MPPS (N-CREATE/N-SET → RIS), and C-STORE (spool + forward STOW).
/// MWL/MPPS gọi RIS thật qua <see cref="IRisClient"/>; C-STORE spool ra đĩa rồi SpoolForwarder forward nền.
/// </summary>
public sealed class RisGatewayDicomProvider :
    DicomService,
    IDicomServiceProvider,
    IDicomCEchoProvider,
    IDicomCFindProvider,
    IDicomCStoreProvider,
    IDicomNServiceProvider
{
    /// <summary>
    /// Static reference tới IRisClient — set 1 lần lúc Service start.
    /// fo-dicom 5.x giới hạn signature constructor của Provider nên không thể
    /// inject qua DI cleanly. Static là cách pragmatic.
    /// </summary>
    public static IRisClient? RisClient { get; set; }

    /// <summary>
    /// Spool đĩa cho ảnh nhận qua C-STORE (chờ forward nền). Set 1 lần lúc Service start
    /// vì fo-dicom 5.x không inject được vào Provider.
    /// </summary>
    public static SpoolStore? Spool { get; set; }

    private static readonly DicomTransferSyntax[] AcceptedTransferSyntaxes =
    {
        DicomTransferSyntax.ExplicitVRLittleEndian,
        DicomTransferSyntax.ImplicitVRLittleEndian,
    };

    private static readonly DicomTransferSyntax[] AcceptedImageTransferSyntaxes =
    {
        DicomTransferSyntax.JPEG2000Lossless,
        DicomTransferSyntax.JPEG2000Lossy,
        DicomTransferSyntax.JPEGProcess14SV1,
        DicomTransferSyntax.JPEGProcess14,
        DicomTransferSyntax.JPEGProcess1,
        DicomTransferSyntax.JPEGProcess2_4,
        DicomTransferSyntax.RLELossless,
        DicomTransferSyntax.ExplicitVRLittleEndian,
        DicomTransferSyntax.ImplicitVRLittleEndian,
    };

    public RisGatewayDicomProvider(
        INetworkStream stream,
        Encoding fallbackEncoding,
        ILogger log,
        DicomServiceDependencies dependencies)
        : base(stream, fallbackEncoding, log, dependencies)
    {
    }

    // True khi kết nối này đã nhận association request (phân biệt với TCP probe rỗng:
    // connect-rồi-đóng từ health-check ConfigApp/DVT) để khỏi spam log INF.
    private bool _associationReceived;

    #region Association lifecycle

    public Task OnReceiveAssociationRequestAsync(DicomAssociation association)
    {
        _associationReceived = true;
        Logger.LogInformation(
            "Association request from {CallingAE} @ {RemoteHost}:{RemotePort} to {CalledAE}",
            association.CallingAE,
            association.RemoteHost,
            association.RemotePort,
            association.CalledAE);

        foreach (var pc in association.PresentationContexts)
        {
            if (pc.AbstractSyntax == DicomUID.Verification)
            {
                pc.AcceptTransferSyntaxes(AcceptedTransferSyntaxes);
                continue;
            }

            if (pc.AbstractSyntax == DicomUID.ModalityWorklistInformationModelFind)
            {
                pc.AcceptTransferSyntaxes(AcceptedTransferSyntaxes);
                continue;
            }

            if (pc.AbstractSyntax == DicomUID.ModalityPerformedProcedureStep)
            {
                pc.AcceptTransferSyntaxes(AcceptedTransferSyntaxes);
                continue;
            }

            if (pc.AbstractSyntax.StorageCategory != DicomStorageCategory.None)
            {
                pc.AcceptTransferSyntaxes(AcceptedImageTransferSyntaxes);
                continue;
            }

            Logger.LogWarning("Rejecting unsupported abstract syntax {Uid}", pc.AbstractSyntax);
            pc.SetResult(DicomPresentationContextResult.RejectAbstractSyntaxNotSupported);
        }

        return SendAssociationAcceptAsync(association);
    }

    public Task OnReceiveAssociationReleaseRequestAsync()
    {
        Logger.LogInformation("Association release requested");
        return SendAssociationReleaseResponseAsync();
    }

    public void OnReceiveAbort(DicomAbortSource source, DicomAbortReason reason)
    {
        Logger.LogWarning("Association abort: source={Source} reason={Reason}", source, reason);
    }

    public void OnConnectionClosed(Exception exception)
    {
        if (exception is not null)
        {
            Logger.LogError(exception, "Connection closed with error");
        }
        else if (_associationReceived)
        {
            Logger.LogInformation("Connection closed");
        }
        else
        {
            // Kết nối TCP rỗng (connect rồi đóng, KHÔNG có association) — thường là health
            // probe tới cổng SCP (ConfigApp/DVT). Hạ xuống Debug để khỏi spam log.
            Logger.LogDebug("Empty connection closed (no association — health probe)");
        }
    }

    #endregion

    #region C-ECHO

    public Task<DicomCEchoResponse> OnCEchoRequestAsync(DicomCEchoRequest request)
    {
        Logger.LogInformation("C-ECHO received");
        return Task.FromResult(new DicomCEchoResponse(request, DicomStatus.Success));
    }

    #endregion

    #region C-FIND — Modality Worklist (MWL)

    public async IAsyncEnumerable<DicomCFindResponse> OnCFindRequestAsync(DicomCFindRequest request)
    {
        var query = ExtractWorklistQuery(request);

        Logger.LogInformation(
            "C-FIND MWL level={Level} modality={Modality} scheduledAE={ScheduledAE} date={Date}",
            request.Level, query.Modality, query.ScheduledStationAeTitle, query.ScheduledProcedureStepStartDate);

        if (RisClient is null)
        {
            Logger.LogError("RisClient chưa được khởi tạo");
            yield return new DicomCFindResponse(request, DicomStatus.ProcessingFailure);
            yield break;
        }

        var result = await RisClient.FetchWorkListAsync(query).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            Logger.LogError("FetchWorkList failed: HTTP {Code} — {Message}",
                result.HttpStatusCode, result.ErrorMessage);
            yield return new DicomCFindResponse(request, DicomStatus.ProcessingFailure);
            yield break;
        }

        var items = result.Data ?? new List<LgsGetWorkListResponse>();
        Logger.LogInformation("Trả {Count} worklist items", items.Count);

        foreach (var item in items)
        {
            yield return new DicomCFindResponse(request, DicomStatus.Pending)
            {
                Dataset = BuildWorklistDataset(item),
            };
        }

        yield return new DicomCFindResponse(request, DicomStatus.Success);
    }

    private static LgsGetWorkListRequest ExtractWorklistQuery(DicomCFindRequest request)
    {
        var sps = request.Dataset
            .GetSequence(DicomTag.ScheduledProcedureStepSequence)?
            .FirstOrDefault();

        return new LgsGetWorkListRequest
        {
            PatientName             = NullIfEmpty(request.Dataset.GetSingleValueOrDefault(DicomTag.PatientName, string.Empty)),
            PatientId               = NullIfEmpty(request.Dataset.GetSingleValueOrDefault(DicomTag.PatientID, string.Empty)),
            AccessionNumber         = NullIfEmpty(request.Dataset.GetSingleValueOrDefault(DicomTag.AccessionNumber, string.Empty)),
            StudyInstanceUid        = NullIfEmpty(request.Dataset.GetSingleValueOrDefault(DicomTag.StudyInstanceUID, string.Empty)),
            RequestedProcedureId    = NullIfEmpty(request.Dataset.GetSingleValueOrDefault(DicomTag.RequestedProcedureID, string.Empty)),

            ScheduledStationAeTitle         = sps?.GetSingleValueOrDefault(DicomTag.ScheduledStationAETitle, string.Empty) ?? string.Empty,
            ScheduledProcedureStepStartDate = NullIfEmpty(sps?.GetSingleValueOrDefault(DicomTag.ScheduledProcedureStepStartDate, string.Empty)),
            ScheduledProcedureStepStartTime = NullIfEmpty(sps?.GetSingleValueOrDefault(DicomTag.ScheduledProcedureStepStartTime, string.Empty)),
            Modality                        = NullIfEmpty(sps?.GetSingleValueOrDefault(DicomTag.Modality, string.Empty)),
            ScheduledProcedureStepId        = NullIfEmpty(sps?.GetSingleValueOrDefault(DicomTag.ScheduledProcedureStepID, string.Empty)),
        };

        static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
    }

    private static DicomDataset BuildWorklistDataset(LgsGetWorkListResponse item)
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

    private static void AddIfPresent(DicomDataset ds, DicomTag tag, string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            ds.AddOrUpdate(tag, value);
        }
    }

    #endregion

    #region MPPS — N-CREATE / N-SET

    public async Task<DicomNCreateResponse> OnNCreateRequestAsync(DicomNCreateRequest request)
    {
        var sopInstanceUid = request.SOPInstanceUID?.UID ?? string.Empty;
        var status = request.Dataset?.GetSingleValueOrDefault(
            DicomTag.PerformedProcedureStepStatus, string.Empty) ?? string.Empty;

        Logger.LogInformation("MPPS N-CREATE sop={Sop} status={Status}", sopInstanceUid, status);

        if (RisClient is null)
        {
            Logger.LogError("RisClient chưa được khởi tạo");
            return new DicomNCreateResponse(request, DicomStatus.ProcessingFailure);
        }

        if (string.IsNullOrEmpty(sopInstanceUid) || request.Dataset is null)
        {
            Logger.LogWarning("MPPS N-CREATE thiếu SOP UID hoặc dataset → reject");
            return new DicomNCreateResponse(request, DicomStatus.InvalidAttributeValue);
        }

        var payload = BuildMppsInProgressRequest(sopInstanceUid, request.Dataset);
        var result = await RisClient.NotifyMppsInProgressAsync(payload).ConfigureAwait(false);

        if (!result.IsSuccess)
        {
            Logger.LogError("NotifyMppsInProgress failed: HTTP {Code} — {Message}",
                result.HttpStatusCode, result.ErrorMessage);
            return new DicomNCreateResponse(request, DicomStatus.ProcessingFailure);
        }

        return new DicomNCreateResponse(request, DicomStatus.Success);
    }

    public async Task<DicomNSetResponse> OnNSetRequestAsync(DicomNSetRequest request)
    {
        var sopInstanceUid = request.SOPInstanceUID?.UID ?? string.Empty;
        var status = request.Dataset?.GetSingleValueOrDefault(
            DicomTag.PerformedProcedureStepStatus, string.Empty) ?? string.Empty;

        Logger.LogInformation("MPPS N-SET sop={Sop} status={Status}", sopInstanceUid, status);

        if (RisClient is null)
        {
            Logger.LogError("RisClient chưa được khởi tạo");
            return new DicomNSetResponse(request, DicomStatus.ProcessingFailure);
        }

        if (string.IsNullOrEmpty(sopInstanceUid) || request.Dataset is null)
        {
            Logger.LogWarning("MPPS N-SET thiếu SOP UID hoặc dataset → reject");
            return new DicomNSetResponse(request, DicomStatus.InvalidAttributeValue);
        }

        // Status xác định endpoint nào để forward (COMPLETED vs DISCONTINUED).
        // Modality chuẩn gửi 1 trong 2 giá trị này; case khác → log warn + reject.
        var statusNorm = status.Trim().ToUpperInvariant();
        bool ok;
        if (statusNorm == "COMPLETED")
        {
            var payload = BuildMppsCompletedRequest(sopInstanceUid, request.Dataset);
            var result = await RisClient.NotifyMppsCompletedAsync(payload).ConfigureAwait(false);
            ok = result.IsSuccess;
            if (!ok) Logger.LogError("NotifyMppsCompleted failed: HTTP {Code} — {Message}",
                result.HttpStatusCode, result.ErrorMessage);
        }
        else if (statusNorm == "DISCONTINUED")
        {
            var payload = BuildMppsDiscontinuedRequest(sopInstanceUid, request.Dataset);
            var result = await RisClient.NotifyMppsDiscontinuedAsync(payload).ConfigureAwait(false);
            ok = result.IsSuccess;
            if (!ok) Logger.LogError("NotifyMppsDiscontinued failed: HTTP {Code} — {Message}",
                result.HttpStatusCode, result.ErrorMessage);
        }
        else
        {
            Logger.LogWarning("MPPS N-SET status='{Status}' không phải COMPLETED/DISCONTINUED → reject", status);
            return new DicomNSetResponse(request, DicomStatus.InvalidAttributeValue);
        }

        return new DicomNSetResponse(request, ok ? DicomStatus.Success : DicomStatus.ProcessingFailure);
    }

    // ============== MPPS DICOM → request model ==============

    private static LgsMppsInProgressRequest BuildMppsInProgressRequest(string sopInstanceUid, DicomDataset ds)
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

    private static LgsMppsCompletedRequest BuildMppsCompletedRequest(string sopInstanceUid, DicomDataset ds)
    {
        return new LgsMppsCompletedRequest
        {
            SopInstanceUid  = sopInstanceUid,
            EndDate         = ds.GetSingleValueOrDefault(DicomTag.PerformedProcedureStepEndDate, string.Empty),
            EndTime         = ds.GetSingleValueOrDefault(DicomTag.PerformedProcedureStepEndTime, string.Empty),
            PerformedSeries = ExtractPerformedSeries(ds),
        };
    }

    private static LgsMppsDiscontinuedRequest BuildMppsDiscontinuedRequest(string sopInstanceUid, DicomDataset ds)
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

        static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    public Task<DicomNGetResponse> OnNGetRequestAsync(DicomNGetRequest request)
        => Task.FromResult(new DicomNGetResponse(request, DicomStatus.SOPClassNotSupported));

    public Task<DicomNDeleteResponse> OnNDeleteRequestAsync(DicomNDeleteRequest request)
        => Task.FromResult(new DicomNDeleteResponse(request, DicomStatus.SOPClassNotSupported));

    public Task<DicomNActionResponse> OnNActionRequestAsync(DicomNActionRequest request)
        => Task.FromResult(new DicomNActionResponse(request, DicomStatus.SOPClassNotSupported));

    public Task<DicomNEventReportResponse> OnNEventReportRequestAsync(DicomNEventReportRequest request)
        => Task.FromResult(new DicomNEventReportResponse(request, DicomStatus.SOPClassNotSupported));

    #endregion

    #region C-STORE

    public async Task<DicomCStoreResponse> OnCStoreRequestAsync(DicomCStoreRequest request)
    {
        var sop = request.SOPInstanceUID?.UID ?? "(none)";
        var sopClass = request.SOPClassUID?.UID ?? "(none)";
        var study = request.Dataset?.GetSingleValueOrDefault(DicomTag.StudyInstanceUID, "(none)");
        var series = request.Dataset?.GetSingleValueOrDefault(DicomTag.SeriesInstanceUID, "(none)");
        var modality = request.Dataset?.GetSingleValueOrDefault(DicomTag.Modality, "(none)");

        Logger.LogInformation(
            "C-STORE study={Study} series={Series} sop={Sop} sopClass={SopClass} modality={Modality}",
            study, series, sop, sopClass, modality);

        if (Spool is null)
        {
            Logger.LogError("SpoolStore chưa khởi tạo (Program startup) — C-STORE không nhận được");
            return new DicomCStoreResponse(request, DicomStatus.ProcessingFailure);
        }

        // Spool ra đĩa rồi trả Success NGAY (store-and-forward): không chặn modality theo
        // WAN, không mất ảnh nếu PACS lỗi. SpoolForwarder (nền) sẽ transcode JPEG 2000 +
        // STOW kèm Bearer token + retry; dicomweb-proxy verify token + gắn label tenant.
        try
        {
            var path = await Spool.EnqueueAsync(request.File).ConfigureAwait(false);
            Logger.LogInformation("C-STORE spooled sop={Sop} → {Path}", sop, path);
            return new DicomCStoreResponse(request, DicomStatus.Success);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Spool C-STORE lỗi sop={Sop}", sop);
            return new DicomCStoreResponse(request, DicomStatus.ProcessingFailure);
        }
    }

    public Task OnCStoreRequestExceptionAsync(string tempFileName, Exception e)
    {
        Logger.LogError(e, "C-STORE exception, tempFile={TempFile}", tempFileName);
        return Task.CompletedTask;
    }

    #endregion
}
