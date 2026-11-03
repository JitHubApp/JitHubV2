using MarkdownRenderer.Images;

namespace MarkdownRenderer.Svg.Resvg.Internal;

/// <summary>
/// Opens one SVG for <see cref="ResvgMarkdownSvgRenderer"/>: process
/// snapshot admission, preflight, worker attachment and metadata validation,
/// then hands every source and worker owner to the returned document.
/// </summary>
/// <remarks>
/// The open's steps share one <see cref="SvgDocumentOpenOwners"/> local.
/// <see cref="ReleaseOpenOwners"/> releases the worker handle, the preflight
/// source, the snapshot reservation and the open borrow in a fixed order that
/// the shutdown and source-retention tests depend on.
/// </remarks>
internal sealed class SvgDocumentOpenOperation
{
    private readonly ResvgMarkdownSvgRenderer _owner;
    private readonly ResvgMarkdownSvgRendererOptions _options;
    private readonly WorkerPool _workers;
    private readonly SvgContentQuarantine _quarantine;
    private readonly SvgProcessSnapshotAdmission _snapshotAdmission;
    private readonly SvgProviderSnapshotLedger _sourceOwnership;
    private readonly SvgDocumentLifetime _openOperations;
    private readonly SvgResourceInspectionWorkspacePool _preflightWorkspaces = new();

    internal SvgDocumentOpenOperation(
        ResvgMarkdownSvgRenderer owner,
        ResvgMarkdownSvgRendererOptions options,
        WorkerPool workers,
        SvgContentQuarantine quarantine,
        SvgProcessSnapshotAdmission snapshotAdmission,
        SvgProviderSnapshotLedger sourceOwnership,
        SvgDocumentLifetime openOperations)
    {
        _owner = owner;
        _options = options;
        _workers = workers;
        _quarantine = quarantine;
        _snapshotAdmission = snapshotAdmission;
        _sourceOwnership = sourceOwnership;
        _openOperations = openOperations;
    }

    /// <summary>
    /// Reserves process snapshot capacity, runs preflight, opens the source
    /// in a worker and returns the owning document.
    /// </summary>
    /// <param name="request">The open request; its source is cleared once preflight owns a snapshot.</param>
    /// <param name="demand">The host's render demand, or null for a direct open.</param>
    /// <param name="returnCapacityUnavailable">Whether transient capacity pressure is a result instead of a failure.</param>
    /// <param name="cancellationToken">Cancels the open.</param>
    /// <returns>The opened document, or a capacity-unavailable result.</returns>
    internal async ValueTask<MarkdownSvgAdmissionResult> OpenAsync(
        MarkdownSvgOpenRequest request,
        MarkdownSvgRenderDemand? demand,
        bool returnCapacityUnavailable,
        CancellationToken cancellationToken)
    {
        SvgDocumentOpenOwners owners = default;
        try
        {
            cancellationToken = EnterOpen(request, ref owners, cancellationToken);
            if (!TryReserveInitialSnapshot(
                    request,
                    demand,
                    returnCapacityUnavailable,
                    ref owners,
                    out MarkdownSvgAdmissionResult snapshotUnavailable))
            {
                return snapshotUnavailable;
            }

            try
            {
                owners.Preflight = await SvgPreflight.InspectAsync(
                    request, _options, cancellationToken
                    , _sourceOwnership
                    , owners.SnapshotAdmission
                    , demand
                    , inspectionWorkspaces: _preflightWorkspaces
                    ).ConfigureAwait(false);
            }
            catch (SvgPreflightCapacityUnavailableException exception)
            {
                // Transient normalization scratch pressure is not a content
                // failure: do not hash or quarantine the source. The preflight
                // finally and this method's finally release every source alias
                // and reservation before this host result becomes observable.
                return NormalizationCapacityUnavailable(exception, returnCapacityUnavailable);
            }
            // The accepted preflight result now owns the reservation. It will
            // release it only when the document snapshot lifetime drains.
            owners.SnapshotAdmission = null;
            // The preflight result owns its private source snapshot. Keep only
            // the immutable render metadata in this async state machine while
            // worker attachment is pending; otherwise a completed task can
            // retain the caller's potentially much larger input array.
            request = request with { SanitizedSvgBytes = ReadOnlyMemory<byte>.Empty };

            string identity = Convert.ToHexString(owners.Preflight.Hash);
            _quarantine.ThrowIfQuarantined(identity);

            try
            {
                WorkerOpenResult opened = await _workers.OpenDocumentAsync(
                    owners.Preflight,
                    request,
                    cancellationToken,
                    contentAdmissionGuard: _quarantine.CreateContentAdmissionGuard(identity),
                    contentFailureCallback: _quarantine.CreateContentFailureCallback(identity),
                    demand: demand).ConfigureAwait(false);
                return AcceptOpenedDocument(opened, request, identity, ref owners);
            }
            catch (Exception exception) when (TryClassifyWorkerOpenFailure(
                exception,
                cancellationToken.IsCancellationRequested,
                out MarkdownSvgFailureReason reason,
                out bool quarantineContent))
            {
                if (quarantineContent)
                    _quarantine.Add(identity);
                throw new MarkdownSvgException(reason, innerException: exception);
            }
        }
        catch (MarkdownSvgException)
        {
            throw;
        }
        catch (OperationCanceledException exception)
        {
            throw new MarkdownSvgException(MarkdownSvgFailureReason.Canceled, innerException: exception);
        }
        finally
        {
            ReleaseOpenOwners(ref owners, ref request);
        }
    }

