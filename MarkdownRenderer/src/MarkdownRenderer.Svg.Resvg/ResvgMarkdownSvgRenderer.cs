using System.Runtime.InteropServices;
using MarkdownRenderer.Images;
using MarkdownRenderer.Svg.Resvg.Internal;

namespace MarkdownRenderer.Svg.Resvg;

/// <summary>
/// Renders the bounded static SVG subset in a persistent, kill-on-close resvg worker.
/// </summary>
/// <remarks>
/// Also exposes explicitly registered static WebP decoding through
/// <see cref="IMarkdownRasterDecoder"/> using the same isolated worker pool.
/// Registering this object as an SVG renderer does not implicitly enable raster decoding.
/// </remarks>
public sealed class ResvgMarkdownSvgRenderer : IMarkdownSvgRenderer, IMarkdownRasterDecoder, IMarkdownSvgPreparationAdmission, IDisposable, IAsyncDisposable
{
    /// <summary>The exact upstream resvg release used by the worker.</summary>
    public const string ResvgVersion = "0.48.1";

    /// <summary>The exact upstream source commit used by the worker.</summary>
    public const string ResvgCommit = "68b14c4c3bccdb60344c777406486b54c36ec1a4";

    private readonly ResvgMarkdownSvgRendererOptions _options;
    private readonly WorkerPool _workers;
    private readonly SvgRendererDocumentRegistry _documents = new();
    private readonly SvgProcessSnapshotAdmission _snapshotAdmission;
    private readonly SvgDocumentOpenOperation _opener;
    private readonly SvgRasterDecodeOperation _rasterDecoder;
    // This separate lifetime borrows in-flight opens, including background
    // preflight. It never retains returned document handles or source arrays.
    private readonly SvgDocumentLifetime _openOperations = new(static () => { });
    private readonly TaskCompletionSource _shutdown = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly SvgProviderSnapshotLedger _sourceOwnership;
    private readonly SvgContentQuarantine _quarantine;
    private readonly bool _memoryPressureSubscribed;
    private int _disposed;

    /// <inheritdoc />
    public long CacheGeneration => _workers.FontGeneration;

    /// <inheritdoc />
    public event EventHandler? CacheInvalidated;

    internal SvgProviderSnapshotLedger SourceOwnership => _sourceOwnership;
    internal SvgContentQuarantine Quarantine => _quarantine;
    internal long SourceAttachmentCount => _workers.SourceAttachmentCount;
    internal bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    object IMarkdownSvgAdmission.AdmissionDomain => _snapshotAdmission.AdmissionDomain;

    long IMarkdownSvgAdmission.CapacityVersion => _snapshotAdmission.CapacityVersion;

    Task IMarkdownSvgAdmission.WaitForCapacityAsync(
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

    bool IMarkdownSvgPreparationAdmission.TryReservePreparationScratch(
        MarkdownSvgRenderDemand demand,
        out IMarkdownSvgPreparationLease? lease,
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

    async ValueTask<MarkdownSvgAdmissionResult> IMarkdownSvgAdmission.TryOpenAsync(
        MarkdownSvgOpenRequest request,
        MarkdownSvgRenderDemand demand,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(demand);
        MarkdownSvgOpenRequest forwardedRequest = request with { Priority = demand.Priority };
        try
        {
            return await _opener.OpenAsync(
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

    /// <inheritdoc />
    public MarkdownSvgSourcePreparation PrepareSource(byte[] source, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        return SvgSourcePreparer.Prepare(source, _options.MaxSourceBytes, cancellationToken);
    }

    /// <inheritdoc />
    public bool SupportsFormat(MarkdownRasterFormat format) => format == MarkdownRasterFormat.WebP;

    /// <inheritdoc />
    public async ValueTask<MarkdownRaster> DecodeAsync(MarkdownRasterDecodeRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            // Includes hashing, metadata inspection, worker startup and mapping initialization.
            // None of those synchronous phases may run on a calling UI thread.
            return await Task.Run(() => _rasterDecoder.DecodeAsync(request, cancellationToken),
                CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            request = null!;
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
        SvgProcessSnapshotAdmission snapshotAdmission,
        IWorkerProcessLauncher? workerLauncher = null,
        IWorkerAdmissionObserver? admissionObserver = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _snapshotAdmission = snapshotAdmission ?? throw new ArgumentNullException(nameof(snapshotAdmission));
        _sourceOwnership = new SvgProviderSnapshotLedger(snapshotAdmission.SourceLedger);
        _workers = new WorkerPool(options, workerLauncher, admissionObserver);
        _quarantine = new SvgContentQuarantine(_workers);
        _opener = new SvgDocumentOpenOperation(
            this, options, _workers, _quarantine, snapshotAdmission, _sourceOwnership, _openOperations);
        _rasterDecoder = new SvgRasterDecodeOperation(this, options, _workers, _quarantine, _openOperations);
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
            MarkdownSvgAdmissionResult result = await _opener.OpenAsync(
                request,
                demand: null,
                returnCapacityUnavailable: false,
                cancellationToken).ConfigureAwait(false);
            return result.Document ?? throw new InvalidOperationException(
                "A direct SVG open cannot return a transient host-capacity result.");
        }
        finally
        {
            // The open operation clears its independent parameter copy in its own
            // finally. Clear this async wrapper's copy too: retained completed
            // tasks must not keep the caller's source array reachable.
            request = null!;
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
