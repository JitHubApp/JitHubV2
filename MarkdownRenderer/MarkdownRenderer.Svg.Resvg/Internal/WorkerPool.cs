using System.Runtime.InteropServices;
using MarkdownRenderer.Images;

namespace MarkdownRenderer.Svg.Resvg.Internal;

internal sealed class WorkerPool : IAsyncDisposable, IDisposable
{
    private readonly ResvgMarkdownSvgRendererOptions _options;
    private readonly WorkerSchedulingQueue<PendingWork> _queue = new();
    private readonly Queue<DateTimeOffset> _startupFailures = new();
    private readonly object _stateLock = new();
    private readonly WorkerSlot _primary = new(isSecondary: false);
    private readonly TimeProvider _timeProvider;
    private readonly Func<bool> _isEnergySaverOn;
    private readonly bool _supportsSecondaryHardware;
    private readonly Lazy<WindowsWorkerProcess.WorkerJob> _job;
    private WorkerSlot? _secondary;
    private DateTimeOffset _disabledUntil;
    private long _nextRequestId;
    private long _nextDocumentId;
    private long _sourceAttachmentCount;
    private long _fontGeneration;
    private int _disposed;

    public WorkerPool(ResvgMarkdownSvgRendererOptions options)
        : this(options, TimeProvider.System, QueryEnergySaverOn)
    {
    }

    internal WorkerPool(
        ResvgMarkdownSvgRendererOptions options,
        TimeProvider timeProvider,
        Func<bool> isEnergySaverOn)
    {
        _options = options;
        _timeProvider = timeProvider;
        _isEnergySaverOn = isEnergySaverOn;
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
        _ = await EnsureWorkerAsync(lease).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
    }

    public async Task<WorkerOpenResult> OpenDocumentAsync(
        SvgPreflightResult preflight,
        MarkdownSvgOpenRequest openRequest,
        SharedMemoryLease sourceMemory,
        CancellationToken cancellationToken)
    {
        ulong documentId = NextDocumentId();
        using WorkerLease lease = await AcquireAsync(
            WorkerOperation.Open,
            openRequest.Priority,
            cancellationToken).ConfigureAwait(false);
        WindowsWorkerProcess worker = await EnsureWorkerAsync(lease).ConfigureAwait(false);
        if (preflight.Info.HasText)
            await EnsureFontCatalogReadyAsync(lease, worker, cancellationToken).ConfigureAwait(false);
        WorkerResponse response = await AttachDocumentAsync(
            lease,
            worker,
            documentId,
            preflight,
            openRequest,
            sourceMemory,
            _options.RequestDeadline,
            cancellationToken).ConfigureAwait(false);
        return new WorkerOpenResult(
            response,
            new WorkerDocumentHandle(documentId));
    }

    public async Task<WorkerResponse> RenderDocumentAsync(
        WorkerDocumentHandle document,
        SvgPreflightResult preflight,
        MarkdownSvgOpenRequest openRequest,
        MarkdownSvgRenderRequest renderRequest,
        SharedMemoryLease sourceMemory,
        SharedMemoryLease outputMemory,
        CancellationToken cancellationToken)
    {
        using WorkerLease lease = await AcquireAsync(
            WorkerOperation.Render,
            renderRequest.Priority,
            cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        WindowsWorkerProcess worker = await EnsureWorkerAsync(lease).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (preflight.Info.HasText)
            await EnsureFontCatalogReadyAsync(lease, worker, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        long requestGeneration = FontGeneration;
        WorkerRequest request = new(
            WorkerOperation.Render,
            NextRequestId(),
            worker.NonceLow,
            worker.NonceHigh,
            outputMemory.Name,
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
            _options);
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
                CancellationToken.None).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (preflight.Info.HasText && requestGeneration != FontGeneration)
                throw new OperationCanceledException("The SVG font environment changed while the request was active.");
            if (response.Status != WorkerStatus.DocumentMissing)
                return Stamp(response, worker);

            WorkerResponse attachment = await AttachDocumentAsync(
                lease,
                worker,
                document.DocumentId,
                preflight,
                openRequest,
                sourceMemory,
                _options.RequestDeadline,
                cancellationToken).ConfigureAwait(false);
            if (attachment.Status != WorkerStatus.Ok)
                return attachment;
            cancellationToken.ThrowIfCancellationRequested();

            request = request with { RequestId = NextRequestId() };
            response = await worker.ExchangeAsync(
                request,
                _options.RequestDeadline,
                CancellationToken.None).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (preflight.Info.HasText && requestGeneration != FontGeneration)
                throw new OperationCanceledException("The SVG font environment changed while the request was active.");
            if (response.Status == WorkerStatus.DocumentMissing)
                throw new WorkerProtocolException("The worker lost an attached SVG document during one transaction.");
            return Stamp(response, worker);
        }
        catch (Exception exception) when (IsWorkerTransportFailure(exception))
        {
            Invalidate(lease.Slot, worker);
            if (cancellationToken.IsCancellationRequested)
                throw new OperationCanceledException(cancellationToken);
            if (requestGeneration != FontGeneration)
                throw new OperationCanceledException("The SVG worker was intentionally rotated after a font change.");
            throw;
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
            _ = ForgetDocumentAsync(worker, document.DocumentId);
    }

    private async Task<WorkerResponse> AttachDocumentAsync(
        WorkerLease lease,
        WindowsWorkerProcess worker,
        ulong documentId,
        SvgPreflightResult preflight,
        MarkdownSvgOpenRequest openRequest,
        SharedMemoryLease sourceMemory,
        TimeSpan deadline,
        CancellationToken cancellationToken)
    {
        long requestGeneration = FontGeneration;
        WorkerRequest request = new(
            WorkerOperation.Open,
            NextRequestId(),
            worker.NonceLow,
            worker.NonceHigh,
            sourceMemory.Name,
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
                CancellationToken.None).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (preflight.Info.HasText && requestGeneration != FontGeneration)
                throw new OperationCanceledException("The SVG font environment changed while the document was attaching.");
            if (response.Status == WorkerStatus.Ok)
                Interlocked.Increment(ref _sourceAttachmentCount);
            return Stamp(response, worker);
        }
        catch (Exception exception) when (IsWorkerTransportFailure(exception))
        {
            Invalidate(lease.Slot, worker);
            if (cancellationToken.IsCancellationRequested)
                throw new OperationCanceledException(cancellationToken);
            if (requestGeneration != FontGeneration)
                throw new OperationCanceledException("The SVG worker was intentionally rotated after a font change.");
            throw;
        }
    }

