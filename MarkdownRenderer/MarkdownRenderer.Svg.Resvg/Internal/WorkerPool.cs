using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using MarkdownRenderer.Images;
#if MARKDOWN_LAYOUT_DIAGNOSTICS
using MarkdownRenderer.Diagnostics;
#endif

namespace MarkdownRenderer.Svg.Resvg.Internal;

internal sealed partial class WorkerPool : IAsyncDisposable, IDisposable
{
    private readonly ResvgMarkdownSvgRendererOptions _options;
    private readonly WorkerSchedulingQueue<PendingWork> _queue = new();
    private readonly Queue<DateTimeOffset> _startupFailures = new();
    private readonly object _stateLock = new();
    private readonly WorkerSlot _primary = new(isSecondary: false);
    private readonly HashSet<WorkerFontWaitReservation> _detachedFontWaitReservations = [];
    private readonly TimeProvider _timeProvider;
    private readonly Func<bool> _isEnergySaverOn;
    private readonly Func<string, SvgSourceBuffer, SharedMemoryLease>? _sourceMemoryFactory;
    private readonly Func<string, int, SharedMemoryLease>? _outputMemoryFactory;
    private readonly bool _supportsSecondaryHardware;
#if MARKDOWN_LAYOUT_DIAGNOSTICS
    private readonly SvgWorkerPolicyTrace _workerPolicyTrace = new();
#endif
    private readonly Lazy<WindowsWorkerProcess.WorkerJob> _job;
    private readonly CancellationTokenSource _disposeCancellation = new();
    private readonly TaskCompletionSource _shutdown = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly WorkerLifetimeRegistry<WindowsWorkerProcess> _workerLifetimes = new();
    private TaskCompletionSource _queueCapacityChanged =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private WorkerSlot? _secondary;
    private DateTimeOffset _disabledUntil;
    private long _nextRequestId;
    private long _nextDocumentId;
    private long _sourceAttachmentCount;
    private long _fontGeneration;
    private int _detachedFontWaitCount;
    private int _detachedVisibleRenderCount;
    private int _detachedVisibleTextRenderCount;
    private int _disposed;

    public WorkerPool(ResvgMarkdownSvgRendererOptions options)
        : this(options, TimeProvider.System, QueryEnergySaverOn, null, null, useWorkerSecurityContext: true)
    {
    }

    internal WorkerPool(
        ResvgMarkdownSvgRendererOptions options,
        TimeProvider timeProvider,
        Func<bool> isEnergySaverOn,
        Func<string, SvgSourceBuffer, SharedMemoryLease> sourceMemoryFactory,
        Func<string, int, SharedMemoryLease>? outputMemoryFactory = null)
        : this(options, timeProvider, isEnergySaverOn, sourceMemoryFactory, outputMemoryFactory,
            useWorkerSecurityContext: false)
    {
    }

    private WorkerPool(
        ResvgMarkdownSvgRendererOptions options,
        TimeProvider timeProvider,
        Func<bool> isEnergySaverOn,
        Func<string, SvgSourceBuffer, SharedMemoryLease>? sourceMemoryFactory,
        Func<string, int, SharedMemoryLease>? outputMemoryFactory,
        bool useWorkerSecurityContext)
    {
        _options = options;
        _timeProvider = timeProvider;
        _isEnergySaverOn = isEnergySaverOn;
        if (useWorkerSecurityContext && (sourceMemoryFactory is not null || outputMemoryFactory is not null))
            throw new ArgumentException("Production mapping factories must use the selected worker's security context.");
        if (!useWorkerSecurityContext && sourceMemoryFactory is null)
            throw new ArgumentNullException(nameof(sourceMemoryFactory));
        _sourceMemoryFactory = sourceMemoryFactory;
        _outputMemoryFactory = useWorkerSecurityContext ? null : outputMemoryFactory ?? CreateOutputMemory;
        _supportsSecondaryHardware = WorkerSchedulingPolicy.CanUseSecondary(
            RuntimeInformation.ProcessArchitecture,
            Environment.ProcessorCount,
            energySaverOn: false);
        _job = new Lazy<WindowsWorkerProcess.WorkerJob>(
            () => WindowsWorkerProcess.CreateConstrainedJob(
                options.WorkerCommitBytes,
                _supportsSecondaryHardware ? 2 : 1),
            LazyThreadSafetyMode.ExecutionAndPublication);
        WorkerPath = ResolveWorkerPath(options);
    }

    public string WorkerPath { get; }
    public long FontGeneration => Volatile.Read(ref _fontGeneration);
    internal long SourceAttachmentCount => Volatile.Read(ref _sourceAttachmentCount);

    public async Task WarmUpAsync(CancellationToken cancellationToken)
    {
        using WorkerLease lease = await AcquireAsync(
            WorkerOperation.Hello,
            MarkdownSvgRenderPriority.Overscan,
            cancellationToken).ConfigureAwait(false);
        _ = await EnsureWorkerAsync(
            lease
#if MARKDOWN_LAYOUT_DIAGNOSTICS
            , 0, 0, 0, 0, 0
#endif
            ).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
    }

    // This internal operation is consumed once by the renderer. Reuse only
    // the runtime-owned async frame, never the document/source lifetime.
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    public async ValueTask<WorkerOpenResult> OpenDocumentAsync(
        SvgPreflightResult preflight,
        MarkdownSvgOpenRequest openRequest,
        CancellationToken cancellationToken,
        Action? contentAdmissionGuard = null,
        Action? contentFailureCallback = null,
        MarkdownSvgDemandTicket? demand = null)
    {
        WorkerLease? lease = null;
        using var openFailureMarker = new WorkerContentFailureMarker(contentFailureCallback);
        Action? openContentFailureCallback = contentFailureCallback is null
            ? null
            : openFailureMarker.InvokeOnce;
        long requestGeneration = FontGeneration;
        try
        {
            ulong documentId = NextDocumentId();
            ulong requestId = NextRequestId();
    #if MARKDOWN_LAYOUT_DIAGNOSTICS
            long diagnosticWorkId = SvgWorkerStageTrace.CaptureCurrentWorkId();
            long queueStartedAt = SvgWorkerStageTrace.Start(diagnosticWorkId);
    #endif
            lease = await AcquireAsync(
                WorkerOperation.Open,
                demand?.Priority ?? openRequest.Priority,
                cancellationToken,
                requiresFontCatalog: preflight.Info.HasText,
                demandTicket: demand).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            contentAdmissionGuard?.Invoke();
    #if MARKDOWN_LAYOUT_DIAGNOSTICS
            SvgWorkerStageTrace.Record(
                diagnosticWorkId,
                SvgWorkerStageId.PoolSchedulingQueueWait,
                0,
                requestId,
                documentId,
                queueStartedAt,
                sourceBytes: preflight.Source.Length);
    #endif
            WindowsWorkerProcess worker = await EnsureWorkerAsync(
                lease
    #if MARKDOWN_LAYOUT_DIAGNOSTICS
                ,
                diagnosticWorkId,
                requestId,
                documentId,
                preflight.Source.Length,
                0
    #endif
                ).ConfigureAwait(false);
            if (preflight.Info.HasText)
            {
                TextWorkerAdmission textAdmission = await PrepareTextWorkerAdmissionAsync(
                    lease,
                    worker,
                    WorkerOperation.Open,
                    demand?.Priority ?? openRequest.Priority,
                    requestGeneration,
                    cancellationToken,
                    demand
    #if MARKDOWN_LAYOUT_DIAGNOSTICS
                    ,
                    diagnosticWorkId,
                    requestId,
                    documentId,
                    preflight.Source.Length,
                    0
    #endif
                    ).ConfigureAwait(false);
                lease = textAdmission.Lease;
                worker = textAdmission.Worker;
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (preflight.Info.HasText && requestGeneration != FontGeneration)
                throw new OperationCanceledException("The SVG font environment changed before the document was attaching.");
            contentAdmissionGuard?.Invoke();
            WorkerResponse response = await WithSourceMappingAsync(
                _sourceMemoryFactory,
                NewSourceMappingName(),
                preflight.Source,
                sourceMemory =>
                {
                    return AttachDocumentAsync(
                        lease,
                        worker,
                        documentId,
                        preflight,
                        openRequest,
                        sourceMemory,
                        _options.RequestDeadline,
                        cancellationToken,
                        requestId,
                        requestGeneration,
                        openContentFailureCallback,
                        null
    #if MARKDOWN_LAYOUT_DIAGNOSTICS
                        ,
                        diagnosticWorkId,
                        (int)SvgWorkerStageId.OpenTransaction
    #endif
                        ).AsTask();
                },
                cancellationToken,
                contentAdmissionGuard,
                _sourceMemoryFactory is null ? worker.ObjectSecurity : null).ConfigureAwait(false);
            if (preflight.Info.HasText)
            {
                try
                {
                    ValidateTextResponse(response, requestGeneration, FontGeneration, "document open");
                }
                catch (OperationCanceledException) when (response.Status == WorkerStatus.Ok)
                {
                    await ForgetDocumentAsync(worker, documentId).ConfigureAwait(false);
                    throw;
                }
            }
            var document = new WorkerDocumentHandle(
                documentId,
                response.Status == WorkerStatus.Ok ? response.WorkerInstanceId : 0);
            return new WorkerOpenResult(
                response,
                document);
        }
        finally
        {
            lease?.Dispose();
            // The OPEN transaction has drained before this borrowed callback
            // is cleared. A renderer-owned wrapper receives its own guard.
            contentAdmissionGuard = null;
            contentFailureCallback = null;
            openContentFailureCallback = null;
            // These values are borrowed from the document/source owner. The
            // transaction above has drained before this point; do not let the
            // completed/faulted open Task keep those source-bearing objects.
            preflight = null!;
            openRequest = null!;
        }
    }

    public Task<WorkerRenderResult> RenderDocumentAsync(
        WorkerDocumentHandle document,
        SvgPreflightResult preflight,
        MarkdownSvgOpenRequest openRequest,
        MarkdownSvgRenderRequest renderRequest,
        int outputLength,
        CancellationToken cancellationToken,
        MarkdownSvgDemandTicket? demand = null)
    {
        ulong requestId = NextRequestId();
        long requestGeneration = FontGeneration;
#if MARKDOWN_LAYOUT_DIAGNOSTICS
        long targetPixels = (long)renderRequest.TargetWidthPixels * renderRequest.TargetHeightPixels;
        long diagnosticWorkId = SvgWorkerStageTrace.CaptureCurrentWorkId();
        long queueStartedAt = SvgWorkerStageTrace.Start(diagnosticWorkId);
#endif
        return WithAdmittedOutputMappingAsync(
            token => AdmitRenderAsync(
                renderRequest,
                preflight,
                document,
                requestId,
                requestGeneration,
                demand,
#if MARKDOWN_LAYOUT_DIAGNOSTICS
                targetPixels,
                diagnosticWorkId,
                queueStartedAt,
#endif
                token),
            (admission, outputMemory, token) => RenderDocumentAdmittedAsync(
                admission.Lease,
                admission.Worker,
                document,
                preflight,
                openRequest,
                renderRequest,
                outputMemory,
                requestId,
                admission.RequestGeneration,
                token
#if MARKDOWN_LAYOUT_DIAGNOSTICS
                , diagnosticWorkId, targetPixels
#endif
                ).AsTask(),
            _outputMemoryFactory,
            NewOutputMappingName(),
            outputLength,
            cancellationToken,
            _outputMemoryFactory is null
                ? static (RenderAdmission admission) => admission.Worker.ObjectSecurity
                : null);
    }

    private async Task<RenderAdmission> AdmitRenderAsync(
        MarkdownSvgRenderRequest renderRequest,
        SvgPreflightResult preflight,
        WorkerDocumentHandle document,
        ulong requestId,
        long requestGeneration,
        MarkdownSvgDemandTicket? demand,
#if MARKDOWN_LAYOUT_DIAGNOSTICS
        long targetPixels,
        long diagnosticWorkId,
        long queueStartedAt,
#endif
        CancellationToken cancellationToken)
    {
        WorkerLease lease = null!;
        bool transferred = false;
        try
        {
            // Include queue admission in the cleanup region: cancellation or
            // queue rejection here must release the source-bearing arguments
            // just like failures after a worker lease has been acquired.
            lease = await AcquireAsync(
                WorkerOperation.Render,
                demand?.Priority ?? renderRequest.Priority,
                cancellationToken,
                requiresFontCatalog: preflight.Info.HasText,
                preferredWorkerInstanceId: document.PreferredWorkerInstanceId,
                demandTicket: demand).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            document.ValidateContentAdmission();
#if MARKDOWN_LAYOUT_DIAGNOSTICS
            SvgWorkerStageTrace.Record(
                diagnosticWorkId,
                SvgWorkerStageId.PoolSchedulingQueueWait,
                0,
                requestId,
                document.DocumentId,
                queueStartedAt,
                sourceBytes: preflight.Source.Length,
                targetPixels: targetPixels);
#endif
            cancellationToken.ThrowIfCancellationRequested();
            WindowsWorkerProcess worker = await EnsureWorkerAsync(
                lease
#if MARKDOWN_LAYOUT_DIAGNOSTICS
                ,
                diagnosticWorkId,
                requestId,
                document.DocumentId,
                preflight.Source.Length,
                targetPixels).ConfigureAwait(false);
#else
                ).ConfigureAwait(false);
#endif
            cancellationToken.ThrowIfCancellationRequested();
            if (preflight.Info.HasText)
            {
                TextWorkerAdmission textAdmission = await PrepareTextWorkerAdmissionAsync(
                    lease,
                    worker,
                    WorkerOperation.Render,
                    demand?.Priority ?? renderRequest.Priority,
                    requestGeneration,
                    cancellationToken,
                    demand
#if MARKDOWN_LAYOUT_DIAGNOSTICS
                    ,
                    diagnosticWorkId,
                    requestId,
                    document.DocumentId,
                    preflight.Source.Length,
                    targetPixels
#endif
                    ).ConfigureAwait(false);
                lease = textAdmission.Lease;
                worker = textAdmission.Worker;
            }
            else if (requestGeneration != FontGeneration)
            {
                // Geometry does not depend on the font catalog. It can adopt
                // the latest generation without waiting for any font work.
                requestGeneration = FontGeneration;
            }
            cancellationToken.ThrowIfCancellationRequested();
            document.ValidateContentAdmission();

            var admission = new RenderAdmission(lease, worker, requestGeneration);
            transferred = true;
            return admission;
        }
        finally
        {
            try
            {
                if (!transferred)
                    lease?.Dispose();
            }
            finally
            {
                preflight = null!;
                renderRequest = null!;
            }
        }
    }

