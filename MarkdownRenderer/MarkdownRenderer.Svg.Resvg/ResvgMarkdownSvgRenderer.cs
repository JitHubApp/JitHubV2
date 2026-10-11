using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using MarkdownRenderer.Images;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Svg.Resvg.Internal;

namespace MarkdownRenderer.Svg.Resvg;

/// <summary>
/// Renders the bounded static SVG subset in a persistent, kill-on-close resvg worker.
/// </summary>
public sealed class ResvgMarkdownSvgRenderer : IMarkdownSvgRenderer, IMarkdownSvgHostPreparationAdmission, IDisposable, IAsyncDisposable
{
    /// <inheritdoc />
    public MarkdownSvgSourcePreparation PrepareSource(byte[] source, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(source);
        cancellationToken.ThrowIfCancellationRequested();
        byte[] prepared;
        SvgResourceBudgetResult budget;
        SvgSourceMetadata preparedMetadata = default;
        try
        {
            // The lowered source policy applies to sanitized output, not raw
            // branches that the existing inert-image preparation removes.
            prepared = SvgStaticSnapshot.Create(source, cancellationToken, _options.MaxSourceBytes);
            budget = prepared.Length > _options.MaxSourceBytes
                ? SvgResourceBudgetResult.Reject("input-bytes")
                : SvgResourceBudget.ValidateWithMetadata(
                    prepared,
                    cancellationToken,
                    out preparedMetadata);
        }
        catch (SvgStaticSnapshotResourceException exception)
        {
            // Stop serialization before an over-budget buffer is allocated.
            // Never return a partial snapshot or retry with executable input.
            return RejectPreparedSource(source, exception.Reason);
        }
        if (budget.Accepted)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return MarkdownSvgSourcePreparation.AdmitWithMetadata(prepared, preparedMetadata);
        }

        return RejectPreparedSource(source, budget.Reason ?? "invalid-content");
    }

    private static MarkdownSvgSourcePreparation RejectPreparedSource(byte[] source, string reason)
    {
        if (SvgPreflightAuditEvents.Log.IsEnabled())
        {
            SvgPreflightAuditEvents.Log.Rejected(
                reason,
                source.Length,
                Convert.ToHexString(SHA256.HashData(source)));
        }
        MarkdownSvgFailureReason failureReason = IsResourceLimitReason(reason)
            ? MarkdownSvgFailureReason.ResourceLimitExceeded
            : MarkdownSvgFailureReason.UnsupportedContent;
        return MarkdownSvgSourcePreparation.Reject(
            failureReason,
            $"The SVG was rejected by host preflight ({reason}).");
    }

    private static bool IsResourceLimitReason(string reason) =>
        reason.Contains("limit", StringComparison.OrdinalIgnoreCase) ||
        reason.Contains("budget", StringComparison.OrdinalIgnoreCase) ||
        reason.Contains("large", StringComparison.OrdinalIgnoreCase) ||
        reason.Contains("depth", StringComparison.OrdinalIgnoreCase) ||
        reason.Contains("count", StringComparison.OrdinalIgnoreCase) ||
        reason.Contains("bytes", StringComparison.OrdinalIgnoreCase) ||
        reason.Contains("complexity", StringComparison.OrdinalIgnoreCase) ||
        reason.Contains("size", StringComparison.OrdinalIgnoreCase) ||
        reason.Contains("length", StringComparison.OrdinalIgnoreCase) ||
        reason.Contains("deadline", StringComparison.OrdinalIgnoreCase) ||
        reason.Equals("embedded-image-data", StringComparison.OrdinalIgnoreCase);

    /// <summary>The exact upstream resvg release used by the worker.</summary>
    public const string ResvgVersion = "0.48.1";

    /// <summary>The exact upstream source commit used by the worker.</summary>
    public const string ResvgCommit = "68b14c4c3bccdb60344c777406486b54c36ec1a4";

    private readonly ResvgMarkdownSvgRendererOptions _options;
    private readonly WorkerPool _workers;
    private readonly SvgRendererDocumentRegistry _documents = new();
    private readonly SvgProcessSnapshotAdmission _snapshotAdmission;
    // This separate lifetime borrows in-flight opens, including background
    // preflight. It never retains returned document handles or source arrays.
    private readonly SvgDocumentLifetime _openOperations = new(static () => { });
    private readonly TaskCompletionSource _shutdown = new(TaskCreationOptions.RunContinuationsAsynchronously);