    private async Task ForgetDocumentAsync(WindowsWorkerProcess worker, ulong documentId)
    {
        try
        {
            WorkerResponse response = await worker.ExchangeAsync(
                CreateControlRequest(WorkerOperation.CloseDocument, worker, documentId),
                _options.RequestDeadline,
                CancellationToken.None).ConfigureAwait(false);
            if (response.Status != WorkerStatus.Ok || response.OutputLength != 0)
                throw new WorkerProtocolException("The resvg worker rejected a document close request.");
        }
        catch (Exception exception) when (IsWorkerTransportFailure(exception))
        {
            Invalidate(worker);
        }
    }

    private static WorkerResponse Stamp(WorkerResponse response, WindowsWorkerProcess worker) =>
        response with { WorkerInstanceId = worker.InstanceId };

    public void AdvanceFontGeneration()
    {
        Interlocked.Increment(ref _fontGeneration);
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
            primary = _primary.Worker;
            secondary = _secondary?.Worker;
            _primary.Worker = null;
            if (_secondary is not null)
            {
                CancelIdleRetirementLocked(_secondary);
                _secondary.Worker = null;
            }
        }

        foreach (PendingWork pending in queued)
            pending.Completion.TrySetException(new ObjectDisposedException(nameof(WorkerPool)));
        primary?.Dispose();
        if (!ReferenceEquals(primary, secondary))
            secondary?.Dispose();
        if (_job.IsValueCreated)
            _job.Value.Dispose();
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    private Task<WorkerLease> AcquireAsync(
        WorkerOperation operation,
        MarkdownSvgRenderPriority priority,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        cancellationToken.ThrowIfCancellationRequested();
        var pending = new PendingWork(operation, priority, cancellationToken);
        lock (_stateLock)
        {
            ThrowIfUnavailableLocked();
            if (_queue.Count >= _options.QueueCapacity)
            {
                throw new MarkdownSvgException(
                    MarkdownSvgFailureReason.ResourceLimitExceeded,
                    "The SVG worker priority queue is full.");
            }

            _queue.Enqueue(pending, priority, operation == WorkerOperation.Render);
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
            PumpLocked();
        }
        return pending.Completion.Task;
    }

    private void Cancel(PendingWork pending)
    {
        lock (_stateLock)
        {
            pending.IsCanceled = true;
            if (_queue.Remove(pending))
            {
                pending.Completion.TrySetCanceled(pending.CancellationToken);
                PumpLocked();
                return;
            }
            EvaluateCanceledBlockingLocked();
        }
    }

    private void PumpLocked()
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;

        bool progressed;
        do
        {
            progressed = false;
            if (!_primary.Busy && _queue.TryDequeuePrimary(out PendingWork? primaryWork))
            {
                AssignLocked(_primary, primaryWork!);
                progressed = true;
            }

            if (_secondary is { Busy: false } secondary &&
                _queue.TryDequeueSecondary(out PendingWork? secondaryWork))
            {
                AssignLocked(secondary, secondaryWork!);
                progressed = true;
            }

            bool canUseSecondary = WorkerSchedulingPolicy.CanUseSecondary(
                RuntimeInformation.ProcessArchitecture,
                Environment.ProcessorCount,
                SafeQueryEnergySaverOn());
            if (WorkerSchedulingPolicy.ShouldStartSecondary(
                    canUseSecondary,
                    _primary.Busy,
                    _secondary is not null,
                    _queue.VisibleRenderCount))
            {
                _secondary = new WorkerSlot(isSecondary: true);
                if (_queue.TryDequeueSecondary(out PendingWork? newSecondaryWork))
                {
                    AssignLocked(_secondary, newSecondaryWork!);
                    progressed = true;
                }
            }
        }
        while (progressed);

