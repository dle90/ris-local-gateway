using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using FellowOakDicom;
using FellowOakDicom.Network;
using Medisync.RisLocalGateway.Core.Ris;
using Medisync.RisLocalGateway.Core.Ris.Models;
using Medisync.RisLocalGateway.Dicom.Mappers;
using Medisync.RisLocalGateway.Dicom.Stats;
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

    /// <summary>
    /// Thống kê study local (tab "Study đã nhận"). Set 1 lần lúc Service start (static vì
    /// fo-dicom 5.x không inject được vào Provider). Null = không ghi thống kê.
    /// </summary>
    public static StudyStatsStore? Stats { get; set; }

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
        var query = WorklistDicomMapper.ToLgsGetWorkListRequest(request);

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
                Dataset = WorklistDicomMapper.ToWorklistDataset(item),
            };
        }

        yield return new DicomCFindResponse(request, DicomStatus.Success);
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

        var payload = MppsDicomMapper.ToMppsInProgressRequest(sopInstanceUid, request.Dataset);
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
            var payload = MppsDicomMapper.ToMppsCompletedRequest(sopInstanceUid, request.Dataset);
            var result = await RisClient.NotifyMppsCompletedAsync(payload).ConfigureAwait(false);
            ok = result.IsSuccess;
            if (!ok) Logger.LogError("NotifyMppsCompleted failed: HTTP {Code} — {Message}",
                result.HttpStatusCode, result.ErrorMessage);
        }
        else if (statusNorm == "DISCONTINUED")
        {
            var payload = MppsDicomMapper.ToMppsDiscontinuedRequest(sopInstanceUid, request.Dataset);
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

            // Thống kê "đã nhận" (best-effort, chỉ đẩy vào queue in-memory — không chạm DB ở đây).
            if (request.Dataset is not null)
            {
                Stats?.TryRecord(StudyStatsEvent.FromDataset(
                    StudyStatsEventKind.Received, request.Dataset,
                    callingAe: Association?.CallingAE ?? string.Empty));
            }

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