    private async Task<TextWorkerAdmission> PrepareTextWorkerAdmissionAsync(
        WorkerLease initialLease,
        WindowsWorkerProcess initialWorker,
        WorkerOperation operation,
        MarkdownSvgRenderPriority priority,
        long requestGeneration,
        CancellationToken cancellationToken,
        MarkdownSvgDemandTicket? demand
#if MARKDOWN_LAYOUT_DIAGNOSTICS
        , long diagnosticWorkId,
        ulong diagnosticRequestId,
        ulong diagnosticDocumentId,
        long diagnosticSourceBytes,
        long diagnosticTargetPixels
#endif
        )
    {
        WorkerFontCatalogGeneration.ValidateRequest(requestGeneration);
        cancellationToken.ThrowIfCancellationRequested();
        if (requestGeneration != FontGeneration)
            throw new OperationCanceledException("The SVG font environment changed before font readiness was acquired.");

        if (initialWorker.ReadyFontGeneration == requestGeneration)
            return new TextWorkerAdmission(initialLease, initialWorker);
        if (initialWorker.ReadyFontGeneration > requestGeneration)
            throw new WorkerProtocolException("The SVG worker font generation is newer than the host generation.");

        // Convert the active slot into one bounded logical admission before
        // releasing it. Queue entries remain source-free; this ticket accounts
        // for the caller's still-owned preflight while it waits off-slot.
        var elapsed = Stopwatch.StartNew();
        using var readinessDeadline = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _disposeCancellation.Token);
        readinessDeadline.CancelAfter(WorkerSchedulingPolicy.InitializationDeadline);
        CancellationToken waitToken = readinessDeadline.Token;
        WorkerFontWaitReservation? reservation = ReserveDetachedFontWait(initialLease);
        initialLease.Dispose();
        SemaphoreSlim readinessGate = _primary.FontCatalogGate;
        bool gateEntered = false;
        WorkerLease? candidateLease = null;
        bool transferred = false;
        long initializationWallStarted = 0;
        long workerCpuAtStart = -1;
        WindowsWorkerProcess? lastProbeWorker = null;
        bool lastProbeWasInitialCatalog = false;
#if MARKDOWN_LAYOUT_DIAGNOSTICS
        long fontWaitStartedAt = 0;
#endif
        string initializationPhase = "font-catalog-startup";
        try
        {
#if MARKDOWN_LAYOUT_DIAGNOSTICS
            fontWaitStartedAt = SvgWorkerStageTrace.Start(diagnosticWorkId);
#endif
            await readinessGate.WaitAsync(waitToken).ConfigureAwait(false);
            gateEntered = true;

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                waitToken.ThrowIfCancellationRequested();
                if (requestGeneration != FontGeneration)
                    throw new OperationCanceledException("The SVG font environment changed while waiting for its catalog.");

                // Once the primary's generation is known current, do not hold
                // the refresh gate while this operation waits in the normal
                // queue. That lets concurrently admitted visible text renders
                // build queue pressure and preserves secondary-worker policy.
                if (candidateLease is null && IsPrimaryFontCatalogReady(requestGeneration))
                {
                    readinessGate.Release();
                    gateEntered = false;
                    candidateLease = await AcquireAsync(
                        operation,
                        demand?.Priority ?? priority,
                        waitToken,
                        requiresFontCatalog: true,
                        fontWaitReservation: reservation,
                        demandTicket: demand).ConfigureAwait(false);
                    reservation = null; // Admission atomically consumed the ticket.
                    waitToken.ThrowIfCancellationRequested();

                    WindowsWorkerProcess readyWorker = await EnsureWorkerAsync(
                        candidateLease
#if MARKDOWN_LAYOUT_DIAGNOSTICS
                        , diagnosticWorkId,
                        diagnosticRequestId,
                        diagnosticDocumentId,
                        diagnosticSourceBytes,
                        diagnosticTargetPixels
#endif
                        ).ConfigureAwait(false);
                    if (requestGeneration != FontGeneration)
                        throw new OperationCanceledException("The SVG font environment changed before text work was dispatched.");
                    if (readyWorker.ReadyFontGeneration == requestGeneration)
                    {
                        RebindLeaseCancellation(candidateLease, cancellationToken, waitToken);
                        transferred = true;
                        return new TextWorkerAdmission(candidateLease, readyWorker);
                    }
                    if (readyWorker.ReadyFontGeneration > requestGeneration)
                        throw new WorkerProtocolException("The SVG worker font generation is newer than the host generation.");

                    // A restarted or stale secondary can race the readiness
                    // snapshot. Park this caller again, then serialize one
                    // short HELLO probe under the gate before retrying.
                    reservation = ReserveDetachedFontWait(candidateLease);
                    candidateLease.Dispose();
                    candidateLease = null;
                    await readinessGate.WaitAsync(waitToken).ConfigureAwait(false);
                    gateEntered = true;
                    continue;
                }

                candidateLease = await AcquireAsync(
                    operation,
                    demand?.Priority ?? priority,
                    waitToken,
                    requiresFontCatalog: true,
                    fontWaitReservation: reservation,
                    demandTicket: demand).ConfigureAwait(false);
                reservation = null; // Admission atomically consumed the ticket.
                waitToken.ThrowIfCancellationRequested();

                WindowsWorkerProcess worker = await EnsureWorkerAsync(
                    candidateLease
#if MARKDOWN_LAYOUT_DIAGNOSTICS
                    , diagnosticWorkId,
                    diagnosticRequestId,
                    diagnosticDocumentId,
                    diagnosticSourceBytes,
                    diagnosticTargetPixels
#endif
                    ).ConfigureAwait(false);

                if (requestGeneration != FontGeneration)
                    throw new OperationCanceledException("The SVG font environment changed before its catalog probe.");

                if (worker.ReadyFontGeneration >= 0)
                {
                    if (worker.ReadyFontGeneration > requestGeneration)
                        throw new WorkerProtocolException("The SVG worker published a font generation newer than the host target.");
                    if (worker.ReadyFontGeneration == requestGeneration)
                    {
                        RebindLeaseCancellation(candidateLease, cancellationToken, waitToken);
                        readinessGate.Release();
                        gateEntered = false;
                        transferred = true;
                        return new TextWorkerAdmission(candidateLease, worker);
                    }
                }

                Task catalogSupply = worker.EnsureFontCatalogSuppliedAsync(
                    CreateControlRequest(WorkerOperation.FontCatalog, worker, fontGeneration: requestGeneration),
                    waitToken);
                if (!catalogSupply.IsCompleted)
                {
                    // File enumeration and mapping preparation must not retain
                    // the scheduling slot needed by geometry-only documents.
                    reservation = ReserveDetachedFontWait(candidateLease);
                    candidateLease.Dispose();
                    candidateLease = null;
                    initializationPhase = "font-catalog-mappings";
                    await catalogSupply.ConfigureAwait(false);
                    continue;
                }
                await catalogSupply.ConfigureAwait(false);

                TimeSpan remaining = WorkerSchedulingPolicy.InitializationDeadline - elapsed.Elapsed;
                if (remaining <= TimeSpan.Zero)
                {
                    if (lastProbeWorker is not null && lastProbeWasInitialCatalog)
                        RecordFontCatalogWaitDeadline(
                            lastProbeWorker,
                            workerCpuAtStart,
                            elapsed.Elapsed,
                            initializationPhase);
                    throw new WorkerInitializationDeadlineException(
                        $"The resvg worker did not publish font catalog generation {requestGeneration} before the initialization deadline (phase: {initializationPhase}).",
                        new TimeoutException());
                }

                if (initializationWallStarted == 0)
                {
                    initializationWallStarted = Stopwatch.GetTimestamp();
                    workerCpuAtStart = WorkerTimeoutEvents.Log.IsEnabled()
                        ? worker.GetProcessCpuTicks()
                        : -1;
                }
                lastProbeWorker = worker;
                lastProbeWasInitialCatalog = worker.ReadyFontGeneration < 0;

                WorkerResponse response;
                try
                {
                    response = await worker.ExchangeAsync(
                        CreateControlRequest(
                            WorkerOperation.Hello,
                            worker,
                            fontGeneration: requestGeneration),
                        remaining < _options.RequestDeadline ? remaining : _options.RequestDeadline,
                        waitToken,
                        timeoutEvidenceScope: new WorkerTimeoutEvidenceScope(
                            WorkerSchedulingPolicy.InitializationDeadline,
                            workerCpuAtStart,
                            initializationWallStarted)
#if MARKDOWN_LAYOUT_DIAGNOSTICS
                        , diagnosticWorkId: diagnosticWorkId,
                        diagnosticStageId: (int)SvgWorkerStageId.FontCatalogHelloRoundTrip,
                        diagnosticDocumentId: diagnosticDocumentId,
                        diagnosticSourceBytes: diagnosticSourceBytes,
                        diagnosticTargetPixels: diagnosticTargetPixels
#endif
                        ).ConfigureAwait(false);
                }
                catch (Exception exception) when (IsWorkerTransportFailure(exception))
                {
                    Invalidate(candidateLease.Slot, worker);
                    bool initialCatalog = worker.ReadyFontGeneration < 0;
                    if (initialCatalog)
                        RecordStartupFailure();
                    if (cancellationToken.IsCancellationRequested)
                        throw new OperationCanceledException(cancellationToken);
                    WorkerTimeoutEvents.Log.FontCatalogDeadlinePhase(initializationPhase);
                    throw new WorkerInitializationDeadlineException(
                        $"The resvg worker could not confirm font catalog generation {requestGeneration} (phase: {initializationPhase}).",
                        exception);
                }

                cancellationToken.ThrowIfCancellationRequested();
                waitToken.ThrowIfCancellationRequested();
                if (requestGeneration != FontGeneration)
                    throw new OperationCanceledException("The SVG font environment changed while its catalog probe was active.");
                try
                {
                    ValidateFontCatalogProbeResponse(requestGeneration, response);
                }
                catch (WorkerProtocolException exception)
                {
                    throw InvalidateMalformedFontCatalogProbe(
                        exception,
                        () =>
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            waitToken.ThrowIfCancellationRequested();
                            if (requestGeneration != FontGeneration)
                            {
                                throw new OperationCanceledException(
                                    "The SVG font environment changed while its catalog probe was being validated.");
                            }

                            if (lastProbeWasInitialCatalog)
                                RecordStartupFailure();
                        },
                        () => Invalidate(candidateLease.Slot, worker));
                }

                if (response.Status == WorkerStatus.FontCatalogFailure)
                {
                    if (requestGeneration != FontGeneration)
                        throw new OperationCanceledException("The SVG font environment changed while its catalog probe was active.");
                    throw new WorkerInitializationException(
                        "The resvg worker could not refresh its Windows font catalog.",
                        new WorkerProtocolException(response.Detail.Length == 0
                            ? "The font catalog refresh failed."
                            : response.Detail));
                }

                if (response.Status == WorkerStatus.Ok)
                {
                    worker.MarkFontCatalogReady(response.ReadyFontGeneration);
#if MARKDOWN_LAYOUT_DIAGNOSTICS
                    RecordFontCatalogInitializationPhases(
                        response.Detail,
                        diagnosticWorkId,
                        diagnosticRequestId,
                        diagnosticDocumentId,
                        worker.InstanceId);
#endif
                    readinessGate.Release();
                    gateEntered = false;
                    RebindLeaseCancellation(candidateLease, cancellationToken, waitToken);
                    transferred = true;
                    return new TextWorkerAdmission(candidateLease, worker);
                }

                initializationPhase = response.Detail switch
                {
                    "font-catalog-mappings" or
                    "user-local-fonts" or
                    "user-roaming-fonts" or
                    "font-family-defaults" or
                    "text-pipeline-warmup" => response.Detail,
                    _ => "unknown",
                };

                // Keep the caller's logical admission, but relinquish the only
                // worker lease while native scanning continues asynchronously.
                reservation = ReserveDetachedFontWait(candidateLease);
                candidateLease.Dispose();
                candidateLease = null;
                remaining = WorkerSchedulingPolicy.InitializationDeadline - elapsed.Elapsed;
                if (remaining <= TimeSpan.Zero)
                    continue;
                await Task.Delay(
                    TimeSpan.FromMilliseconds(Math.Min(50, remaining.TotalMilliseconds)),
                    waitToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (OperationCanceledException) when (
            !cancellationToken.IsCancellationRequested && readinessDeadline.IsCancellationRequested)
        {
            if (_disposeCancellation.IsCancellationRequested)
                throw new ObjectDisposedException(nameof(WorkerPool));
            if (lastProbeWorker is not null && lastProbeWasInitialCatalog)
                RecordFontCatalogWaitDeadline(
                    lastProbeWorker,
                    workerCpuAtStart,
                    elapsed.Elapsed,
                    initializationPhase);
            throw new WorkerInitializationDeadlineException(
                $"The resvg worker did not publish font catalog generation {requestGeneration} before the initialization deadline (phase: {initializationPhase}).",
                new TimeoutException());
        }
        finally
        {
            if (candidateLease is not null && !transferred)
                candidateLease.Dispose();
            reservation?.Dispose();
            if (gateEntered)
                readinessGate.Release();
#if MARKDOWN_LAYOUT_DIAGNOSTICS
            if (fontWaitStartedAt != 0)
            {
                SvgWorkerStageTrace.Record(
                    diagnosticWorkId,
                    SvgWorkerStageId.FontCatalogWait,
                    initialWorker.InstanceId,
                    diagnosticRequestId,
                    diagnosticDocumentId,
                    fontWaitStartedAt,
                    sourceBytes: diagnosticSourceBytes,
                    targetPixels: diagnosticTargetPixels);
            }
#endif
        }
    }

    private bool IsPrimaryFontCatalogReady(long requestGeneration)
    {
        lock (_stateLock)
        {
            ThrowIfUnavailableLocked();
            return _primary.Worker is { HasExited: false } worker &&
                worker.ReadyFontGeneration == requestGeneration;
        }
    }