        EvaluateCanceledBlockingLocked();
    }

    private void AssignLocked(WorkerSlot slot, PendingWork pending)
    {
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
    }

    private async Task<WindowsWorkerProcess> EnsureWorkerAsync(WorkerLease lease)
    {
        WindowsWorkerProcess? stale = null;
        lock (_stateLock)
        {
            ThrowIfUnavailableLocked();
            if (lease.Slot.Worker is { HasExited: false } existing)
                return existing;
            stale = lease.Slot.Worker;
            lease.Slot.Worker = null;
        }
        stale?.Dispose();

        WindowsWorkerProcess created;
        try
        {
            created = await WindowsWorkerProcess.StartAsync(
                WorkerPath,
                _options,
                _job.Value,
                CancellationToken.None).ConfigureAwait(false);
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

        bool rejectCreated;
        lock (_stateLock)
        {
            rejectCreated = Volatile.Read(ref _disposed) != 0 ||
                !ReferenceEquals(lease.Slot.Active, lease.Pending) ||
                (lease.Slot.KillCanceledWhenStarted &&
                 lease.Pending.IsCanceled &&
                 _queue.VisibleCount > 0);
            if (!rejectCreated)
                lease.Slot.Worker = created;
        }
        if (rejectCreated)
        {
            created.Dispose();
            if (lease.Pending.IsCanceled)
                throw new OperationCanceledException(lease.Pending.CancellationToken);
            throw new ObjectDisposedException(nameof(WorkerPool));
        }
        return created;
    }

    private async Task EnsureFontCatalogReadyAsync(
        WorkerLease lease,
        WindowsWorkerProcess worker,
        CancellationToken cancellationToken)
    {
        if (worker.IsFontCatalogReady)
            return;

        try
        {
            WorkerResponse response = await worker.ExchangeAsync(
                CreateControlRequest(WorkerOperation.Hello, worker),
                WorkerSchedulingPolicy.InitializationDeadline,
                CancellationToken.None).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (response.Status != WorkerStatus.Ok || response.OutputLength != 0)
                throw new WorkerProtocolException("The resvg worker rejected font-catalog initialization.");
            worker.MarkFontCatalogReady();
        }
        catch (Exception exception) when (IsWorkerTransportFailure(exception))
        {
            Invalidate(lease.Slot, worker);
            RecordStartupFailure();
            if (cancellationToken.IsCancellationRequested)
                throw new OperationCanceledException(cancellationToken);
            if (exception is WorkerDeadlineException)
            {
                throw new WorkerInitializationDeadlineException(
                    "The resvg worker did not initialize its font catalog before the initialization deadline.",
                    exception);
            }
            throw new WorkerInitializationException(
                "The resvg worker could not initialize its font catalog.",
                exception);
        }
    }

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
                if (slot.Worker is null)
                {
                    CancelIdleRetirementLocked(slot);
                    _secondary = null;
                }
                else
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
        worker?.Dispose();
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
        worker?.Dispose();
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
        ulong documentId = 0) =>
        new(
            operation,
            NextRequestId(),
            worker.NonceLow,
            worker.NonceHigh,
            string.Empty,
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
            FontGeneration,
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
        worker.Dispose();
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
        worker.Dispose();
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

    private bool SafeQueryEnergySaverOn()
    {
        try
        {
            return _isEnergySaverOn();
        }
        catch (Exception exception) when (exception is COMException or PlatformNotSupportedException or TypeInitializationException)
        {
            // Conservatively keep one worker if power state cannot be queried.
            return true;
        }
    }

    private static bool QueryEnergySaverOn() =>
        Windows.System.Power.PowerManager.EnergySaverStatus ==
        Windows.System.Power.EnergySaverStatus.On;

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
        CancellationToken cancellationToken)
    {
        public WorkerOperation Operation { get; } = operation;
        public MarkdownSvgRenderPriority Priority { get; } = priority;
        public CancellationToken CancellationToken { get; } = cancellationToken;
        public TaskCompletionSource<WorkerLease> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationTokenRegistration CancellationRegistration { get; set; }
        public WorkerSlot? ActiveSlot { get; set; }
        public bool IsCanceled { get; set; }
        public DateTimeOffset? BlockedSince { get; set; }
        public bool CancellationCheckScheduled { get; set; }
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
}

internal readonly record struct WorkerDocumentHandle(ulong DocumentId);

internal readonly record struct WorkerOpenResult(
    WorkerResponse Response,
    WorkerDocumentHandle Document);