    /// <summary>
    /// Classifies a failure while the worker opens the document or while its
    /// response becomes a document. The cases are tested in the order shown,
    /// and a <see cref="MarkdownSvgException"/> or an unlisted exception is not
    /// classified, so it propagates unchanged.
    /// </summary>
    /// <param name="exception">The failure.</param>
    /// <param name="openCanceled">Whether the open's linked token is canceled.</param>
    /// <param name="reason">The host-visible failure reason.</param>
    /// <param name="quarantineContent">Whether the failure is evidence against the SVG's content hash.</param>
    /// <returns>Whether the failure maps to a new <see cref="MarkdownSvgException"/>.</returns>
    private static bool TryClassifyWorkerOpenFailure(
        Exception exception,
        bool openCanceled,
        out MarkdownSvgFailureReason reason,
        out bool quarantineContent)
    {
        quarantineContent = false;
        switch (exception)
        {
            case OperationCanceledException:
                reason = MarkdownSvgFailureReason.Canceled;
                return true;
            case ObjectDisposedException when openCanceled:
                // Shutdown can close admission after preflight, before its
                // canceled token is checked by the pool. This says nothing
                // about the SVG: do not poison its content hash quarantine.
                reason = MarkdownSvgFailureReason.Canceled;
                return true;
            case WorkerInitializationDeadlineException:
                // Initialization is process-specific, not evidence that this SVG is
                // hostile. Never quarantine a content hash for a cold-start timeout.
                reason = MarkdownSvgFailureReason.Timeout;
                return true;
            case WorkerInitializationException:
                reason = MarkdownSvgFailureReason.WorkerFailure;
                return true;
            case WorkerDeadlineException:
                quarantineContent = true;
                reason = MarkdownSvgFailureReason.Timeout;
                return true;
            case MarkdownSvgException:
                break;
            case WorkerProtocolException or IOException or InvalidOperationException:
                quarantineContent = true;
                reason = MarkdownSvgFailureReason.WorkerFailure;
                return true;
        }

        reason = default;
        return false;
    }

    /// <summary>
    /// Takes the renderer's open borrow, links its shutdown token to the
    /// caller's, and validates the deterministic request limits.
    /// </summary>
    /// <returns>The linked token the rest of the open observes.</returns>
    private CancellationToken EnterOpen(
        MarkdownSvgOpenRequest request,
        ref SvgDocumentOpenOwners owners,
        CancellationToken cancellationToken)
    {
        _owner.ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(request);
        owners.OpenOperation = _openOperations.TryEnterRender() ?? throw new MarkdownSvgException(
            MarkdownSvgFailureReason.Canceled, "The SVG renderer is shutting down.");
        owners.OpenCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, owners.OpenOperation.Token);
        if (!Enum.IsDefined(request.Priority))
            throw new ArgumentOutOfRangeException(nameof(request), "The SVG open priority is invalid.");
        if (!Enum.IsDefined(request.ColorScheme))
            throw new ArgumentOutOfRangeException(nameof(request), "The SVG color scheme is invalid.");