    private static void RecordFontCatalogWaitDeadline(
        WindowsWorkerProcess worker,
        long workerCpuAtStart,
        TimeSpan elapsed,
        string initializationPhase)
    {
        long workerCpuAtTimeout = workerCpuAtStart >= 0 ? worker.GetProcessCpuTicks() : -1;
        int workerCpuMilliseconds = workerCpuAtStart >= 0 && workerCpuAtTimeout >= workerCpuAtStart
            ? (int)Math.Min((workerCpuAtTimeout - workerCpuAtStart) / 10_000, int.MaxValue)
            : -1;
        WindowsWorkerProcess.WorkerMemorySnapshot memory = WorkerTimeoutEvents.Log.IsEnabled()
            ? worker.GetProcessMemorySnapshot()
            : WindowsWorkerProcess.WorkerMemorySnapshot.Unavailable;
        WorkerTimeoutEvents.Log.Timeout(
            (int)WorkerOperation.Hello,
            (int)WorkerSchedulingPolicy.InitializationDeadline.TotalMilliseconds,
            workerCpuMilliseconds,
            transportPhase: -1,
            requestWriteMilliseconds: -1,
            workerExited: worker.HasExited ? 1 : 0,
            openProgressPhase: -1,
            workerWorkingSetKiB: memory.WorkingSetKiB,
            workerPrivateCommitKiB: memory.PrivateCommitKiB,
            workerPageFaults: memory.PageFaults,
            elapsedWallMilliseconds: (int)Math.Min(elapsed.TotalMilliseconds, int.MaxValue),
            workerInputSha256: string.Empty,
            workerExecutableSha256: worker.WorkerExecutableSha256 ?? string.Empty);
        WorkerTimeoutEvents.Log.FontCatalogDeadlinePhase(initializationPhase);
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private async ValueTask<WorkerResponse> RenderDocumentAdmittedAsync(
        WorkerLease lease,
        WindowsWorkerProcess worker,
        WorkerDocumentHandle document,
        SvgPreflightResult preflight,
        MarkdownSvgOpenRequest openRequest,
        MarkdownSvgRenderRequest renderRequest,
        SharedMemoryLease outputMemory,
        ulong requestId,
        long requestGeneration,
        CancellationToken cancellationToken
#if MARKDOWN_LAYOUT_DIAGNOSTICS
        , long diagnosticWorkId,
        long targetPixels
#endif
        )
    {
        try
        {
            using WorkerDocumentStateGate.Lease documentStateLease =
                await worker.DocumentStateGate.EnterAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            document.ValidateContentAdmission();

            if (preflight.Info.HasText && requestGeneration != FontGeneration)
                throw new OperationCanceledException("The SVG font environment changed before rendering.");
            WorkerRequest request = new(
                WorkerOperation.Render,
                requestId,
                worker.NonceLow,
                worker.NonceHigh,
                0,
                preflight.Hash,
                0,
                outputMemory.Memory.Length,
                renderRequest.TargetWidthPixels,
                renderRequest.TargetHeightPixels,
                renderRequest.TileRegion,
                NormalizeLocale(openRequest.Locale),
                openRequest.ColorScheme,
                openRequest.SemanticColor,
                renderRequest.PixelFormat,
                requestGeneration,
                PerWorkerParsedResourceCacheBytes,
                document.DocumentId,
                _options,
                renderRequest.PhysicalViewport);
            try
            {
                // A document can be opened on one worker and rendered on the other.
                // The missing-document probe, the authoritative attach/parse, and
                // the raster operation are separate bounded worker transactions.
                // Charging all three to one shared stopwatch makes an otherwise
                // valid SVG fail nondeterministically during visible image bursts.
                WorkerResponse response = await worker.ExchangeAsync(
                    request,
                    _options.RequestDeadline,
                    CancellationToken.None,
                    outputMemory
    #if MARKDOWN_LAYOUT_DIAGNOSTICS
                    ,
                    diagnosticWorkId: diagnosticWorkId,
                    diagnosticStageId: (int)SvgWorkerStageId.RenderTransaction,
                    diagnosticSourceBytes: preflight.Source.Length,
                    diagnosticTargetPixels: targetPixels
    #endif
                    ).ConfigureAwait(false);
                ValidateRenderResponseBeforeCancellation(
                    response,
                    worker,
                    lease,
                    document,
                    preflight,
                    renderRequest,
                    requestGeneration,
                    outputMemory,
                    cancellationToken);
                ValidateRenderStatus(
                    response,
                    requiresFontCatalog: preflight.Info.HasText,
                    requestGeneration: requestGeneration,
                    documentMissingAllowed: true,
                    operation: "render");
                cancellationToken.ThrowIfCancellationRequested();
                if (preflight.Info.HasText)
                    ValidateTextResponse(response, requestGeneration, FontGeneration, "render");
                WorkerResponse? attachment = await AttachDocumentIfMissingAsync(
                    response,
                    () =>
                    {
                        document.ValidateContentAdmission();
                        return WithSourceMappingAsync(
                            _sourceMemoryFactory,
                            NewSourceMappingName(),
                            preflight.Source,
                            sourceMemory => AttachDocumentAsync(
                                lease,
                                worker,
                                document.DocumentId,
                                preflight,
                                openRequest,
                                sourceMemory,
                                _options.RequestDeadline,
                                cancellationToken,
                                NextRequestId(),
                                requestGeneration,
                                null,
                                document
    #if MARKDOWN_LAYOUT_DIAGNOSTICS
                                ,
                                diagnosticWorkId,
                                (int)SvgWorkerStageId.DocumentMissingAttach
    #endif
                                ).AsTask(),
                            cancellationToken,
                            document.ValidateContentAdmission,
                            _sourceMemoryFactory is null ? worker.ObjectSecurity : null);
                    }).ConfigureAwait(false);
                if (attachment is null)
                    return Stamp(response, worker);
                if (preflight.Info.HasText)
                    ValidateTextResponse(attachment.Value, requestGeneration, FontGeneration, "document reattachment");
                if (attachment.Value.Status != WorkerStatus.Ok)
                    return attachment.Value;
                document.PreferWorker(attachment.Value.WorkerInstanceId);
                cancellationToken.ThrowIfCancellationRequested();
                document.ValidateContentAdmission();

                request = request with { RequestId = NextRequestId() };
                response = await worker.ExchangeAsync(
                    request,
                    _options.RequestDeadline,
                    CancellationToken.None,
                    outputMemory
    #if MARKDOWN_LAYOUT_DIAGNOSTICS
                    ,
                    diagnosticWorkId: diagnosticWorkId,
                    diagnosticStageId: (int)SvgWorkerStageId.RenderTransaction,
                    diagnosticSourceBytes: preflight.Source.Length,
                    diagnosticTargetPixels: targetPixels
    #endif
                    ).ConfigureAwait(false);
                ValidateRenderResponseBeforeCancellation(
                    response,
                    worker,
                    lease,
                    document,
                    preflight,
                    renderRequest,
                    requestGeneration,
                    outputMemory,
                    cancellationToken);
                ValidateRenderStatus(
                    response,
                    requiresFontCatalog: preflight.Info.HasText,
                    requestGeneration: requestGeneration,
                    documentMissingAllowed: false,
                    operation: "render after reattachment");
                cancellationToken.ThrowIfCancellationRequested();
                if (preflight.Info.HasText)
                    ValidateTextResponse(response, requestGeneration, FontGeneration, "render after reattachment");
                return Stamp(response, worker);
            }
            catch (Exception exception) when (IsWorkerTransportFailure(exception))
            {
                bool callerCanceled = cancellationToken.IsCancellationRequested;
                long currentGeneration = FontGeneration;
                bool generationCurrent = !preflight.Info.HasText || requestGeneration == currentGeneration;
                QuarantineContentBeforeWorkerRelease(
                    document,
                    contentFailureCallback: null,
                    callerCanceled: callerCanceled,
                    requiresFontCatalog: preflight.Info.HasText,
                    requestGeneration: requestGeneration,
                    currentGeneration: currentGeneration,
                    invalidateWorker: () => Invalidate(lease.Slot, worker));
                if (callerCanceled)
                    throw new OperationCanceledException(cancellationToken);
                if (preflight.Info.HasText && !generationCurrent)
                    throw new OperationCanceledException("The SVG worker was intentionally rotated after a font change.");
                throw;
            }
        }
        finally
        {
            // The worker transaction and any missing-document reattach have
            // drained before these borrowed source inputs are cleared.
            preflight = null!;
            openRequest = null!;
            renderRequest = null!;
            outputMemory = null!;
        }
    }

    private void ValidateRenderResponseBeforeCancellation(
        WorkerResponse response,
        WindowsWorkerProcess worker,
        WorkerLease lease,
        WorkerDocumentHandle document,
        SvgPreflightResult preflight,
        MarkdownSvgRenderRequest renderRequest,
        long requestGeneration,
        SharedMemoryLease outputMemory,
        CancellationToken cancellationToken)
    {
        if (response.Status == WorkerStatus.Ok)
        {
            ValidateRasterResponse(
                response,
                renderRequest,
                outputMemory.Memory.Length);
        }
        else if (response.Status == WorkerStatus.WorkerFailure)
        {
            QuarantineContentBeforeWorkerRelease(
                document,
                contentFailureCallback: null,
                callerCanceled: cancellationToken.IsCancellationRequested,
                requiresFontCatalog: preflight.Info.HasText,
                requestGeneration: requestGeneration,
                currentGeneration: FontGeneration,
                invalidateWorker: () => Invalidate(lease.Slot, worker));
        }
    }

    internal static void ValidateRasterResponse(
        WorkerResponse response,
        MarkdownSvgRenderRequest renderRequest,
        int expectedOutputLength)
    {
        ArgumentNullException.ThrowIfNull(renderRequest);
        int width = renderRequest.TileRegion?.Width ?? renderRequest.TargetWidthPixels;
        int height = renderRequest.TileRegion?.Height ?? renderRequest.TargetHeightPixels;
        long expectedLength = (long)width * height * 4;
        long expectedStride = (long)width * 4;
        if (response.Status != WorkerStatus.Ok ||
            expectedLength != expectedOutputLength ||
            response.OutputLength != expectedOutputLength ||
            response.Width != width ||
            response.Height != height ||
            response.Stride != expectedStride ||
            response.PixelFormat != renderRequest.PixelFormat)
        {
            throw new WorkerProtocolException("The resvg worker returned malformed raster metadata.");
        }
    }

    internal static void ValidateOpenResponse(WorkerResponse response)
    {
        if (response.Status != WorkerStatus.Ok ||
            !double.IsFinite(response.IntrinsicWidth) || response.IntrinsicWidth <= 0 ||
            !double.IsFinite(response.IntrinsicHeight) || response.IntrinsicHeight <= 0 ||
            response.OutputLength != 0 ||
            response.Width != 0 ||
            response.Height != 0)
        {
            throw new WorkerProtocolException("The resvg worker returned malformed document metadata.");
        }
    }

    internal static void ValidateOpenStatus(
        WorkerResponse response,
        bool requiresFontCatalog,
        long requestGeneration,
        string operation)
    {
        if (requiresFontCatalog)
            ValidateTextResponseMetadata(response, requestGeneration, operation);

        bool statusAllowed = response.Status is
            WorkerStatus.Ok or
            WorkerStatus.Unsupported or
            WorkerStatus.ResourceLimit or
            WorkerStatus.WorkerFailure;
        statusAllowed |= requiresFontCatalog &&
            (response.Status is WorkerStatus.FontCatalogPending or WorkerStatus.FontCatalogFailure);
        if (!statusAllowed)
        {
            throw new WorkerProtocolException($"The worker returned an invalid status for {operation}.");
        }
    }

    internal static void ValidateRenderStatus(
        WorkerResponse response,
        bool requiresFontCatalog,
        long requestGeneration,
        bool documentMissingAllowed,
        string operation)
    {
        if (requiresFontCatalog)
            ValidateTextResponseMetadata(response, requestGeneration, operation);

        bool statusAllowed = response.Status is WorkerStatus.Ok or
            WorkerStatus.Unsupported or
            WorkerStatus.ResourceLimit or
            WorkerStatus.WorkerFailure;
        statusAllowed |= requiresFontCatalog &&
            (response.Status is WorkerStatus.FontCatalogPending or WorkerStatus.FontCatalogFailure);
        statusAllowed |= documentMissingAllowed && response.Status == WorkerStatus.DocumentMissing;
        if (statusAllowed)
        {
            return;
        }

        throw new WorkerProtocolException($"The worker returned an invalid status for {operation}.");
    }

    internal static bool ShouldQuarantineContentFailure(
        bool callerCanceled,
        bool requiresFontCatalog,
        long requestGeneration,
        long currentGeneration) =>
        !callerCanceled && (!requiresFontCatalog || requestGeneration == currentGeneration);

    internal static void QuarantineContentBeforeWorkerRelease(
        WorkerDocumentHandle? document,
        Action? contentFailureCallback,
        bool callerCanceled,
        bool requiresFontCatalog,
        long requestGeneration,
        long currentGeneration,
        Action invalidateWorker)
    {
        ArgumentNullException.ThrowIfNull(invalidateWorker);
        try
        {
            if (ShouldQuarantineContentFailure(
                    callerCanceled,
                    requiresFontCatalog,
                    requestGeneration,
                    currentGeneration))
            {
                if (document is not null)
                    document.QuarantineContent();
                else
                    contentFailureCallback?.Invoke();
            }
        }
        finally
        {
            invalidateWorker();
        }
    }

    public void ForgetDocument(WorkerDocumentHandle document)
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;
        WindowsWorkerProcess[] workers;
        lock (_stateLock)
        {
            workers = new[] { _primary.Worker, _secondary?.Worker }
                .Where(worker => worker is not null && !worker.HasExited)
                .OfType<WindowsWorkerProcess>()
                .Distinct()
                .ToArray();
        }
        foreach (WindowsWorkerProcess worker in workers)
            ObserveDetachedTaskFault(ForgetDocumentAsync(worker, document.DocumentId));
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private async ValueTask<WorkerResponse> AttachDocumentAsync(
        WorkerLease lease,
        WindowsWorkerProcess worker,
        ulong documentId,
        SvgPreflightResult preflight,
        MarkdownSvgOpenRequest openRequest,
        SharedMemoryLease sourceMemory,
        TimeSpan deadline,
        CancellationToken cancellationToken,
        ulong requestId,
        long requestGeneration,
        Action? contentFailureCallback,
        WorkerDocumentHandle? contentDocument
#if MARKDOWN_LAYOUT_DIAGNOSTICS
        , long diagnosticWorkId,
        int diagnosticStageId
#endif
        )
    {
        try
        {
            WorkerFontCatalogGeneration.ValidateRequest(requestGeneration);
            if (preflight.Info.HasText && requestGeneration != FontGeneration)
                throw new OperationCanceledException("The SVG font environment changed before document attachment.");
            WorkerRequest request = new(
                WorkerOperation.Open,
                requestId,
                worker.NonceLow,
                worker.NonceHigh,
                0,
                preflight.Hash,
                preflight.Source.Length,
                0,
                0,
                0,
                null,
                NormalizeLocale(openRequest.Locale),
                openRequest.ColorScheme,
                openRequest.SemanticColor,
                MarkdownSvgPixelFormat.Rgba8Premultiplied,
                requestGeneration,
                PerWorkerParsedResourceCacheBytes,
                documentId,
                _options);
            try
            {
                WorkerResponse response = await worker.ExchangeAsync(
                    request,
                    deadline,
                    CancellationToken.None,
                    sourceMemory
    #if MARKDOWN_LAYOUT_DIAGNOSTICS
                    ,
                    diagnosticWorkId: diagnosticWorkId,
                    diagnosticStageId: diagnosticStageId,
                    diagnosticSourceBytes: preflight.Source.Length
    #endif
                    ).ConfigureAwait(false);
                if (response.Status == WorkerStatus.WorkerFailure)
                {
                    QuarantineContentBeforeWorkerRelease(
                        contentDocument,
                        contentFailureCallback,
                        callerCanceled: cancellationToken.IsCancellationRequested,
                        requiresFontCatalog: preflight.Info.HasText,
                        requestGeneration: requestGeneration,
                        currentGeneration: FontGeneration,
                        invalidateWorker: () => Invalidate(lease.Slot, worker));
                }
                if (response.Status == WorkerStatus.Ok)
                    ValidateOpenResponse(response);
                try
                {
                    ValidateOpenStatus(
                        response,
                        requiresFontCatalog: preflight.Info.HasText,
                        requestGeneration: requestGeneration,
                        operation: "document attachment");
                    cancellationToken.ThrowIfCancellationRequested();
                    if (preflight.Info.HasText)
                    {
                        ValidateTextResponse(
                            response,
                            requestGeneration,
                            FontGeneration,
                            "document attachment");
                    }
                }
                catch (OperationCanceledException) when (response.Status == WorkerStatus.Ok)
                {
                    // The worker committed the OPEN before the caller's cancellation
                    // or font-generation change became observable. Do not strand a
                    // live document binding when OpenDocumentAsync cannot return a
                    // handle that would later send CLOSE.
                    await ForgetDocumentAsync(worker, documentId).ConfigureAwait(false);
                    throw;
                }
                if (response.Status == WorkerStatus.Ok)
                    Interlocked.Increment(ref _sourceAttachmentCount);
                return Stamp(response, worker);
            }
            catch (Exception exception) when (IsWorkerTransportFailure(exception))
            {
                bool callerCanceled = cancellationToken.IsCancellationRequested;
                long currentGeneration = FontGeneration;
                bool generationCurrent = !preflight.Info.HasText || requestGeneration == currentGeneration;
                QuarantineContentBeforeWorkerRelease(
                    contentDocument,
                    contentFailureCallback,
                    callerCanceled: callerCanceled,
                    requiresFontCatalog: preflight.Info.HasText,
                    requestGeneration: requestGeneration,
                    currentGeneration: currentGeneration,
                    invalidateWorker: () => Invalidate(lease.Slot, worker));
                if (callerCanceled)
                    throw new OperationCanceledException(cancellationToken);
                if (preflight.Info.HasText && !generationCurrent)
                    throw new OperationCanceledException("The SVG worker was intentionally rotated after a font change.");
                throw;
            }
        }
        finally
        {
            // The owning mapping helper disposes sourceMemory after this
            // transaction returns; clearing this borrowed reference neither
            // shortens nor extends that mapping lifetime.
            preflight = null!;
            openRequest = null!;
            sourceMemory = null!;
            contentFailureCallback = null;
            contentDocument = null;
        }
    }

    private Task ForgetDocumentAsync(WindowsWorkerProcess worker, ulong documentId) =>
        CloseDocumentOrInvalidateAsync(
            () => worker.ExchangeAsync(
                CreateControlRequest(WorkerOperation.CloseDocument, worker, documentId),
                _options.RequestDeadline,
                CancellationToken.None).AsTask(),
            () => Invalidate(worker));

    private static void ObserveDetachedTaskFault(Task task)
    {
        if (task.IsCompleted)
        {
            if (task.IsFaulted)
                _ = task.Exception;
            return;
        }

        _ = task.ContinueWith(
            static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously | TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);
    }

    internal static async Task CloseDocumentOrInvalidateAsync(
        Func<Task<WorkerResponse>> close,
        Action invalidate)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(close);
            ArgumentNullException.ThrowIfNull(invalidate);
            WorkerResponse response = await close().ConfigureAwait(false);
            if (response.Status != WorkerStatus.Ok || response.OutputLength != 0)
                throw new WorkerProtocolException("The resvg worker rejected a document close request.");
        }
        catch (MarkdownSvgException exception) when (
            exception.Reason == MarkdownSvgFailureReason.ResourceLimitExceeded)
        {
            // ExchangeAsync rejects a request before touching the worker when
            // its bounded transaction queue is full. A failed CLOSE at this
            // point is only a best-effort retirement miss: restarting a healthy
            // worker would abort unrelated OPEN/RENDER requests. The native
            // worker bounds document bindings and evicts uncached documents.
        }
        catch (Exception exception) when (
            IsWorkerTransportFailure(exception) ||
            exception is MarkdownSvgException or OperationCanceledException)
        {
            invalidate();
        }
        finally
        {
            close = null!;
            invalidate = null!;
        }
    }

    private static WorkerResponse Stamp(WorkerResponse response, WindowsWorkerProcess worker) =>
        response with { WorkerInstanceId = worker.InstanceId };

    internal static void ValidateFontCatalogProbeResponse(
        long requestGeneration,
        WorkerResponse response)
    {
        if (response.OutputLength != 0)
            throw new WorkerProtocolException("The resvg worker returned output for font-catalog initialization.");
        if (response.Status is not (
                WorkerStatus.Ok or
                WorkerStatus.FontCatalogPending or
                WorkerStatus.FontCatalogFailure))
        {
            throw new WorkerProtocolException("The resvg worker returned an invalid font-catalog probe status.");
        }

        WorkerFontCatalogProtocolPolicy.ValidateProbeResponse(requestGeneration, response);
        if (response.Status == WorkerStatus.Ok && response.ReadyFontGeneration != requestGeneration)
        {
            throw new WorkerProtocolException(
                "The resvg worker confirmed a font generation different from the host target.");
        }
    }

    internal static WorkerInitializationException InvalidateMalformedFontCatalogProbe(
        WorkerProtocolException malformedResponse,
        Action ensureRequestCurrent,
        Action invalidateWorker)
    {
        ArgumentNullException.ThrowIfNull(malformedResponse);
        ArgumentNullException.ThrowIfNull(ensureRequestCurrent);
        ArgumentNullException.ThrowIfNull(invalidateWorker);

        // Cancellation, deadline expiry, and generation rotation outrank the
        // diagnosis of a completed response. In particular, a stale but
        // otherwise healthy probe must not evict a reusable worker.
        ensureRequestCurrent();
        invalidateWorker();
        return new WorkerInitializationException(
            "The resvg worker returned an invalid font-catalog probe response.",
            malformedResponse);
    }

    internal static void ValidateTextResponse(
        WorkerResponse response,
        long requestGeneration,
        long currentGeneration,
        string operation)
    {
        if (requestGeneration != currentGeneration)
            throw new OperationCanceledException($"The SVG font environment changed during {operation}.");

        ValidateTextResponseMetadata(response, requestGeneration, operation);

        if (response.Status == WorkerStatus.FontCatalogPending)
        {
            throw new OperationCanceledException(
                response.Detail.Length == 0
                    ? $"The resvg worker's font catalog became pending during {operation}."
                    : $"The resvg worker's font catalog changed during {operation}: {response.Detail}");
        }

        if (response.Status == WorkerStatus.FontCatalogFailure)
        {
            throw new WorkerInitializationException(
                $"The resvg worker's font catalog failed during {operation}.",
                new WorkerProtocolException(response.Detail.Length == 0
                    ? "The font catalog refresh failed."
                    : response.Detail));
        }

    }

    private static void ValidateTextResponseMetadata(
        WorkerResponse response,
        long requestGeneration,
        string operation)
    {
        if (response.Status == WorkerStatus.Ok && response.ReadyFontGeneration != requestGeneration)
        {
            throw new WorkerProtocolException($"The resvg worker used an unexpected font generation during {operation}.");
        }

        if ((response.Status is WorkerStatus.FontCatalogPending or WorkerStatus.FontCatalogFailure) &&
            response.ReadyFontGeneration > requestGeneration)
        {
            throw new WorkerProtocolException($"The resvg worker published a future font generation during {operation}.");
        }
    }

    public void AdvanceFontGeneration()
    {
        while (true)
        {
            long current = Volatile.Read(ref _fontGeneration);
            if (current >= WorkerFontCatalogGeneration.MaximumGeneration)
                throw new InvalidOperationException("The SVG font generation reached the protocol limit.");
            if (Interlocked.CompareExchange(ref _fontGeneration, current + 1, current) == current)
                return;
        }
    }

    public async Task TrimCacheAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        cancellationToken.ThrowIfCancellationRequested();
        WindowsWorkerProcess[] workers;
        lock (_stateLock)
        {
            workers = new[] { _primary.Worker, _secondary?.Worker }
                .Where(worker => worker is not null && !worker.HasExited)
                .OfType<WindowsWorkerProcess>()
                .Distinct()
                .ToArray();
        }

        foreach (WindowsWorkerProcess worker in workers)
        {
            try
            {
                using WorkerDocumentStateGate.Lease documentStateLease =
                    await worker.DocumentStateGate.EnterAsync(cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                WorkerResponse response = await worker.ExchangeAsync(
                    CreateControlRequest(WorkerOperation.TrimCache, worker),
                    _options.RequestDeadline,
                    CancellationToken.None).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (response.Status != WorkerStatus.Ok || response.OutputLength != 0)
                    throw new WorkerProtocolException("The resvg worker rejected a cache trim request.");
            }
            catch (Exception exception) when (IsWorkerTransportFailure(exception))
            {
                Invalidate(worker);
                if (cancellationToken.IsCancellationRequested)
                    throw new OperationCanceledException(cancellationToken);
                throw;
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        PendingWork[] queued;
        WindowsWorkerProcess? primary;
        WindowsWorkerProcess? secondary;
        lock (_stateLock)
        {
            queued = _queue.Drain();
            PulseQueueCapacityChangedLocked();
            primary = _primary.Worker;
            secondary = _secondary?.Worker;
            _primary.Worker = null;
            if (_secondary is not null)
            {
                CancelIdleRetirementLocked(_secondary);
                _secondary.Worker = null;
            }
        }

        List<Exception>? failures = null;
        // Wake readiness callers parked outside the bounded work queue (font
        // gate, detached admission, or retry delay) before tearing down workers.
        try { _disposeCancellation.Cancel(); }
        catch (Exception exception) { (failures ??= []).Add(exception); }

        foreach (PendingWork pending in queued)
        {
            try { pending.DisposePromotionRegistration(); }
            catch (Exception exception) { (failures ??= []).Add(exception); }
            pending.Completion.TrySetException(new ObjectDisposedException(nameof(WorkerPool)));
        }
        try { primary?.Dispose(); }
        catch (Exception exception) { (failures ??= []).Add(exception); }
        if (!ReferenceEquals(primary, secondary))
        {
            try { secondary?.Dispose(); }
            catch (Exception exception) { (failures ??= []).Add(exception); }
        }
        try { _workerLifetimes.Dispose(); }
        catch (Exception exception) { (failures ??= []).Add(exception); }
        _ = CompleteWorkerShutdownAsync(primary, secondary, failures);
        if (failures is not null)
            throw new AggregateException(failures);
    }

    public async ValueTask DisposeAsync()
    {
        try { Dispose(); }
        catch (Exception) { /* The shared barrier reports cleanup after owners drain. */ }
        await _shutdown.Task.ConfigureAwait(false);
    }

    private async Task CompleteWorkerShutdownAsync(WindowsWorkerProcess? primary,
        WindowsWorkerProcess? secondary, List<Exception>? failures)
    {
        try
        {
            if (primary is not null)
            {
                try { await primary.DisposeAsync().ConfigureAwait(false); }
                catch (Exception exception) { (failures ??= []).Add(exception); }
            }
            if (secondary is not null && !ReferenceEquals(primary, secondary))
            {
                try { await secondary.DisposeAsync().ConfigureAwait(false); }
                catch (Exception exception) { (failures ??= []).Add(exception); }
            }
            try { await _workerLifetimes.DisposeAsync().ConfigureAwait(false); }
            catch (Exception exception) { (failures ??= []).Add(exception); }
            // A startup admitted before shutdown can still be initializing
            // the lazy job. Keep it alive until every startup/retirement owner
            // drains, then close it exactly once; none can create it afterward.
            try
            {
                if (_job.IsValueCreated)
                    _job.Value.Dispose();
            }
            catch (Exception exception) { (failures ??= []).Add(exception); }
            if (failures is null)
                _shutdown.TrySetResult();
            else
                _shutdown.TrySetException(failures);
        }
        finally
        {
            primary = null;
            secondary = null;
        }
    }

    private async Task<WorkerLease> AcquireAsync(
        WorkerOperation operation,
        MarkdownSvgRenderPriority priority,
        CancellationToken cancellationToken,
        bool requiresFontCatalog = false,
        long preferredWorkerInstanceId = 0,
        bool isSecondaryFontProbe = false,
        WorkerFontWaitReservation? fontWaitReservation = null,
        MarkdownSvgDemandTicket? demandTicket = null)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        cancellationToken.ThrowIfCancellationRequested();
        var pending = new PendingWork(
            operation,
            priority,
            cancellationToken,
            requiresFontCatalog,
            preferredWorkerInstanceId,
            isSecondaryFontProbe,
            demandTicket);
        WorkerLease? admitted = null;
        try
        {
            // Registration is installed before queue publication. An immediate
            // callback may find no membership and return, so PumpLocked also
            // synchronizes the monotonic ticket immediately before dispatch.
            if (demandTicket is not null)
            {
                pending.SetPromotionRegistration(demandTicket.RegisterPromotion(
                    () => PromotePendingVisible(pending)));
            }

            while (true)
            {
                Task? capacityWait = null;
                lock (_stateLock)
                {
                    ThrowIfUnavailableLocked();
                    bool consumesReservation = fontWaitReservation?.IsActive == true;
                    bool canEnter = consumesReservation
                        ? WorkerFontWaitAdmissionPolicy.CanReenter(_queue.Count, _options.QueueCapacity)
                        : WorkerFontWaitAdmissionPolicy.CanAdmitNew(
                            _queue.Count,
                            _detachedFontWaitCount,
                            _options.QueueCapacity);
                    if (!canEnter)
                    {
                        // A full queue is backpressure, not a property of the
                        // SVG: a README with more images than the queue holds
                        // (flags, sponsor avatars) must render them all
                        // (P3-010). Wait for a dispatch, cancellation or
                        // retirement to free a slot; the queue stays bounded.
                        capacityWait = _queueCapacityChanged.Task;
                    }
                    else
                    {
                        pending.SynchronizePriorityFromTicket();
                        _queue.Enqueue(
                            pending,
                            pending.Priority,
                            operation is WorkerOperation.Render or WorkerOperation.RasterDecode,
                            requiresFontCatalog,
                            isOpen: operation == WorkerOperation.Open,
                            preferredWorkerInstanceId: preferredWorkerInstanceId,
                            isSecondaryFontProbe: isSecondaryFontProbe);
                        try
                        {
                            if (consumesReservation)
                            {
                                if (!fontWaitReservation!.TryConsume())
                                    throw new InvalidOperationException("The SVG font-wait admission ticket was already consumed.");
                                if (!_detachedFontWaitReservations.Remove(fontWaitReservation))
                                    throw new InvalidOperationException("The SVG font-wait reservation was not registered with this pool.");
                                _detachedFontWaitCount--;
                                if (fontWaitReservation.CountsAsVisibleRender)
                                    _detachedVisibleRenderCount--;
                                if (fontWaitReservation.CountsAsVisibleTextRender)
                                    _detachedVisibleTextRenderCount--;
                            }

                            if (cancellationToken.CanBeCanceled)
                            {
                                pending.CancellationRegistration = cancellationToken.UnsafeRegister(
                                    static state =>
                                    {
                                        var cancellation = (CancellationState)state!;
                                        cancellation.Pool.Cancel(cancellation.Pending);
                                    },
                                    new CancellationState(this, pending));
                            }
                        }
                        catch
                        {
                            _queue.Remove(pending);
                            if (consumesReservation)
                                PulseQueueCapacityChangedLocked();
                            throw;
                        }
                        PumpLocked();
                    }
                }

                if (capacityWait is not null)
                {
                    await capacityWait.WaitAsync(cancellationToken).ConfigureAwait(false);
                    continue;
                }

                admitted = await AwaitAdmissionAsync(
                    pending.Completion.Task,
                    pending.CancellationRegistration).ConfigureAwait(false);
                return admitted;
            }
        }
        finally
        {
            pending.DisposePromotionRegistration();
            if (admitted is null)
                pending.CancellationRegistration.Dispose();
        }
    }

    private WorkerFontWaitReservation ReserveDetachedFontWait(WorkerLease activeLease)
    {
        ArgumentNullException.ThrowIfNull(activeLease);
        lock (_stateLock)
        {
            ThrowIfUnavailableLocked();
            bool ownsActiveLease = ReferenceEquals(activeLease.Slot.Active, activeLease.Pending) && activeLease.Slot.Busy;
            bool alreadyReserved = activeLease.Pending.HasDetachedFontWaitReservation;
            // Admission was originally bounded for the pool's maximum active
            // slots. Preserve those already-admitted active requests as they
            // detach, even if another slot has just become idle.
            int maximumActiveSlots = _supportsSecondaryHardware ? 2 : 1;
            if (!WorkerFontWaitAdmissionPolicy.CanReserveFromActive(
                    _queue.Count,
                    _detachedFontWaitCount,
                    _options.QueueCapacity,
                    maximumActiveSlots,
                    leaseIsActive: ownsActiveLease,
                    alreadyReserved: alreadyReserved))
            {
                if (!ownsActiveLease)
                    throw new InvalidOperationException("A detached SVG font wait must transfer an active worker lease.");
                if (alreadyReserved)
                    throw new InvalidOperationException("An active SVG worker lease can reserve only one detached font wait.");
                throw new MarkdownSvgException(
                    MarkdownSvgFailureReason.ResourceLimitExceeded,
                    "The SVG worker font-wait admission limit is full.");
            }
            bool visibleRender = (activeLease.Pending.Operation is WorkerOperation.Render or WorkerOperation.RasterDecode) &&
                activeLease.Pending.Priority == MarkdownSvgRenderPriority.Visible;
            bool visibleTextRender = visibleRender && activeLease.Pending.RequiresFontCatalog;
            WorkerFontWaitReservation reservation = null!;
            reservation = new WorkerFontWaitReservation(
                () => ReleaseDetachedFontWait(reservation),
                visibleRender,
                visibleTextRender,
                activeLease.Pending.DemandTicket,
                isRender: activeLease.Pending.Operation is WorkerOperation.Render or WorkerOperation.RasterDecode,
                requiresFontCatalog: activeLease.Pending.RequiresFontCatalog,
                consumed: () => activeLease.Pending.HasDetachedFontWaitReservation = false);
            try
            {
                // Compute every counter before publishing membership. Checked
                // overflow must not leave a registered reservation or a count
                // that has no owner.
                int nextDetachedFontWaitCount = checked(_detachedFontWaitCount + 1);
                int nextVisibleRenderCount = checked(
                    _detachedVisibleRenderCount + (visibleRender ? 1 : 0));
                int nextVisibleTextRenderCount = checked(
                    _detachedVisibleTextRenderCount + (visibleTextRender ? 1 : 0));

                // Register before publishing detached counts/ownership. The
                // callback is asynchronous and will observe the fully-added
                // reservation after this state lock is released.
                if (reservation.DemandTicket is not null)
                {
                    reservation.SetPromotionRegistration(reservation.DemandTicket.RegisterPromotion(
                        () => PromoteDetachedFontWait(reservation)));
                }

                // HashSet.Add can allocate while growing. Publish its
                // membership first so an allocation failure cannot strand
                // accounting or the active-lease transfer marker. Promotion
                // callbacks block on _stateLock until this transaction ends.
                if (!_detachedFontWaitReservations.Add(reservation))
                    throw new InvalidOperationException("The SVG font-wait reservation is already registered.");

                _detachedFontWaitCount = nextDetachedFontWaitCount;
                _detachedVisibleRenderCount = nextVisibleRenderCount;
                _detachedVisibleTextRenderCount = nextVisibleTextRenderCount;
                activeLease.Pending.HasDetachedFontWaitReservation = true;
                return reservation;
            }
            catch
            {
                // Roll back a partial publication, if any, before consuming
                // the local wrapper. Its release callback is membership-gated
                // and therefore cannot decrement another reservation's counts.
                _detachedFontWaitReservations.Remove(reservation);
                reservation.TryConsume();
                throw;
            }
        }
    }

    private void PromoteDetachedFontWait(WorkerFontWaitReservation reservation)
    {
        lock (_stateLock)
        {
            if (!_detachedFontWaitReservations.Contains(reservation) ||
                !reservation.PromoteVisible(out bool addedVisibleRender, out bool addedVisibleTextRender))
            {
                return;
            }

            if (addedVisibleRender)
                _detachedVisibleRenderCount++;
            if (addedVisibleTextRender)
                _detachedVisibleTextRenderCount++;
            PumpLocked();
        }
    }

    private void ReleaseDetachedFontWait(WorkerFontWaitReservation reservation)
    {
        lock (_stateLock)
        {
            if (!_detachedFontWaitReservations.Remove(reservation))
                return;

            bool visibleRender = reservation.CountsAsVisibleRender;
            bool visibleTextRender = reservation.CountsAsVisibleTextRender;
            if (_detachedFontWaitCount <= 0 ||
                (visibleRender && _detachedVisibleRenderCount <= 0) ||
                (visibleTextRender && _detachedVisibleTextRenderCount <= 0))
                throw new InvalidOperationException("The SVG font-wait admission count underflowed.");
            _detachedFontWaitCount--;
            if (visibleRender)
                _detachedVisibleRenderCount--;
            if (visibleTextRender)
                _detachedVisibleTextRenderCount--;
            PulseQueueCapacityChangedLocked();
            if (Volatile.Read(ref _disposed) == 0)
                PumpLocked();
        }
    }

    private void PromotePendingVisible(PendingWork pending)
    {
        lock (_stateLock)
        {
            if (!_queue.Contains(pending) || !pending.PromoteVisibleFromTicket())
                return;
            _queue.PromoteVisible(pending);
            PumpLocked();
        }
    }

    private void PulseQueueCapacityChangedLocked()
    {
        TaskCompletionSource prior = _queueCapacityChanged;
        _queueCapacityChanged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        prior.TrySetResult();
    }

    internal static async Task<TLease> AwaitAdmissionAsync<TLease>(
        Task<TLease> completion,
        CancellationTokenRegistration cancellationRegistration)
    {
        try
        {
            // Successful admission transfers registration ownership to the
            // active lease, which needs it to preempt canceled worker work.
            return await completion.ConfigureAwait(false);
        }
        catch
        {
            // Drained/canceled queue entries never obtain a lease. Detach
            // their callback from a possibly long-lived caller token outside
            // the state lock so disposal can wait for an in-flight callback.
            cancellationRegistration.Dispose();
            throw;
        }
    }

    private void Cancel(PendingWork pending)
    {
        lock (_stateLock)
        {
            if (_queue.Remove(pending))
            {
                pending.DisposePromotionRegistration();
                pending.IsCanceled = true;
                pending.Completion.TrySetCanceled(pending.CancellationToken);
                PulseQueueCapacityChangedLocked();
                PumpLocked();
                return;
            }
            pending.IsCanceled = true;
            EvaluateCanceledBlockingLocked();
        }
    }

    private void RebindLeaseCancellation(
        WorkerLease lease,
        CancellationToken cancellationToken,
        CancellationToken admissionCancellationToken)
    {
        PendingWork pending = lease.Pending;
        CancellationTokenRegistration previous = pending.CancellationRegistration;
        pending.CancellationRegistration = default;
        previous.Dispose();
        // Disposing the old registration drains a racing deadline callback.
        // Never transfer an already-expired admission as uncanceled content
        // work, leaving its stale IsCanceled bit eligible for worker preemption.
        admissionCancellationToken.ThrowIfCancellationRequested();
        pending.CancellationToken = cancellationToken;
        if (cancellationToken.CanBeCanceled)
        {
            pending.CancellationRegistration = cancellationToken.UnsafeRegister(
                static state =>
                {
                    var cancellation = (CancellationState)state!;
                    cancellation.Pool.Cancel(cancellation.Pending);
                },
                new CancellationState(this, pending));
        }
        cancellationToken.ThrowIfCancellationRequested();
    }

    private void PumpLocked()
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;

        // Promotion callbacks are deliberately asynchronous. Reconcile the
        // monotonic ticket before queue counts, secondary-worker decisions,
        // and dequeue so a delayed callback can never dispatch stale overscan.
        _queue.PromoteVisibleWhere(static pending => pending.PromoteVisibleFromTicket());
        foreach (WorkerFontWaitReservation reservation in _detachedFontWaitReservations)
        {
            if (reservation.PromoteVisible(out bool addedVisibleRender, out bool addedVisibleTextRender))
            {
                if (addedVisibleRender)
                    _detachedVisibleRenderCount++;
                if (addedVisibleTextRender)
                    _detachedVisibleTextRenderCount++;
            }
        }

        // A worker that exited while idle is no longer an eligible secondary.
        // Remove it before evaluating the normal two-visible-render gate so a
        // replacement can never be started merely because a slot was retained.
        if (_secondary is { Busy: false } staleSecondary &&
            staleSecondary.Readiness!.HasFailed &&
            _queue.Count == 0)
        {
            CancelIdleRetirementLocked(staleSecondary);
            _secondary = null;
        }
        else if (_secondary is { Busy: false } unavailableSecondary &&
                 (unavailableSecondary.Worker is null || unavailableSecondary.Worker.HasExited) &&
                 !unavailableSecondary.Readiness!.IsWarming &&
                 !unavailableSecondary.Readiness.HasFailed)
        {
            CancelIdleRetirementLocked(unavailableSecondary);
            unavailableSecondary.Readiness.FailWarmup();
            _secondary = null;
        }

        bool progressed;
        do
        {
            progressed = false;

            // Sample power policy once for this scheduling pass, then use the
            // same decision for both dispatch and its diagnostics snapshot.
            bool energySaverOn = SafeQueryEnergySaverOn(out bool energySaverQueryFailed);
            bool canUseSecondary = WorkerSchedulingPolicy.CanUseSecondary(
                RuntimeInformation.ProcessArchitecture,
                Environment.ProcessorCount,
                energySaverOn,
                energySaverQueryFailed);

            if (!_primary.Busy && TryAssignOldestSecondaryAffinityLocked(canUseSecondary))
                progressed = true;

            long primaryInstanceId = _primary.Worker is { HasExited: false } primaryWorker
                ? primaryWorker.InstanceId
                : 0;
            if (!_primary.Busy &&
                _queue.TryDequeuePrimary(primaryInstanceId, out PendingWork? primaryWork))
            {
                AssignLocked(_primary, primaryWork!);
                progressed = true;
            }

            if (_secondary is { } secondary &&
                secondary.Worker is { HasExited: false } secondaryWorker &&
                WorkerSchedulingPolicy.CanDispatchSecondary(
                    canUseSecondary,
                    secondary.Readiness!.CanDispatch(requiresFontCatalog: false),
                    secondary.Busy))
            {
                if (_queue.TryDequeueSecondaryFontProbe(secondaryWorker.InstanceId, out PendingWork? fontProbe))
                {
                    AssignLocked(secondary, fontProbe!);
                    progressed = true;
                }
                // The secondary can help with text only after its own catalog
                // is ready. Until then, it may still consume visible non-text
                // renders without making text wait behind cold initialization.
                bool secondaryFontsReady = WorkerFontCatalogGeneration.IsReadyFor(
                        secondaryWorker.ReadyFontGeneration,
                        FontGeneration) &&
                    WorkerFontCatalogGeneration.IsReadyFor(
                        secondary.Readiness.FontCatalogReadyGeneration,
                        FontGeneration);
                if (secondary.Busy)
                {
                    // The font probe just assigned the slot. It is one short
                    // control exchange; leave the renderer queue untouched.
                }
                else if (secondaryFontsReady)
                {
                    if (_queue.TryDequeueSecondary(
                            secondaryWorker.InstanceId,
                            fontCatalogReady: true,
                            out PendingWork? secondaryWork))
                    {
                        AssignLocked(secondary, secondaryWork!);
                        progressed = true;
                    }
                }
                else if (_queue.TryDequeueSecondary(
                             secondaryWorker.InstanceId,
                             fontCatalogReady: false,
                             out PendingWork? nonTextWork))
                {
                    AssignLocked(secondary, nonTextWork!);
                    progressed = true;
                }
                else if (WorkerSchedulingPolicy.ShouldWarmSecondaryFontCatalog(
                             _primary.Worker?.ReadyFontGeneration ?? -1,
                             secondaryWorker.ReadyFontGeneration,
                             FontGeneration,
                             WorkerFontWaitAdmissionPolicy.CountVisibleDemand(
                                 _queue.VisibleTextRenderCount,
                                 _detachedVisibleTextRenderCount)))
                {
                    BeginSecondaryWarmupLocked(secondary);
                    progressed = secondary.Busy;
                }
            }

            bool shouldStartSecondary = WorkerSchedulingPolicy.ShouldStartSecondary(
                    canUseSecondary,
                    _primary.Busy,
                    _secondary is not null,
                    WorkerFontWaitAdmissionPolicy.CountVisibleDemand(
                        _queue.VisibleRenderCount,
                        _detachedVisibleRenderCount));
#if MARKDOWN_LAYOUT_DIAGNOSTICS
            long decisionSequence = 0;
            MarkdownSvgWorkerPolicySnapshot policySnapshot = default;
            if (_workerPolicyTrace.IsEnabled &&
                (_queue.VisibleOpenCount > 0 || _queue.VisibleRenderCount > 0 || _secondary is not null))
            {
                policySnapshot = CreateWorkerPolicySnapshot(
                    (int)SvgWorkerPolicyPhase.Evaluated,
                    decisionSequence: 0,
                    energySaverOn,
                    energySaverQueryFailed,
                    canUseSecondary,
                    shouldStartSecondary);
                decisionSequence = _workerPolicyTrace.Record(policySnapshot);
            }
#endif
            if (shouldStartSecondary)
            {
                var newSecondary = new WorkerSlot(isSecondary: true);
#if MARKDOWN_LAYOUT_DIAGNOSTICS
                newSecondary.PolicyDecisionSequence = decisionSequence;
                newSecondary.PolicySnapshot = policySnapshot with
                {
                    DecisionSequence = decisionSequence,
                };
#endif
                _secondary = newSecondary;
#if MARKDOWN_LAYOUT_DIAGNOSTICS
                RecordWorkerPolicyPhaseLocked(newSecondary, SvgWorkerPolicyPhase.StartRequested);
#endif
                BeginSecondaryWarmupLocked(_secondary);
                progressed = _secondary.Busy;
            }
        }
        while (progressed);

        EvaluateCanceledBlockingLocked();
    }

    private bool TryAssignOldestSecondaryAffinityLocked(bool machineEligible)
    {
        if (_secondary is not { } secondary ||
            secondary.Worker is not { HasExited: false } worker ||
            secondary.Readiness is not { } readiness)
        {
            return false;
        }

        bool canDispatch = WorkerSchedulingPolicy.CanDispatchSecondary(
            machineEligible,
            readiness.CanDispatch(requiresFontCatalog: false),
            secondary.Busy);
        long requestedGeneration = FontGeneration;
        bool fontCatalogReady =
            WorkerFontCatalogGeneration.IsReadyFor(worker.ReadyFontGeneration, requestedGeneration) &&
            WorkerFontCatalogGeneration.IsReadyFor(readiness.FontCatalogReadyGeneration, requestedGeneration);
        if (!_queue.TryDequeueOldestVisibleForSecondaryAffinity(
                worker.InstanceId,
                fontCatalogReady,
                canDispatch,
                out PendingWork? work))
        {
            return false;
        }

        AssignLocked(secondary, work!);
        return true;
    }

    private void BeginSecondaryWarmupLocked(WorkerSlot slot)
    {
        if (!slot.IsSecondary || slot.Busy || slot.Readiness is null || slot.Readiness.HasFailed)
            return;

        WindowsWorkerProcess? worker = slot.Worker is { HasExited: false } existing
            ? existing
            : null;
        bool workerReady = worker is not null;
        long fontCatalogReadyGeneration = workerReady ? worker!.ReadyFontGeneration : -1;
        if (!slot.Readiness.TryBeginWarmup(workerReady, fontCatalogReadyGeneration))
            return;

        var maintenance = new PendingWork(
            WorkerOperation.Hello,
            MarkdownSvgRenderPriority.Overscan,
            CancellationToken.None);
        AssignLocked(slot, maintenance);
        _ = Task.Run(async () =>
        {
            WorkerLease lease = await maintenance.Completion.Task.ConfigureAwait(false);
            await WarmSecondaryAsync(slot, lease).ConfigureAwait(false);
        });
    }

    private async Task WarmSecondaryAsync(
        WorkerSlot slot,
        WorkerLease lease)
    {
        WindowsWorkerProcess? worker = slot.Worker;
        bool warmupSucceeded = false;
        try
        {
            await slot.Readiness!.WarmAsync(
                async _ =>
                {
                    worker = await EnsureWorkerAsync(
                        lease
#if MARKDOWN_LAYOUT_DIAGNOSTICS
                        , 0, 0, 0, 0, 0
#endif
                        ).ConfigureAwait(false);
                    // Process startup is the only long portion that owns the
                    // slot continuously. A ready worker can serve geometry
                    // while the font catalog coordinator scans in background.
                    slot.Readiness!.MarkWorkerReady();
                    lease.Dispose();
                },
                () => ShouldWarmSecondaryFontCatalog(slot, worker),
                async cancellationToken =>
                {
                    if (worker is null)
                        throw new InvalidOperationException("The secondary SVG worker was not started before font warmup.");

                    return await EnsureSecondaryFontCatalogReadyAsync(
                        slot,
                        worker,
                        cancellationToken).ConfigureAwait(false);
                },
                CancellationToken.None).ConfigureAwait(false);
            warmupSucceeded = true;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // This worker is an optional capacity optimization. A cold-start
            // failure must not fail or replay content; the primary keeps serving
            // the queue under its existing deadlines and quarantine policy.
            slot.Readiness!.FailWarmup();
        }
        finally
        {
            WindowsWorkerProcess? discard = null;
            lock (_stateLock)
            {
                if (ReferenceEquals(_secondary, slot))
                {
#if MARKDOWN_LAYOUT_DIAGNOSTICS
                    RecordWorkerPolicyPhaseLocked(
                        slot,
                        warmupSucceeded ? SvgWorkerPolicyPhase.Ready : SvgWorkerPolicyPhase.Failed);
#endif
                }
                if (ReferenceEquals(_secondary, slot) && !warmupSucceeded)
                {
                    // Keep a failed-slot sentinel until the current queue batch
                    // drains. This prevents an immediate background retry loop
                    // while the primary continues serving visible work.
                    slot.Readiness!.FailWarmup();
                    CancelIdleRetirementLocked(slot);
                    discard = slot.Worker;
                    slot.Worker = null;
                }
            }
            RetireForRestart(discard);
            lease.Dispose();
        }
    }

    private bool ShouldWarmSecondaryFontCatalog(
        WorkerSlot slot,
        WindowsWorkerProcess? worker)
    {
        bool energySaverOn = SafeQueryEnergySaverOn(out bool energySaverQueryFailed);
        if (!WorkerSchedulingPolicy.CanUseSecondary(
                RuntimeInformation.ProcessArchitecture,
                Environment.ProcessorCount,
                energySaverOn,
                energySaverQueryFailed))
        {
            return false;
        }

        lock (_stateLock)
        {
            if (Volatile.Read(ref _disposed) != 0 ||
                !ReferenceEquals(_secondary, slot) ||
                worker is null ||
                !ReferenceEquals(slot.Worker, worker) ||
                worker.HasExited)
            {
                return false;
            }

            long primaryGeneration = _primary.Worker is { HasExited: false } primaryWorker
                ? primaryWorker.ReadyFontGeneration
                : -1;
            if (slot.Readiness!.FontCatalogFailedGeneration >= FontGeneration)
                return false;
            return WorkerSchedulingPolicy.ShouldWarmSecondaryFontCatalog(
                primaryGeneration,
                worker.ReadyFontGeneration,
                FontGeneration,
                WorkerFontWaitAdmissionPolicy.CountVisibleDemand(
                    _queue.VisibleTextRenderCount,
                    _detachedVisibleTextRenderCount));
        }
    }

    private void AssignLocked(WorkerSlot slot, PendingWork pending)
    {
        pending.DisposePromotionRegistration();
        CancelIdleRetirementLocked(slot);
        slot.Busy = true;
        slot.Active = pending;
        pending.ActiveSlot = slot;
        if (!pending.Completion.TrySetResult(new WorkerLease(this, slot, pending)))
        {
            slot.Active = null;
            slot.Busy = false;
            pending.ActiveSlot = null;
        }
        PulseQueueCapacityChangedLocked();
    }

    private async Task<WindowsWorkerProcess> EnsureWorkerAsync(
        WorkerLease lease
#if MARKDOWN_LAYOUT_DIAGNOSTICS
        , long diagnosticWorkId,
        ulong diagnosticRequestId,
        ulong diagnosticDocumentId,
        long diagnosticSourceBytes,
        long diagnosticTargetPixels
#endif
        )
    {
        WindowsWorkerProcess? stale = null;
        WorkerLifetimeRegistry<WindowsWorkerProcess>.Reservation startupOwnership;
        lock (_stateLock)
        {
            ThrowIfUnavailableLocked();
            if (lease.Slot.Worker is { HasExited: false } existing)
                return existing;
            startupOwnership = _workerLifetimes.Reserve();
            stale = lease.Slot.Worker;
            lease.Slot.Worker = null;
        }
        using var startupReservation = startupOwnership;
        RetireForRestart(stale);

        WindowsWorkerProcess created;
#if MARKDOWN_LAYOUT_DIAGNOSTICS
        if (lease.Slot.IsSecondary)
        {
            lock (_stateLock)
            {
                if (ReferenceEquals(_secondary, lease.Slot))
                {
                    RecordWorkerPolicyPhaseLocked(
                        lease.Slot,
                        SvgWorkerPolicyPhase.WorkerLaunchStarted);
                }
            }
        }
        long startupStartedAt = SvgWorkerStageTrace.Start(diagnosticWorkId);
#endif
        try
        {
            created = await WindowsWorkerProcess.StartAsync(
                WorkerPath,
                _options,
                _job.Value,
                _disposeCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_disposeCancellation.IsCancellationRequested)
        {
            // Pool shutdown is cancellation, not a failed worker startup and
            // must not advance the startup-failure circuit breaker.
            throw;
        }
        catch (WorkerDeadlineException)
        {
            RecordStartupFailure();
            throw;
        }
        catch (Exception exception)
        {
            RecordStartupFailure();
            throw new MarkdownSvgException(
                MarkdownSvgFailureReason.WorkerFailure,
                "The isolated SVG worker could not start.",
                exception);
        }

        startupOwnership.Attach(created);
        bool rejectCreated;
        lock (_stateLock)
        {
            rejectCreated = Volatile.Read(ref _disposed) != 0 ||
                !ReferenceEquals(lease.Slot.Active, lease.Pending) ||
                (lease.Slot.KillCanceledWhenStarted &&
                 lease.Pending.IsCanceled &&
                 _queue.VisibleCount > 0);
            if (!rejectCreated)
            {
                lease.Slot.Worker = created;
#if MARKDOWN_LAYOUT_DIAGNOSTICS
                if (lease.Slot.IsSecondary)
                    RecordWorkerPolicyPhaseLocked(lease.Slot, SvgWorkerPolicyPhase.WorkerProcessStarted);
#endif
            }
        }
        if (rejectCreated)
        {
            RetireWorker(created);
            if (lease.Pending.IsCanceled)
                throw new OperationCanceledException(lease.Pending.CancellationToken);
            throw new ObjectDisposedException(nameof(WorkerPool));
        }
#if MARKDOWN_LAYOUT_DIAGNOSTICS
        SvgWorkerStageTrace.Record(
            diagnosticWorkId,
            SvgWorkerStageId.WorkerStartupAndPipeHandshake,
            created.InstanceId,
            diagnosticRequestId,
            diagnosticDocumentId,
            startupStartedAt,
            sourceBytes: diagnosticSourceBytes,
            targetPixels: diagnosticTargetPixels);
#endif
        // Start preparation off-thread, but do not delay non-text requests.
        // The worker observes this cached task even if no text is ever opened.
        _ = created.EnsureFontCatalogSuppliedAsync(
            CreateControlRequest(WorkerOperation.FontCatalog, created, fontGeneration: FontGeneration),
            CancellationToken.None);
        return created;
    }

    private async Task<long> EnsureSecondaryFontCatalogReadyAsync(
        WorkerSlot slot,
        WindowsWorkerProcess initialWorker,
        CancellationToken cancellationToken)
    {
        var elapsed = Stopwatch.StartNew();
        WindowsWorkerProcess worker = initialWorker;
        string initializationPhase = "font-catalog-refresh";
        while (CanContinueSecondaryWarmup(slot, worker))
        {
            cancellationToken.ThrowIfCancellationRequested();
            long targetGeneration = FontGeneration;
            WorkerFontCatalogGeneration.ValidateRequest(targetGeneration);
            if (worker.ReadyFontGeneration == targetGeneration)
                return targetGeneration;

            TimeSpan remaining = WorkerSchedulingPolicy.InitializationDeadline - elapsed.Elapsed;
            if (remaining <= TimeSpan.Zero)
            {
                slot.Readiness!.MarkFontCatalogFailed(targetGeneration);
                WorkerTimeoutEvents.Log.FontCatalogDeadlinePhase(initializationPhase);
                return worker.ReadyFontGeneration;
            }

            await worker.EnsureFontCatalogSuppliedAsync(
                CreateControlRequest(WorkerOperation.FontCatalog, worker, fontGeneration: targetGeneration),
                cancellationToken).ConfigureAwait(false);
            remaining = WorkerSchedulingPolicy.InitializationDeadline - elapsed.Elapsed;
            if (remaining <= TimeSpan.Zero)
            {
                slot.Readiness!.MarkFontCatalogFailed(targetGeneration);
                WorkerTimeoutEvents.Log.FontCatalogDeadlinePhase("font-catalog-mappings");
                return worker.ReadyFontGeneration;
            }

            WorkerLease? lease = await AcquireSecondaryFontProbeAsync(
                slot,
                worker,
                remaining,
                cancellationToken).ConfigureAwait(false);
            if (lease is null)
            {
                if (elapsed.Elapsed >= WorkerSchedulingPolicy.InitializationDeadline)
                {
                    slot.Readiness!.MarkFontCatalogFailed(targetGeneration);
                    WorkerTimeoutEvents.Log.FontCatalogDeadlinePhase(initializationPhase);
                }
                return worker.ReadyFontGeneration;
            }

            WorkerResponse response;
            try
            {
                worker = await EnsureWorkerAsync(
                    lease
#if MARKDOWN_LAYOUT_DIAGNOSTICS
                    , 0, 0, 0, 0, 0
#endif
                    ).ConfigureAwait(false);
                if (!CanContinueSecondaryWarmup(slot, worker))
                    return worker.ReadyFontGeneration;

                targetGeneration = FontGeneration;
                remaining = WorkerSchedulingPolicy.InitializationDeadline - elapsed.Elapsed;
                if (remaining <= TimeSpan.Zero)
                {
                    slot.Readiness!.MarkFontCatalogFailed(targetGeneration);
                    WorkerTimeoutEvents.Log.FontCatalogDeadlinePhase(initializationPhase);
                    return worker.ReadyFontGeneration;
                }

                response = await worker.ExchangeAsync(
                    CreateControlRequest(
                        WorkerOperation.Hello,
                        worker,
                        fontGeneration: targetGeneration),
                    remaining < _options.RequestDeadline ? remaining : _options.RequestDeadline,
                    CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                lease.Dispose();
            }

            if (response.OutputLength != 0)
                throw new WorkerProtocolException("The resvg worker returned output for secondary font-catalog initialization.");
            if (targetGeneration != FontGeneration)
                continue;
            WorkerFontCatalogProtocolPolicy.ValidateProbeResponse(targetGeneration, response);
            if (response.Status == WorkerStatus.FontCatalogFailure)
            {
                // Font enumeration is environmental. Keep the healthy optional
                // process for geometry and retry only after a newer notification.
                slot.Readiness!.MarkFontCatalogFailed(targetGeneration);
                return worker.ReadyFontGeneration;
            }
            if (response.Status == WorkerStatus.Ok)
            {
                if (response.ReadyFontGeneration != targetGeneration)
                    throw new WorkerProtocolException("The secondary worker published a font generation different from the host target.");
                worker.MarkFontCatalogReady(response.ReadyFontGeneration);
                slot.Readiness!.MarkFontCatalogReady(response.ReadyFontGeneration);
#if MARKDOWN_LAYOUT_DIAGNOSTICS
                RecordFontCatalogInitializationPhases(response.Detail, 0, 0, 0, worker.InstanceId);
#endif
                return response.ReadyFontGeneration;
            }
            if (response.Status != WorkerStatus.FontCatalogPending)
                throw new WorkerProtocolException("The secondary worker returned an invalid font-catalog probe status.");

            initializationPhase = response.Detail switch
            {
                "font-catalog-mappings" or
                "user-local-fonts" or
                "user-roaming-fonts" or
                "font-family-defaults" or
                "text-pipeline-warmup" => response.Detail,
                _ => "unknown",
            };
            remaining = WorkerSchedulingPolicy.InitializationDeadline - elapsed.Elapsed;
            if (remaining > TimeSpan.Zero)
                await Task.Delay(
                    TimeSpan.FromMilliseconds(Math.Min(50, remaining.TotalMilliseconds)),
                    cancellationToken).ConfigureAwait(false);
        }

        return worker.ReadyFontGeneration;
    }

    private async Task<WorkerLease?> AcquireSecondaryFontProbeAsync(
        WorkerSlot slot,
        WindowsWorkerProcess worker,
        TimeSpan remaining,
        CancellationToken cancellationToken)
    {
        using var probeCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        probeCancellation.CancelAfter(remaining);
        Task<WorkerLease> acquire = AcquireAsync(
            WorkerOperation.Hello,
            MarkdownSvgRenderPriority.Overscan,
            probeCancellation.Token,
            preferredWorkerInstanceId: worker.InstanceId,
            isSecondaryFontProbe: true);
        bool transferred = false;
        try
        {
            while (!acquire.IsCompleted)
            {
                await Task.WhenAny(acquire, Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken))
                    .ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (probeCancellation.IsCancellationRequested || !CanContinueSecondaryWarmup(slot, worker))
                    return null;
            }

            WorkerLease lease = await acquire.ConfigureAwait(false);
            transferred = true;
            return lease;
        }
        finally
        {
            if (!transferred)
            {
                // Cancellation can race with assignment. Drain and dispose a
                // lease that was granted just before the token was observed.
                await CancelAndDrainAcquireAsync(acquire, probeCancellation.Cancel).ConfigureAwait(false);
            }
        }
    }

    internal static async Task CancelAndDrainAcquireAsync<TLease>(
        Task<TLease> acquire,
        Action cancelAcquire)
        where TLease : class, IDisposable
    {
        try
        {
            ArgumentNullException.ThrowIfNull(acquire);
            ArgumentNullException.ThrowIfNull(cancelAcquire);
            cancelAcquire();
            try
            {
                using TLease abandoned = await acquire.ConfigureAwait(false);
            }
            catch (Exception exception) when (
                exception is OperationCanceledException or
                ObjectDisposedException or
                MarkdownSvgException)
            {
                // The acquire's original outcome owns its failure semantics.
                // Observe only cancellation/late-grant cleanup here.
            }
        }
        finally
        {
            // A retained completed cleanup Task must not retain the original
            // admission Task's lease/slot or its cancellation callback owner.
            acquire = null!;
            cancelAcquire = null!;
        }
    }

    private bool CanContinueSecondaryWarmup(WorkerSlot slot, WindowsWorkerProcess worker)
    {
        bool energySaverOn = SafeQueryEnergySaverOn(out bool energySaverQueryFailed);
        if (!WorkerSchedulingPolicy.CanUseSecondary(
                RuntimeInformation.ProcessArchitecture,
                Environment.ProcessorCount,
                energySaverOn,
                energySaverQueryFailed))
        {
            return false;
        }

        lock (_stateLock)
        {
            return Volatile.Read(ref _disposed) == 0 &&
                ReferenceEquals(_secondary, slot) &&
                ReferenceEquals(slot.Worker, worker) &&
                !worker.HasExited;
        }
    }

#if MARKDOWN_LAYOUT_DIAGNOSTICS
    private static void RecordFontCatalogInitializationPhases(
        string detail,
        long diagnosticWorkId,
        ulong diagnosticRequestId,
        ulong diagnosticDocumentId,
        long workerInstanceId)
    {
        if (diagnosticWorkId <= 0 ||
            !FontCatalogTimingPayload.TryParse(detail, out FontCatalogPhaseTiming[] phases))
        {
            return;
        }

        foreach (FontCatalogPhaseTiming phase in phases)
        {
            SvgWorkerStageTrace.RecordElapsed(
                diagnosticWorkId,
                phase.StageId,
                workerInstanceId,
                diagnosticRequestId,
                diagnosticDocumentId,
                phase.WallStopwatchTicks,
                phase.WorkerCpuTicks);
        }
    }
#endif

    private void Release(WorkerSlot slot, PendingWork pending)
    {
        pending.CancellationRegistration.Dispose();
        lock (_stateLock)
        {
            if (!ReferenceEquals(slot.Active, pending))
                return;
            slot.Active = null;
            slot.Busy = false;
            slot.KillCanceledWhenStarted = false;
            pending.ActiveSlot = null;
            pending.BlockedSince = null;
            if (Volatile.Read(ref _disposed) != 0)
                return;

            PumpLocked();
            if (slot.IsSecondary && !slot.Busy && ReferenceEquals(_secondary, slot))
            {
                bool retainFailedSentinel = WorkerSchedulingPolicy.ShouldRetainFailedSecondaryAfterRelease(
                    slot.Readiness!.HasFailed,
                    _queue.Count);
                if (slot.Worker is null && !retainFailedSentinel)
                {
                    CancelIdleRetirementLocked(slot);
                    _secondary = null;
                }
                else if (slot.Worker is not null)
                {
                    ScheduleIdleRetirementLocked(slot);
                }
            }
        }
    }

    private void EvaluateCanceledBlockingLocked()
    {
        DateTimeOffset now = _timeProvider.GetUtcNow();
        EvaluateCanceledSlotLocked(_primary, now);
        if (_secondary is not null)
            EvaluateCanceledSlotLocked(_secondary, now);
    }

    private void EvaluateCanceledSlotLocked(WorkerSlot slot, DateTimeOffset now)
    {
        PendingWork? active = slot.Active;
        if (active is null || !active.IsCanceled || _queue.VisibleCount == 0)
        {
            if (active is not null)
                active.BlockedSince = null;
            slot.KillCanceledWhenStarted = false;
            return;
        }

        active.BlockedSince ??= now;
        if (active.CancellationCheckScheduled)
            return;
        active.CancellationCheckScheduled = true;
        _ = CheckCanceledBlockingAsync(slot, active);
    }

    private async Task CheckCanceledBlockingAsync(WorkerSlot slot, PendingWork pending)
    {
        TimeSpan delay;
        lock (_stateLock)
        {
            DateTimeOffset now = _timeProvider.GetUtcNow();
            DateTimeOffset since = pending.BlockedSince ?? now;
            delay = WorkerSchedulingPolicy.CanceledVisibleBlockingGrace - (now - since);
            if (delay < TimeSpan.Zero)
                delay = TimeSpan.Zero;
        }

        try
        {
            await Task.Delay(delay, _timeProvider, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            return;
        }

        WindowsWorkerProcess? worker = null;
        bool reschedule = false;
        lock (_stateLock)
        {
            pending.CancellationCheckScheduled = false;
            if (!ReferenceEquals(slot.Active, pending) ||
                pending.BlockedSince is not { } blockedSince ||
                !pending.IsCanceled ||
                _queue.VisibleCount == 0)
            {
                pending.BlockedSince = null;
                slot.KillCanceledWhenStarted = false;
                return;
            }

            DateTimeOffset now = _timeProvider.GetUtcNow();
            if (!WorkerSchedulingPolicy.ShouldTerminateCanceledActive(
                    true,
                    _queue.VisibleCount,
                    blockedSince,
                    now))
            {
                reschedule = true;
            }
            else if (slot.Worker is null)
            {
                slot.KillCanceledWhenStarted = true;
            }
            else
            {
                worker = slot.Worker;
                slot.Worker = null;
            }

            if (reschedule)
            {
                pending.CancellationCheckScheduled = true;
                _ = CheckCanceledBlockingAsync(slot, pending);
            }
        }
        RetireWorker(worker);
    }

    private void ScheduleIdleRetirementLocked(WorkerSlot slot)
    {
        if (slot.IdleRetirement is not null)
            return;
        slot.IdleSince = _timeProvider.GetUtcNow();
        var cancellation = new CancellationTokenSource();
        slot.IdleRetirement = cancellation;
        _ = RetireSecondaryAfterIdleAsync(slot, cancellation);
    }

    private async Task RetireSecondaryAfterIdleAsync(
        WorkerSlot slot,
        CancellationTokenSource cancellation)
    {
        try
        {
            await Task.Delay(
                WorkerSchedulingPolicy.SecondaryIdleTimeout,
                _timeProvider,
                cancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        WindowsWorkerProcess? worker = null;
        lock (_stateLock)
        {
            if (!ReferenceEquals(_secondary, slot) ||
                slot.Busy ||
                !ReferenceEquals(slot.IdleRetirement, cancellation) ||
                !WorkerSchedulingPolicy.ShouldRetireSecondary(slot.IdleSince, _timeProvider.GetUtcNow()))
            {
                return;
            }
            slot.IdleRetirement = null;
            worker = slot.Worker;
            slot.Worker = null;
            _secondary = null;
        }
        cancellation.Dispose();
        RetireWorker(worker);
    }

    private void CancelIdleRetirementLocked(WorkerSlot slot)
    {
        CancellationTokenSource? cancellation = slot.IdleRetirement;
        slot.IdleRetirement = null;
        if (cancellation is null)
            return;
        cancellation.Cancel();
        cancellation.Dispose();
    }

    private WorkerRequest CreateControlRequest(
        WorkerOperation operation,
        WindowsWorkerProcess worker,
        ulong documentId = 0,
        long? fontGeneration = null) =>
        new(
            operation,
            NextRequestId(),
            worker.NonceLow,
            worker.NonceHigh,
            0,
            new byte[32],
            0,
            0,
            0,
            0,
            null,
            string.Empty,
            MarkdownSvgColorScheme.Light,
            null,
            MarkdownSvgPixelFormat.Rgba8Premultiplied,
            fontGeneration ?? FontGeneration,
            PerWorkerParsedResourceCacheBytes,
            documentId,
            _options);

    public void InvalidateWorker(long workerInstanceId)
    {
        if (workerInstanceId == 0)
            return;
        WindowsWorkerProcess? worker;
        lock (_stateLock)
        {
            worker = _primary.Worker?.InstanceId == workerInstanceId
                ? _primary.Worker
                : _secondary?.Worker?.InstanceId == workerInstanceId
                    ? _secondary.Worker
                    : null;
        }
        if (worker is not null)
            Invalidate(worker);
    }

    private void Invalidate(WorkerSlot slot, WindowsWorkerProcess worker)
    {
        lock (_stateLock)
        {
            if (ReferenceEquals(slot.Worker, worker))
                slot.Worker = null;
        }
        RetireForRestart(worker);
    }

    private void Invalidate(WindowsWorkerProcess worker)
    {
        lock (_stateLock)
        {
            if (ReferenceEquals(_primary.Worker, worker))
                _primary.Worker = null;
            if (_secondary is not null && ReferenceEquals(_secondary.Worker, worker))
                _secondary.Worker = null;
        }
        RetireForRestart(worker);
    }

    private void RetireWorker(WindowsWorkerProcess? worker)
    {
        if (worker is null)
            return;
        try { worker.Dispose(); }
        finally { _workerLifetimes.Retire(worker); }
    }

    private void RetireForRestart(WindowsWorkerProcess? worker)
    {
        if (worker is null)
            return;
        try { worker.DisposeForRestart(); }
        finally { _workerLifetimes.Retire(worker); }
    }

    private void RecordStartupFailure()
    {
        DateTimeOffset now = _timeProvider.GetUtcNow();
        lock (_stateLock)
        {
            while (_startupFailures.TryPeek(out DateTimeOffset oldest) &&
                   now - oldest > TimeSpan.FromSeconds(60))
            {
                _startupFailures.Dequeue();
            }
            _startupFailures.Enqueue(now);
            if (_startupFailures.Count >= 3)
            {
                _disabledUntil = now.AddSeconds(60);
                _startupFailures.Clear();
            }
        }
    }

    private void ThrowIfUnavailableLocked()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (_disabledUntil > _timeProvider.GetUtcNow())
        {
            throw new MarkdownSvgException(
                MarkdownSvgFailureReason.WorkerFailure,
                "The SVG provider is temporarily disabled after repeated startup failures.");
        }
    }

    private bool SafeQueryEnergySaverOn(out bool queryFailed)
    {
        try
        {
            queryFailed = false;
            return _isEnergySaverOn();
        }
        catch (Exception exception) when (IsRecoverablePowerQueryFailure(exception))
        {
            // Power state is an optimization gate, not a prerequisite for
            // rendering. If a recoverable platform/query failure occurs, keep
            // the pool on one worker for this scheduling pass. This also keeps
            // the failed PendingWork queued/owned by AcquireAsync instead of
            // letting a query exception escape after enqueue and strand it.
            queryFailed = true;
            return true;
        }
    }

    private static bool IsRecoverablePowerQueryFailure(Exception exception)
    {
        // Do not hide fatal resource/process failures behind the conservative
        // single-worker fallback. Check aggregate children too because a
        // platform query wrapper may report a fatal inner exception.
        if (exception is OutOfMemoryException or InsufficientMemoryException or
            InsufficientExecutionStackException or StackOverflowException or
            AccessViolationException or SEHException)
        {
            return false;
        }

        if (exception is AggregateException aggregate)
        {
            foreach (Exception innerException in aggregate.InnerExceptions)
            {
                if (!IsRecoverablePowerQueryFailure(innerException))
                    return false;
            }

            return true;
        }

        return exception.InnerException is null ||
            IsRecoverablePowerQueryFailure(exception.InnerException);
    }

#if MARKDOWN_LAYOUT_DIAGNOSTICS
    private MarkdownSvgWorkerPolicySnapshot CreateWorkerPolicySnapshot(
        int phase,
        long decisionSequence,
        bool energySaverOn,
        bool energySaverQueryFailed,
        bool canUseSecondary,
        bool shouldStartSecondary)
    {
        WorkerSlot? secondary = _secondary;
        WindowsWorkerProcess? primaryWorker = _primary.Worker;
        WindowsWorkerProcess? secondaryWorker = secondary?.Worker;
        long requestedFontGeneration = FontGeneration;
        int secondaryReadinessState = secondary switch
        {
            null => 0,
            { Readiness.HasFailed: true } => 3,
            { Readiness.IsWarming: true } => 1,
            { Readiness.IsWorkerReady: true } => 2,
            _ => 1,
        };

        return new MarkdownSvgWorkerPolicySnapshot(
            Phase: phase,
            DecisionSequence: decisionSequence,
            ProcessArchitecture: (int)RuntimeInformation.ProcessArchitecture,
            LogicalProcessorCount: Environment.ProcessorCount,
            EnergySaverOn: energySaverOn ? 1 : 0,
            EnergySaverQueryFailed: energySaverQueryFailed ? 1 : 0,
            CanUseSecondary: canUseSecondary ? 1 : 0,
            PrimaryBusy: _primary.Busy ? 1 : 0,
            PrimaryWorkerState: GetWorkerProcessState(primaryWorker),
            PrimaryWorkerInstanceId: primaryWorker?.InstanceId ?? 0,
            PrimaryFontCatalogReady: primaryWorker is not null &&
                WorkerFontCatalogGeneration.IsReadyFor(primaryWorker.ReadyFontGeneration, requestedFontGeneration)
                    ? 1
                    : 0,
            VisibleOpenCount: _queue.VisibleOpenCount,
            VisibleRenderCount: _queue.VisibleRenderCount,
            SecondaryReadinessState: secondaryReadinessState,
            SecondaryBusy: secondary?.Busy == true ? 1 : 0,
            SecondaryWorkerState: GetWorkerProcessState(secondaryWorker),
            SecondaryWorkerInstanceId: secondaryWorker?.InstanceId ?? 0,
            SecondaryFontCatalogReady:
                secondaryWorker is not null && secondary?.Readiness is { } readiness &&
                WorkerFontCatalogGeneration.IsReadyFor(secondaryWorker.ReadyFontGeneration, requestedFontGeneration) &&
                WorkerFontCatalogGeneration.IsReadyFor(readiness.FontCatalogReadyGeneration, requestedFontGeneration)
                    ? 1
                    : 0,
            ShouldStartSecondary: shouldStartSecondary ? 1 : 0);
    }

    private void RecordWorkerPolicyPhaseLocked(WorkerSlot slot, SvgWorkerPolicyPhase phase)
    {
        if (slot.PolicyDecisionSequence == 0)
            return;

        MarkdownSvgWorkerPolicySnapshot current = CreateWorkerPolicySnapshot(
            (int)phase,
            slot.PolicyDecisionSequence,
            slot.PolicySnapshot.EnergySaverOn != 0,
            slot.PolicySnapshot.EnergySaverQueryFailed != 0,
            slot.PolicySnapshot.CanUseSecondary != 0,
            slot.PolicySnapshot.ShouldStartSecondary != 0);
        _workerPolicyTrace.Record(current);
    }

    private static int GetWorkerProcessState(WindowsWorkerProcess? worker) => worker switch
    {
        null => 0,
        { HasExited: true } => 2,
        _ => 1,
    };
#endif

    private static bool QueryEnergySaverOn() =>
        Windows.System.Power.PowerManager.EnergySaverStatus ==
        Windows.System.Power.EnergySaverStatus.On;

    private static SharedMemoryLease CreateSourceMemory(string name, SvgSourceBuffer source) =>
        new(name, source, outputLength: 0);

    internal static async Task<TResult> WithSourceMappingAsync<TResult>(
        Func<string, SvgSourceBuffer, SharedMemoryLease>? sourceMemoryFactory,
        string sourceMemoryName,
        SvgSourceBuffer source,
        Func<SharedMemoryLease, Task<TResult>> operation,
        CancellationToken cancellationToken,
        Action? contentAdmissionGuard = null,
        WorkerObjectSecurity.WorkerObjectSecurityContext? securityContext = null)
    {
        SharedMemoryLease? sourceMemory = null;
        try
        {
            if ((sourceMemoryFactory is null) == (securityContext is null))
                throw new ArgumentException("Exactly one source mapping factory or worker security context is required.");
            ArgumentException.ThrowIfNullOrWhiteSpace(sourceMemoryName);
            ArgumentNullException.ThrowIfNull(source);
            ArgumentNullException.ThrowIfNull(operation);
            cancellationToken.ThrowIfCancellationRequested();
            contentAdmissionGuard?.Invoke();

            sourceMemory = (securityContext is null
                    ? sourceMemoryFactory!(sourceMemoryName, source)
                    : new SharedMemoryLease(
                        sourceMemoryName,
                        source,
                        outputLength: 0,
                        securityContext: securityContext)) ??
                throw new InvalidOperationException("The SVG source-memory factory returned no mapping.");
            cancellationToken.ThrowIfCancellationRequested();
            // Quarantine can be published while the mapping factory runs.
            // Revalidate before the operation can send an OPEN/reattachment.
            contentAdmissionGuard?.Invoke();
            return await operation(sourceMemory).ConfigureAwait(false);
        }
        finally
        {
            // Clear source-bearing parameters before disposing the mapping so
            // even disposal faults cannot leave them on a completed Task.
            SharedMemoryLease? mapping = sourceMemory;
            sourceMemory = null;
            source = SvgSourceBuffer.Empty;
            sourceMemoryFactory = null;
            operation = null!;
            contentAdmissionGuard = null;
            sourceMemoryName = string.Empty;
            if (mapping is not null)
                ((IDisposable)mapping).Dispose();
        }
    }

    internal static async Task<WorkerRenderResult> WithAdmittedOutputMappingAsync<TAdmission>(
        Func<CancellationToken, Task<TAdmission>> admitAsync,
        Func<TAdmission, SharedMemoryLease, CancellationToken, Task<WorkerResponse>> renderAsync,
        Func<string, int, SharedMemoryLease>? outputMemoryFactory,
        string outputMemoryName,
        int outputLength,
        CancellationToken cancellationToken,
        Func<TAdmission, WorkerObjectSecurity.WorkerObjectSecurityContext>? securityContextFactory = null)
        where TAdmission : IDisposable
    {
        TAdmission admission = default!;
        bool hasAdmission = false;
        SharedMemoryLease? outputMemory = null;
        try
        {
            ArgumentNullException.ThrowIfNull(admitAsync);
            ArgumentNullException.ThrowIfNull(renderAsync);
            if ((outputMemoryFactory is null) == (securityContextFactory is null))
                throw new ArgumentException("Exactly one output mapping factory or worker security-context selector is required.");
            ArgumentException.ThrowIfNullOrWhiteSpace(outputMemoryName);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(outputLength);
            cancellationToken.ThrowIfCancellationRequested();

            admission = await admitAsync(cancellationToken).ConfigureAwait(false);
            hasAdmission = true;
            cancellationToken.ThrowIfCancellationRequested();
            outputMemory = (securityContextFactory is null
                    ? outputMemoryFactory!(outputMemoryName, outputLength)
                    : new SharedMemoryLease(
                        outputMemoryName,
                        outputLength,
                        securityContext: securityContextFactory(admission))) ??
                throw new InvalidOperationException("The SVG output-memory factory returned no mapping.");
            cancellationToken.ThrowIfCancellationRequested();
            WorkerResponse response = await renderAsync(admission, outputMemory, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var result = new WorkerRenderResult(response, outputMemory);
            outputMemory = null;
            return result;
        }
        finally
        {
            try
            {
                if (outputMemory is not null)
                    ((IDisposable)outputMemory).Dispose();
            }
            finally
            {
                try
                {
                    if (hasAdmission)
                        ((IDisposable)admission).Dispose();
                }
                finally
                {
                    // Delegates supplied by RenderDocumentAsync close over
                    // preflight/openRequest and can otherwise keep their
                    // source arrays alive as long as this completed Task.
                    outputMemory = null;
                    admission = default!;
                    admitAsync = null!;
                    renderAsync = null!;
                    outputMemoryFactory = null;
                    outputMemoryName = string.Empty;
                }
            }
        }
    }

    internal static async Task<WorkerResponse?> AttachDocumentIfMissingAsync(
        WorkerResponse response,
        Func<Task<WorkerResponse>> attach)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(attach);
            return response.Status == WorkerStatus.DocumentMissing
                ? await attach().ConfigureAwait(false)
                : null;
        }
        finally
        {
            // The no-reattach path must release the unused source-bearing
            // closure just as reliably as success, failure, and cancellation.
            attach = null!;
        }
    }

    private static string NewSourceMappingName() =>
        $"Local\\MarkdownRenderer.Resvg.{Environment.ProcessId}.{Guid.NewGuid():N}";

    private static SharedMemoryLease CreateOutputMemory(string name, int outputLength) =>
        new(name, outputLength);

    private static string NewOutputMappingName() =>
        $"Local\\MarkdownRenderer.Resvg.Output.{Environment.ProcessId}.{Guid.NewGuid():N}";

    private static bool IsWorkerTransportFailure(Exception exception) =>
        exception is WorkerProtocolException or WorkerDeadlineException or IOException or InvalidOperationException;

    private ulong NextRequestId() => checked((ulong)Interlocked.Increment(ref _nextRequestId));

    private ulong NextDocumentId() => checked((ulong)Interlocked.Increment(ref _nextDocumentId));

    private long PerWorkerParsedResourceCacheBytes => _supportsSecondaryHardware
        ? Math.Max(1, _options.ParsedResourceCacheBytes / 2)
        : _options.ParsedResourceCacheBytes;

    private static string NormalizeLocale(string? locale)
    {
        string normalized = string.IsNullOrWhiteSpace(locale) ? "en" : locale.Trim();
        if (normalized.Length > 63 ||
            !normalized.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_'))
        {
            throw new MarkdownSvgException(
                MarkdownSvgFailureReason.UnsupportedContent,
                "The SVG locale is invalid.");
        }
        return normalized;
    }

    private static string ResolveWorkerPath(ResvgMarkdownSvgRendererOptions options)
    {
        if (!OperatingSystem.IsWindows() ||
            RuntimeInformation.ProcessArchitecture is not (Architecture.X86 or Architecture.X64 or Architecture.Arm64))
        {
            throw new MarkdownSvgException(MarkdownSvgFailureReason.ArchitectureMismatch);
        }
        return options.WorkerExecutablePath ??
            Path.Combine(AppContext.BaseDirectory, WorkerProtocol.WorkerFileName);
    }

    private sealed class WorkerSlot(bool isSecondary)
    {
        public bool IsSecondary { get; } = isSecondary;
        public SemaphoreSlim FontCatalogGate { get; } = new(1, 1);
#if MARKDOWN_LAYOUT_DIAGNOSTICS
        public long PolicyDecisionSequence { get; set; }
        public MarkdownSvgWorkerPolicySnapshot PolicySnapshot { get; set; }
#endif
        public SecondaryWorkerReadiness? Readiness { get; } =
            isSecondary ? new SecondaryWorkerReadiness() : null;
        public WindowsWorkerProcess? Worker { get; set; }
        public PendingWork? Active { get; set; }
        public bool Busy { get; set; }
        public bool KillCanceledWhenStarted { get; set; }
        public DateTimeOffset IdleSince { get; set; }
        public CancellationTokenSource? IdleRetirement { get; set; }
    }

    private sealed class PendingWork(
        WorkerOperation operation,
        MarkdownSvgRenderPriority priority,
        CancellationToken cancellationToken,
        bool requiresFontCatalog = false,
        long preferredWorkerInstanceId = 0,
        bool isSecondaryFontProbe = false,
        MarkdownSvgDemandTicket? demandTicket = null)
    {
        public WorkerOperation Operation { get; } = operation;
        private MarkdownSvgRenderPriority _priority = priority;
        private IDisposable? _promotionRegistration;
        public MarkdownSvgRenderPriority Priority => _priority;
        public CancellationToken CancellationToken { get; set; } = cancellationToken;
        public bool RequiresFontCatalog { get; } = requiresFontCatalog;
        public long PreferredWorkerInstanceId { get; } = preferredWorkerInstanceId;
        public bool IsSecondaryFontProbe { get; } = isSecondaryFontProbe;
        public MarkdownSvgDemandTicket? DemandTicket { get; } = demandTicket;
        public TaskCompletionSource<WorkerLease> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationTokenRegistration CancellationRegistration { get; set; }
        public WorkerSlot? ActiveSlot { get; set; }
        public bool HasDetachedFontWaitReservation { get; set; }
        public bool IsCanceled { get; set; }
        public DateTimeOffset? BlockedSince { get; set; }
        public bool CancellationCheckScheduled { get; set; }

        public void SetPromotionRegistration(IDisposable registration)
        {
            if (Interlocked.CompareExchange(ref _promotionRegistration, registration, null) is not null)
                throw new InvalidOperationException("A queued worker item already has a promotion registration.");
        }

        public void DisposePromotionRegistration() =>
            Interlocked.Exchange(ref _promotionRegistration, null)?.Dispose();

        public void SynchronizePriorityFromTicket() => PromoteVisibleFromTicket();

        public bool PromoteVisibleFromTicket()
        {
            if (_priority == MarkdownSvgRenderPriority.Visible ||
                DemandTicket?.Priority != MarkdownSvgRenderPriority.Visible)
            {
                return false;
            }
            _priority = MarkdownSvgRenderPriority.Visible;
            return true;
        }
    }

    private sealed record CancellationState(WorkerPool Pool, PendingWork Pending);

    private sealed class WorkerLease(
        WorkerPool owner,
        WorkerSlot slot,
        PendingWork pending) : IDisposable
    {
        private int _disposed;

        public WorkerSlot Slot { get; } = slot;
        public PendingWork Pending { get; } = pending;


        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                owner.Release(Slot, Pending);
        }
    }

    private sealed class RenderAdmission(
        WorkerLease lease,
        WindowsWorkerProcess worker,
        long requestGeneration) : IDisposable
    {
        public WorkerLease Lease { get; } = lease;
        public WindowsWorkerProcess Worker { get; } = worker;
        public long RequestGeneration { get; } = requestGeneration;

        public void Dispose() => Lease.Dispose();
    }

    private sealed record TextWorkerAdmission(WorkerLease Lease, WindowsWorkerProcess Worker);
}

internal sealed class WorkerDocumentHandle
{
    private long _preferredWorkerInstanceId;
    private Action? _contentAdmissionGuard;
    private Action? _contentFailureCallback;

    public WorkerDocumentHandle(
        ulong documentId,
        long preferredWorkerInstanceId = 0,
        Action? contentAdmissionGuard = null,
        Action? contentFailureCallback = null)
    {
        DocumentId = documentId;
        _preferredWorkerInstanceId = preferredWorkerInstanceId;
        _contentAdmissionGuard = contentAdmissionGuard;
        _contentFailureCallback = contentFailureCallback;
    }

    public ulong DocumentId { get; }

    public long PreferredWorkerInstanceId => Interlocked.Read(ref _preferredWorkerInstanceId);

    public void ValidateContentAdmission() => Volatile.Read(ref _contentAdmissionGuard)?.Invoke();

    public void QuarantineContent() => Interlocked.Exchange(ref _contentFailureCallback, null)?.Invoke();

    public void ReleaseContentAdmissionGuard()
    {
        Interlocked.Exchange(ref _contentAdmissionGuard, null);
        Interlocked.Exchange(ref _contentFailureCallback, null);
    }

    public void PreferWorker(long workerInstanceId)
    {
        if (workerInstanceId > 0)
            Interlocked.Exchange(ref _preferredWorkerInstanceId, workerInstanceId);
    }
}

internal sealed class WorkerContentFailureMarker(Action? callback) : IDisposable
{
    private Action? _callback = callback;

    public void InvokeOnce() => Interlocked.Exchange(ref _callback, null)?.Invoke();

    public void Dispose() => Interlocked.Exchange(ref _callback, null);
}

internal readonly record struct WorkerOpenResult(
    WorkerResponse Response,
    WorkerDocumentHandle Document);

internal sealed class WorkerRenderResult : IDisposable
{
    private SharedMemoryLease? _outputMemory;

    internal WorkerRenderResult(WorkerResponse response, SharedMemoryLease outputMemory)
    {
        Response = response;
        _outputMemory = outputMemory ?? throw new ArgumentNullException(nameof(outputMemory));
    }

    internal WorkerResponse Response { get; }

    internal SharedMemoryLease TransferOutputMemory() =>
        Interlocked.Exchange(ref _outputMemory, null) ??
        throw new ObjectDisposedException(nameof(WorkerRenderResult));

    public void Dispose()
    {
        SharedMemoryLease? outputMemory = Interlocked.Exchange(ref _outputMemory, null);
        if (outputMemory is not null)
            ((IDisposable)outputMemory).Dispose();
    }
}