#if MARKDOWN_LAYOUT_DIAGNOSTICS
    private readonly SvgProviderSnapshotLedger _sourceOwnership = new(SvgProviderSnapshotLedger.Process);
    internal SvgProviderSnapshotLedger SourceOwnership => _sourceOwnership;
#endif
    private readonly ConcurrentDictionary<string, byte> _quarantine = new(StringComparer.Ordinal);
    private readonly bool _memoryPressureSubscribed;
    private int _disposed;

    /// <inheritdoc />
    public long CacheGeneration => _workers.FontGeneration;

    /// <inheritdoc />
    public event EventHandler? CacheInvalidated;

    internal long SourceAttachmentCount => _workers.SourceAttachmentCount;
    internal bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    object IMarkdownSvgHostAdmission.AdmissionDomain => _snapshotAdmission.AdmissionDomain;

    long IMarkdownSvgHostAdmission.CapacityVersion => _snapshotAdmission.CapacityVersion;

    Task IMarkdownSvgHostAdmission.WaitForCapacityAsync(
        long requiredFreeBytes,
        MarkdownSvgRenderPriority priority,
        CancellationToken cancellationToken)
    {
        long overscanLimit = _snapshotAdmission.CapacityBytes - _snapshotAdmission.VisibleHeadroomBytes;
        if (priority == MarkdownSvgRenderPriority.Overscan &&
            requiredFreeBytes > overscanLimit &&
            requiredFreeBytes <= _snapshotAdmission.CapacityBytes)
        {
            // Overscan can never satisfy this threshold while preserving the
            // visible headroom. The host coordinator races this source-free,
            // cancellable wait against the demand promotion signal; promotion
            // then retries admission at Visible. Keep the low-level admission
            // owner's impossible-tier contract unchanged.
            return Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }

        return _snapshotAdmission.WaitForCapacityAsync(requiredFreeBytes, priority, cancellationToken);
    }

    bool IMarkdownSvgHostPreparationAdmission.TryReserveHostPreparationScratch(
        MarkdownSvgDemandTicket demand,
        out IMarkdownSvgHostPreparationLease? lease,
        out long observedCapacityVersion,
        out long requiredFreeBytes)
    {
        ArgumentNullException.ThrowIfNull(demand);
        ThrowIfDisposed();

        // SvgStaticSnapshot's bounded segmented output and its compact ToArray
        // result overlap. Reserve the full configured ceiling, not raw input
        // length: transformed output is independently allowed up to M.
        requiredFreeBytes = checked(2L * _options.MaxSourceBytes);
        MarkdownSvgRenderPriority priority = demand.Priority;
        bool admitted = _snapshotAdmission.TryReserve(
            requiredFreeBytes,
            priority,
            out SvgProcessSnapshotAdmission.Lease? snapshotLease,
            out observedCapacityVersion);
        if (!admitted && priority == MarkdownSvgRenderPriority.Overscan &&
            demand.Priority == MarkdownSvgRenderPriority.Visible)
        {
            // A visible promotion can race the first nonblocking attempt.
            // Recheck once in the elevated tier without waiting while source
            // bytes are borrowed or publishing a stale overscan denial.
            admitted = _snapshotAdmission.TryReserve(
                requiredFreeBytes,
                MarkdownSvgRenderPriority.Visible,
                out snapshotLease,
                out observedCapacityVersion);
        }
        lease = snapshotLease;
        return admitted;
    }

    async ValueTask<MarkdownSvgHostOpenResult> IMarkdownSvgHostAdmission.OpenForHostAsync(
        MarkdownSvgOpenRequest request,
        MarkdownSvgDemandTicket demand,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(demand);
        MarkdownSvgOpenRequest forwardedRequest = request with { Priority = demand.Priority };
        try
        {
            return await OpenCoreAsync(
                forwardedRequest,
                demand,
                returnCapacityUnavailable: true,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // This wrapper has its own async state machine. Clear its copy as
            // well as OpenCoreAsync's so a completed deferred/faulted task
            // cannot retain caller-owned source bytes.
            request = null!;
            forwardedRequest = null!;
        }
    }

    /// <summary>Creates a renderer with the immutable default safety ceilings.</summary>
    public ResvgMarkdownSvgRenderer()
        : this(new ResvgMarkdownSvgRendererOptions())
    {
    }

    /// <summary>Creates a renderer with ceilings no greater than the immutable defaults.</summary>
    public ResvgMarkdownSvgRenderer(ResvgMarkdownSvgRendererOptions options)
        : this(options, SvgProcessSnapshotAdmission.Process)
    {
    }

    internal ResvgMarkdownSvgRenderer(
        ResvgMarkdownSvgRendererOptions options,
        SvgProcessSnapshotAdmission snapshotAdmission)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _snapshotAdmission = snapshotAdmission ?? throw new ArgumentNullException(nameof(snapshotAdmission));
        _workers = new WorkerPool(options);
        _ = _shutdown.Task.ContinueWith(
            static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        try
        {
            Windows.System.MemoryManager.AppMemoryUsageIncreased += OnAppMemoryUsageIncreased;
            _memoryPressureSubscribed = true;
        }
        catch (COMException)
        {
            // This WinRT event is unavailable in some unpackaged/test hosts.
            // Such hosts can invoke TrimCachesAsync explicitly.
        }
    }

    /// <summary>Starts and validates the primary worker without parsing an SVG.</summary>
    public async Task WarmUpAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        try
        {
            await _workers.WarmUpAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (MarkdownSvgException)
        {
            throw;
        }
        catch (OperationCanceledException exception)
        {
            throw new MarkdownSvgException(MarkdownSvgFailureReason.Canceled, innerException: exception);
        }
        catch (WorkerDeadlineException exception)
        {
            throw new MarkdownSvgException(MarkdownSvgFailureReason.Timeout, innerException: exception);
        }
        catch (ObjectDisposedException exception) when (IsDisposed)
        {
            throw new MarkdownSvgException(MarkdownSvgFailureReason.Canceled, innerException: exception);
        }
        catch (Exception exception)
        {
            throw new MarkdownSvgException(MarkdownSvgFailureReason.WorkerFailure, innerException: exception);
        }
    }

    /// <inheritdoc />
    public async ValueTask<IMarkdownSvgDocument> OpenAsync(
        MarkdownSvgOpenRequest request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            MarkdownSvgHostOpenResult result = await OpenCoreAsync(
                request,
                demand: null,
                returnCapacityUnavailable: false,
                cancellationToken).ConfigureAwait(false);
            return result.Document ?? throw new InvalidOperationException(
                "A direct SVG open cannot return a transient host-capacity result.");
        }
        finally
        {
            // OpenCoreAsync clears its independent parameter copy in its own
            // finally. Clear this async wrapper's copy too: retained completed
            // tasks must not keep the caller's source array reachable.
            request = null!;
        }
    }

    private async ValueTask<MarkdownSvgHostOpenResult> OpenCoreAsync(
        MarkdownSvgOpenRequest request,
        MarkdownSvgDemandTicket? demand,
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
            ThrowIfDisposed();
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
                    return MarkdownSvgHostOpenResult.CapacityUnavailable(
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
#if MARKDOWN_LAYOUT_DIAGNOSTICS
                    , _sourceOwnership
#endif
                    , snapshotAdmission
                    , demand
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
                    return MarkdownSvgHostOpenResult.CapacityUnavailable(
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
            ThrowIfQuarantined(identity);

            try
            {
                WorkerOpenResult opened = await _workers.OpenDocumentAsync(
                    preflight,
                    request,
                    cancellationToken,
                    contentAdmissionGuard: CreateContentAdmissionGuard(identity),
                    contentFailureCallback: CreateContentFailureCallback(identity),
                    demand: demand).ConfigureAwait(false);
                WorkerResponse response = opened.Response;
                if (response.Status == WorkerStatus.Ok)
                    unownedWorkerDocument = opened.Document;
                ThrowForRejectedResponse(response, identity);
                if (!double.IsFinite(response.IntrinsicWidth) || response.IntrinsicWidth <= 0 ||
                    !double.IsFinite(response.IntrinsicHeight) || response.IntrinsicHeight <= 0 ||
                    response.OutputLength != 0 || response.Width != 0 || response.Height != 0)
                {
                    _quarantine.TryAdd(identity, 0);
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
                    this,
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
                return MarkdownSvgHostOpenResult.Opened(
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
                _quarantine.TryAdd(identity, 0);
                throw new MarkdownSvgException(MarkdownSvgFailureReason.Timeout, innerException: exception);
            }
            catch (MarkdownSvgException)
            {
                throw;
            }
            catch (Exception exception) when (exception is WorkerProtocolException or IOException or InvalidOperationException)
            {
                _quarantine.TryAdd(identity, 0);
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

    /// <summary>
    /// Advances the worker font generation after the host observes <c>WM_FONTCHANGE</c>.
    /// Affected parsed trees are invalidated. Subsequent text work requests a
    /// generation-bound background catalog refresh; source documents remain usable.
    /// </summary>
    public void NotifyFontsChanged()
    {
        ThrowIfDisposed();
        _workers.AdvanceFontGeneration();
        RaiseCacheInvalidated();
    }

    /// <summary>Asks the isolated worker to discard all parsed trees and decoded resources.</summary>
    public async Task TrimCachesAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        try
        {
            await _workers.TrimCacheAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception)
        {
            throw new MarkdownSvgException(MarkdownSvgFailureReason.Canceled, innerException: exception);
        }
        catch (WorkerDeadlineException exception)
        {
            throw new MarkdownSvgException(MarkdownSvgFailureReason.Timeout, innerException: exception);
        }
        catch (MarkdownSvgException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new MarkdownSvgException(MarkdownSvgFailureReason.WorkerFailure, innerException: exception);
        }
    }

    /// <summary>
    /// Cancels retained documents, terminates all workers and closes their
    /// kill-on-close jobs. Source cleanup waits for actual admitted renders;
    /// this method does not block awaiting their completion.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            IReadOnlyList<SvgDocumentLifetime>? documents = null;
            List<Exception>? immediateFailures = null;
            List<Exception>? shutdownFailures = null;
            try
            {
                // All capture/allocation work belongs inside the worker
                // teardown finally. A failed close snapshot cannot strand
                // workers or leave subsequent DisposeAsync calls waiting on
                // an unsignaled shutdown barrier.
                try
                {
                    _openOperations.Dispose();
                }
                catch (Exception exception)
                {
                    (immediateFailures ??= []).Add(exception);
                }

                documents = _documents.Close();
                foreach (SvgDocumentLifetime document in documents)
                {
                    try
                    {
                        // Cancellation can reenter the provider. Registry closure
                        // and the shared shutdown barrier are published first, and
                        // callbacks never run under the registry lock.
                        document.Dispose();
                    }
                    catch (Exception exception)
                    {
                        (immediateFailures ??= []).Add(exception);
                    }
                }
                if (_memoryPressureSubscribed)
                {
                    try
                    {
                        Windows.System.MemoryManager.AppMemoryUsageIncreased -= OnAppMemoryUsageIncreased;
                    }
                    catch (COMException)
                    {
                    }
                }
            }
            catch (Exception exception)
            {
                (shutdownFailures ??= []).Add(exception);
                (immediateFailures ??= []).Add(exception);
            }
            finally
            {
                try
                {
                    // Wake/terminate exchanges before awaiting child barriers.
                    // Otherwise an active render could keep shutdown waiting
                    // for the very worker that shutdown needs to stop.
                    _workers.Dispose();
                }
                catch (Exception exception)
                {
                    (shutdownFailures ??= []).Add(exception);
                    (immediateFailures ??= []).Add(exception);
                }
                _ = CompleteShutdownAsync(documents, shutdownFailures);
            }

            if (immediateFailures is { Count: > 0 })
                throw new AggregateException(immediateFailures);
        }
    }

    /// <summary>
    /// Stops the renderer and awaits in-flight opens and source cleanup after
    /// every admitted render of its retained documents has actually exited.
    /// Also observes cleanup started by an earlier synchronous <see cref="Dispose"/>.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        try
        {
            Dispose();
        }
        catch
        {
            // The shared barrier reports cleanup failure only after every
            // admitted child has drained, including when Dispose threw first.
        }
        await _shutdown.Task.ConfigureAwait(false);
    }

    internal bool TryRegisterDocument(SvgDocumentLifetime document) =>
        !IsDisposed && _documents.TryRegister(document);

    private async Task CompleteShutdownAsync(
        IReadOnlyList<SvgDocumentLifetime>? documents, List<Exception>? shutdownFailures)
    {
        try
        {
            await _workers.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            (shutdownFailures ??= []).Add(exception);
        }
        try
        {
            // The open barrier survives a document-snapshot capture failure.
            await _openOperations.RetirementTask.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            (shutdownFailures ??= []).Add(exception);
        }
        if (documents is not null)
        {
            // All cancellation/worker termination was initiated before this
            // method. Awaiting each barrier does not serialize render work and
            // avoids a second shutdown snapshot/task-array allocation that
            // could otherwise skip child cancellation on allocation failure.
            for (int i = 0; i < documents.Count; i++)
            {
                try
                {
                    await documents[i].RetirementTask.ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    (shutdownFailures ??= []).Add(exception);
                }
            }
        }
        documents = null;
        if (shutdownFailures is null)
            _shutdown.TrySetResult();
        else
            _shutdown.TrySetException(shutdownFailures);
    }

    internal void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

    internal void ThrowIfQuarantined(string identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (_quarantine.ContainsKey(identity))
        {
            throw new MarkdownSvgException(
                MarkdownSvgFailureReason.WorkerFailure,
                "This SVG was quarantined after a worker failure.");
        }
    }

    internal Action CreateContentAdmissionGuard(string identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        return () => ThrowIfQuarantined(identity);
    }

    internal Action CreateContentFailureCallback(string identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        return () => QuarantineWorkerFailure(identity);
    }

    internal void ThrowForRejectedResponse(WorkerResponse response, string identity)
    {
        switch (response.Status)
        {
            case WorkerStatus.Ok:
                return;
            case WorkerStatus.Unsupported:
                throw new MarkdownSvgException(MarkdownSvgFailureReason.UnsupportedContent, response.Detail);
            case WorkerStatus.ResourceLimit:
                throw new MarkdownSvgException(MarkdownSvgFailureReason.ResourceLimitExceeded, response.Detail);
            case WorkerStatus.WorkerFailure:
                _quarantine.TryAdd(identity, 0);
                _workers.InvalidateWorker(response.WorkerInstanceId);
                throw new MarkdownSvgException(MarkdownSvgFailureReason.WorkerFailure, response.Detail);
            default:
                _quarantine.TryAdd(identity, 0);
                _workers.InvalidateWorker(response.WorkerInstanceId);
                throw new MarkdownSvgException(MarkdownSvgFailureReason.WorkerFailure, "The SVG worker returned an unknown failure.");
        }
    }

    internal void QuarantineWorkerFailure(string identity, long workerInstanceId = 0)
    {
        _quarantine.TryAdd(identity, 0);
        _workers.InvalidateWorker(workerInstanceId);
    }

    private async void OnAppMemoryUsageIncreased(object? sender, object args)
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;
        try
        {
            await _workers.TrimCacheAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // A memory-pressure notification is advisory. WorkerPool still
            // invalidates a failed worker; the event must never escape into WinRT.
        }
    }

    private void RaiseCacheInvalidated()
    {
        Delegate[] handlers = CacheInvalidated?.GetInvocationList() ?? [];
        foreach (Delegate subscriber in handlers)
        {
            try
            {
                var handler = (EventHandler)subscriber;
                handler(this, EventArgs.Empty);
            }
            catch
            {
                // One host cannot prevent other controls from observing the
                // already-published generation change.
            }
        }
    }
}