        // Validate deterministic source limits before treating shared
        // process capacity as transient pressure. Reservation precedes
        // InspectAsync's first provider-owned clone.
        if (request.SanitizedSvgBytes.IsEmpty)
            throw new MarkdownSvgException(MarkdownSvgFailureReason.UnsupportedContent, "The SVG source is empty.");
        if (request.SanitizedSvgBytes.Length > _options.MaxSourceBytes)
            throw new MarkdownSvgException(MarkdownSvgFailureReason.ResourceLimitExceeded, "The SVG source exceeds the configured byte ceiling.");
        return owners.OpenCancellation.Token;
    }

    /// <summary>
    /// Reserves process snapshot capacity for the exact-length input clone.
    /// </summary>
    /// <returns>
    /// <see langword="false"/> with <paramref name="unavailable"/> set when capacity is full
    /// and the caller accepts a capacity-unavailable result.
    /// </returns>
    private bool TryReserveInitialSnapshot(
        MarkdownSvgOpenRequest request,
        MarkdownSvgRenderDemand? demand,
        bool returnCapacityUnavailable,
        ref SvgDocumentOpenOwners owners,
        out MarkdownSvgAdmissionResult unavailable)
    {
        // The first provider allocation is an exact-length input clone.
        // Reserving the per-document ceiling here makes a dozen tiny
        // visible SVGs exhaust the process budget before parsing starts.
        // Preflight separately grows this lease before normalization's
        // output/scratch overlap; no allocation is left unaccounted for.
        int initialSourceReservationBytes = request.SanitizedSvgBytes.Length;
        if (_snapshotAdmission.TryReserve(
                initialSourceReservationBytes,
                demand?.Priority ?? request.Priority,
                out owners.SnapshotAdmission,
                out long observedCapacityVersion))
        {
            unavailable = default;
            return true;
        }

        if (returnCapacityUnavailable)
        {
            unavailable = MarkdownSvgAdmissionResult.CapacityUnavailable(
                observedCapacityVersion,
                initialSourceReservationBytes);
            return false;
        }

        throw new MarkdownSvgException(
            MarkdownSvgFailureReason.ResourceLimitExceeded,
            "The process-wide SVG source snapshot capacity is currently full.");
    }

    /// <summary>
    /// Maps preflight's normalization capacity pressure to a capacity-unavailable
    /// result, or to a resource-limit failure when the caller wants one.
    /// </summary>
    private static MarkdownSvgAdmissionResult NormalizationCapacityUnavailable(
        SvgPreflightCapacityUnavailableException exception,
        bool returnCapacityUnavailable)
    {
        if (returnCapacityUnavailable)
        {
            return MarkdownSvgAdmissionResult.CapacityUnavailable(
                exception.CapacityVersion,
                exception.RequiredSourceReservationBytes);
        }

        throw new MarkdownSvgException(
            MarkdownSvgFailureReason.ResourceLimitExceeded,
            "The process-wide SVG source normalization capacity is currently full.",
            exception);
    }

    /// <summary>
    /// Validates the worker's open response and hands the preflight source and
    /// worker handle to a new document.
    /// </summary>
    private MarkdownSvgAdmissionResult AcceptOpenedDocument(
        WorkerOpenResult opened,
        MarkdownSvgOpenRequest request,
        string identity,
        ref SvgDocumentOpenOwners owners)
    {
        SvgPreflightResult preflight = owners.Preflight!;
        WorkerResponse response = opened.Response;
        if (response.Status == WorkerStatus.Ok)
            owners.UnownedWorkerDocument = opened.Document;
        _quarantine.ThrowForRejectedResponse(response, identity);
        if (!double.IsFinite(response.IntrinsicWidth) || response.IntrinsicWidth <= 0 ||
            !double.IsFinite(response.IntrinsicHeight) || response.IntrinsicHeight <= 0 ||
            response.OutputLength != 0 || response.Width != 0 || response.Height != 0)
        {
            _quarantine.Add(identity);
            _workers.InvalidateWorker(response.WorkerInstanceId);
            throw new MarkdownSvgException(MarkdownSvgFailureReason.WorkerFailure, "The SVG worker returned malformed metadata.");
        }

        var info = new MarkdownSvgDocumentInfo(
            response.IntrinsicWidth,
            response.IntrinsicHeight,
            response.AspectRatio > 0 && double.IsFinite(response.AspectRatio) ? response.AspectRatio : null,
            preflight.Info.Description,
            response.HasText,
            response.UsesCurrentColor,
            response.UsesColorScheme);
        var document = new ResvgMarkdownSvgDocument(
            _owner,
            _workers,
            _options,
            preflight,
            request,
            identity,
            info,
            opened.Document);
        long sourceReservationBytes = preflight.SourceReservationBytes;
        // The returned document now owns the sanitized source. Do not
        // keep a second reference in the completed OpenAsync task. It
        // also owns the worker handle, so failure cleanup must not close it.
        owners.UnownedWorkerDocument = null;
        owners.SourceTransferredToDocument = true;
        owners.Preflight = null;
        return MarkdownSvgAdmissionResult.Opened(
            document,
            sourceReservationBytes,
            _snapshotAdmission.AdmissionDomain);
    }

    /// <summary>
    /// Releases what the open still owns: the unowned worker handle, then the
    /// preflight source, then the snapshot reservation and linked token, and
    /// the open borrow last. Every reference is cleared, including the
    /// caller's request, so a retained canceled or faulted task pins nothing.
    /// </summary>
    private void ReleaseOpenOwners(ref SvgDocumentOpenOwners owners, ref MarkdownSvgOpenRequest request)
    {
        try
        {
            try
            {
                if (owners.UnownedWorkerDocument is { } workerDocument)
                {
                    try
                    {
                        _workers.ForgetDocument(workerDocument);
                    }
                    catch
                    {
                        // Cleanup is best effort here and must not replace
                        // the original open/metadata/construction failure.
                    }
                }
            }
            finally
            {
                owners.UnownedWorkerDocument = null;
            }
        }
        finally
        {
            try
            {
                if (!owners.SourceTransferredToDocument)
                    owners.Preflight?.ReleaseSource();
            }
            finally
            {
                // Clear even validation/preflight failures. A retained canceled
                // or faulted task must not pin the original caller-owned array.
                try
                {
                    request = null!;
                    owners.Preflight = null;
                    owners.SnapshotAdmission?.Dispose();
                    owners.SnapshotAdmission = null;
                    owners.OpenCancellation?.Dispose();
                }
                finally
                {
                    // Release the actual-work borrow last. Async shutdown
                    // cannot finish while this operation still owns its
                    // private snapshot or the caller's source alias.
                    owners.OpenCancellation = null;
                    owners.OpenOperation?.Dispose();
                    owners.OpenOperation = null;
                }
            }
        }
    }
}
