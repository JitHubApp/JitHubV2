using MarkdownRenderer.Images;

namespace MarkdownRenderer.Svg.Resvg.Internal;

/// <summary>
/// Opens one SVG for <see cref="ResvgMarkdownSvgRenderer"/>: process
/// snapshot admission, preflight, worker attachment and metadata validation,
/// then hands every source and worker owner to the returned document.
/// </summary>
/// <remarks>
/// The open method stays one long method on purpose: its nested finally
/// blocks release the worker handle, the preflight source, the snapshot
/// reservation and the open borrow in a fixed order that the shutdown and
/// source-retention tests depend on.
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
        SvgPreflightResult? preflight = null;
        SvgProcessSnapshotAdmission.Lease? snapshotAdmission = null;
        WorkerDocumentHandle? unownedWorkerDocument = null;
        SvgDocumentLifetime.RenderLease? openOperation = null;
        CancellationTokenSource? openCancellation = null;
        bool sourceTransferredToDocument = false;
        try
        {
            _owner.ThrowIfDisposed();
            ArgumentNullException.ThrowIfNull(request);
            openOperation = _openOperations.TryEnterRender() ?? throw new MarkdownSvgException(
                MarkdownSvgFailureReason.Canceled, "The SVG renderer is shutting down.");
            openCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, openOperation.Token);
            cancellationToken = openCancellation.Token;
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

            // The first provider allocation is an exact-length input clone.
            // Reserving the per-document ceiling here makes a dozen tiny
            // visible SVGs exhaust the process budget before parsing starts.
            // Preflight separately grows this lease before normalization's
            // output/scratch overlap; no allocation is left unaccounted for.
            int initialSourceReservationBytes = request.SanitizedSvgBytes.Length;
            if (!_snapshotAdmission.TryReserve(
                    initialSourceReservationBytes,
                    demand?.Priority ?? request.Priority,
                    out snapshotAdmission,
                    out long observedCapacityVersion))
            {
                if (returnCapacityUnavailable)
                {
                    return MarkdownSvgAdmissionResult.CapacityUnavailable(
                        observedCapacityVersion,
                        initialSourceReservationBytes);
                }

                throw new MarkdownSvgException(
                    MarkdownSvgFailureReason.ResourceLimitExceeded,
                    "The process-wide SVG source snapshot capacity is currently full.");
            }

            try
            {
                preflight = await SvgPreflight.InspectAsync(
                    request, _options, cancellationToken
                    , _sourceOwnership
                    , snapshotAdmission
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
            // The accepted preflight result now owns the reservation. It will
            // release it only when the document snapshot lifetime drains.
            snapshotAdmission = null;
            // The preflight result owns its private source snapshot. Keep only
            // the immutable render metadata in this async state machine while
            // worker attachment is pending; otherwise a completed task can
            // retain the caller's potentially much larger input array.
            request = request with { SanitizedSvgBytes = ReadOnlyMemory<byte>.Empty };

            string identity = Convert.ToHexString(preflight.Hash);
            _quarantine.ThrowIfQuarantined(identity);

            try
            {
                WorkerOpenResult opened = await _workers.OpenDocumentAsync(
                    preflight,
                    request,
                    cancellationToken,
                    contentAdmissionGuard: _quarantine.CreateContentAdmissionGuard(identity),
                    contentFailureCallback: _quarantine.CreateContentFailureCallback(identity),
                    demand: demand).ConfigureAwait(false);
                WorkerResponse response = opened.Response;
                if (response.Status == WorkerStatus.Ok)
                    unownedWorkerDocument = opened.Document;
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
                unownedWorkerDocument = null;
                sourceTransferredToDocument = true;
                preflight = null;
                return MarkdownSvgAdmissionResult.Opened(
                    document,
                    sourceReservationBytes,
                    _snapshotAdmission.AdmissionDomain);
            }
            catch (OperationCanceledException exception)
            {
                throw new MarkdownSvgException(MarkdownSvgFailureReason.Canceled, innerException: exception);
            }
            catch (ObjectDisposedException exception) when (cancellationToken.IsCancellationRequested)
            {
                // Shutdown can close admission after preflight, before its
                // canceled token is checked by the pool. This says nothing
                // about the SVG: do not poison its content hash quarantine.
                throw new MarkdownSvgException(MarkdownSvgFailureReason.Canceled, innerException: exception);
            }
            catch (WorkerInitializationDeadlineException exception)
            {
                // Initialization is process-specific, not evidence that this SVG is
                // hostile. Never quarantine a content hash for a cold-start timeout.
                throw new MarkdownSvgException(MarkdownSvgFailureReason.Timeout, innerException: exception);
            }
            catch (WorkerInitializationException exception)
            {
                throw new MarkdownSvgException(MarkdownSvgFailureReason.WorkerFailure, innerException: exception);
            }
            catch (WorkerDeadlineException exception)
            {
                _quarantine.Add(identity);
                throw new MarkdownSvgException(MarkdownSvgFailureReason.Timeout, innerException: exception);
            }
            catch (MarkdownSvgException)
            {
                throw;
            }
            catch (Exception exception) when (exception is WorkerProtocolException or IOException or InvalidOperationException)
            {
                _quarantine.Add(identity);
                throw new MarkdownSvgException(MarkdownSvgFailureReason.WorkerFailure, innerException: exception);
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
            try
            {
                try
                {
                    if (unownedWorkerDocument is { } workerDocument)
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
                    unownedWorkerDocument = null;
                }
            }
            finally
            {
                try
                {
                    if (!sourceTransferredToDocument)
                        preflight?.ReleaseSource();
                }
                finally
                {
                    // Clear even validation/preflight failures. A retained canceled
                    // or faulted task must not pin the original caller-owned array.
                    try
                    {
                        request = null!;
                        preflight = null;
                        snapshotAdmission?.Dispose();
                        snapshotAdmission = null;
                        openCancellation?.Dispose();
                    }
                    finally
                    {
                        // Release the actual-work borrow last. Async shutdown
                        // cannot finish while this operation still owns its
                        // private snapshot or the caller's source alias.
                        openCancellation = null;
                        openOperation?.Dispose();
                        openOperation = null;
                    }
                }
            }
        }
    }
}
