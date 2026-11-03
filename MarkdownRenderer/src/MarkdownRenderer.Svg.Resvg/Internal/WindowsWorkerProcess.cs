using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using MarkdownRenderer.Images;
using Microsoft.Win32.SafeHandles;

namespace MarkdownRenderer.Svg.Resvg.Internal;

internal sealed class WindowsWorkerProcess : IAsyncDisposable, IDisposable, IWorkerFontCatalogHost
{
    private static long s_nextInstanceId;
    private readonly SafeProcessHandle _process;
    private readonly NamedPipeServerStream _pipe;
    private WorkerObjectSecurity.WorkerObjectSecurityContext? _objectSecurity;
    private readonly object _disposeGate = new();
    private readonly SemaphoreSlim _transactions = new(1, 1);
    private readonly byte[] _requestFrame;
    private readonly byte[] _responseFrame;
    internal WorkerDocumentStateGate DocumentStateGate { get; } = new();
    private readonly int _queueCapacity;
    private int _queued;
    private readonly WorkerFontCatalogGeneration _fontCatalogGeneration = new();
    private readonly WorkerFontCatalogSupplier _fontCatalogSupplier;
    private WorkerTerminationWait? _terminationWait;
    private int _disposed;

    // WindowsWorkerProcessLauncher is the production path. This constructor adopts a process
    // handle and an already-connected pipe whose handshake the caller owns.
    // The optional factory composes the font-catalog supplier around this
    // worker as its host; the default supplies the catalog directly. The
    // optional frames are the reusable exchange buffers; the default is new.
    internal WindowsWorkerProcess(
        SafeProcessHandle process,
        NamedPipeServerStream pipe,
        ulong nonceLow,
        ulong nonceHigh,
        string? workerExecutableSha256,
        int queueCapacity,
        WorkerObjectSecurity.WorkerObjectSecurityContext objectSecurity,
        Func<IWorkerFontCatalogHost, WorkerFontCatalogSupplier>? fontCatalogSupplierFactory = null,
        WorkerExchangeFrames? exchangeFrames = null)
    {
        exchangeFrames ??= new WorkerExchangeFrames();
        _requestFrame = exchangeFrames.Request;
        _responseFrame = exchangeFrames.Response;
        _process = process;
        _pipe = pipe;
        _objectSecurity = objectSecurity;
        NonceLow = nonceLow;
        NonceHigh = nonceHigh;
        WorkerExecutableSha256 = workerExecutableSha256;
        _queueCapacity = queueCapacity;
        InstanceId = Interlocked.Increment(ref s_nextInstanceId);
        _fontCatalogSupplier = fontCatalogSupplierFactory?.Invoke(this) ?? new WorkerFontCatalogSupplier(this);
    }

    public ulong NonceLow { get; }
    public ulong NonceHigh { get; }
    internal string? WorkerExecutableSha256 { get; }
    internal WorkerObjectSecurity.WorkerObjectSecurityContext ObjectSecurity =>
        Volatile.Read(ref _objectSecurity) ?? throw new ObjectDisposedException(nameof(WindowsWorkerProcess));
    public long InstanceId { get; }
    public long ReadyFontGeneration => _fontCatalogGeneration.ReadyGeneration;
    public bool IsFontCatalogReady => ReadyFontGeneration >= 0;
    public bool HasExited
    {
        get
        {
            if (Volatile.Read(ref _disposed) != 0)
                return true;
            try
            {
                return WorkerNativeMethods.WaitForSingleObject(_process, 0) != WorkerNativeMethods.WaitTimeout;
            }
            catch (ObjectDisposedException)
            {
                return true;
            }
        }
    }

    public void MarkFontCatalogReady(long generation) => _fontCatalogGeneration.MarkReady(generation);

    // Every internal caller consumes this operation exactly once. The runtime
    // pool clears its state machine (request, mapping and execution context)
    // when consumed; source/raster ownership is never pooled here. Callers that
    // need Task semantics must convert once with AsTask before sharing it.
    // Only the awaits stay in this method; its synchronous steps share one
    // WorkerExchangeTrace local by reference and allocate nothing.
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    public async ValueTask<WorkerResponse> ExchangeAsync(
        WorkerRequest request,
        TimeSpan deadline,
        CancellationToken cancellationToken,
        SharedMemoryLease? sharedMemory = null,
        WorkerTimeoutEvidenceScope? timeoutEvidenceScope = null
        ,
        long diagnosticWorkId = 0,
        int diagnosticStageId = 0,
        ulong diagnosticDocumentId = 0,
        long diagnosticSourceBytes = -1,
        long diagnosticTargetPixels = -1)
    {
        EnterExchangeQueue();
        bool entered = false;
        var trace = new WorkerExchangeTrace(
            request,
            diagnosticWorkId,
            diagnosticDocumentId,
            diagnosticSourceBytes,
            diagnosticTargetPixels);
        long transactionQueueStartedAt = SvgWorkerStageTrace.Start(diagnosticWorkId);
        try
        {
            await _transactions.WaitAsync(cancellationToken).ConfigureAwait(false);
            entered = true;
            BeginTransaction(ref trace, request, diagnosticStageId, deadline, timeoutEvidenceScope, transactionQueueStartedAt);
            using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            operation.CancelAfter(deadline);
            ValidateSharedMemoryOwner(request, sharedMemory);
            using WorkerMappingLease? mappingLease = sharedMemory?.DuplicateInto(_process,
                readOnly: request.Operation == WorkerOperation.FontCatalog);
            request = request with { MappingHandle = mappingLease?.ProtocolHandle ?? 0 };
            WorkerProtocol.Encode(request, _requestFrame);
            byte[] requestFrame = _requestFrame;
            byte[] responseFrame = _responseFrame;
            trace.RequestStarted = trace.CaptureTimeoutEvidence ? Stopwatch.GetTimestamp() : 0;
            try
            {
                long pipeStageStartedAt = SvgWorkerStageTrace.Start(diagnosticWorkId);
                await _pipe.WriteAsync(requestFrame, operation.Token).ConfigureAwait(false);
                trace.RecordPipeStage(SvgWorkerStageId.PipeRequestWrite, InstanceId, request.RequestId, pipeStageStartedAt);
                pipeStageStartedAt = SvgWorkerStageTrace.Start(diagnosticWorkId);
                trace.TransportPhase = 1;
                await _pipe.FlushAsync(operation.Token).ConfigureAwait(false);
                trace.RecordPipeStage(SvgWorkerStageId.PipeRequestFlush, InstanceId, request.RequestId, pipeStageStartedAt);
                trace.MarkRequestWritten();
                trace.TransportPhase = 2;
                pipeStageStartedAt = SvgWorkerStageTrace.Start(diagnosticWorkId);
                await _pipe.ReadExactlyAsync(responseFrame, operation.Token).ConfigureAwait(false);
                trace.RecordPipeStage(SvgWorkerStageId.PipeResponseRead, InstanceId, request.RequestId, pipeStageStartedAt);
            }
            catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
            {
                ReportTransportTimeout(in trace, request, sharedMemory);
                DisposeForRestart();
                throw new WorkerDeadlineException("The resvg worker exceeded its request deadline.", exception);
            }
            catch (OperationCanceledException)
            {
                DisposeForRestart();
                throw;
            }
            catch (Exception exception) when (exception is IOException or InvalidOperationException)
            {
                DisposeForRestart();
                throw new WorkerProtocolException("The resvg worker transport failed.", exception);
            }

            WorkerResponse decoded = DecodeResponse(responseFrame, request);
            RecordTransaction(ref trace, request, decoded);
            return decoded;
        }
        finally
        {
            if (entered)
            {
                // The shared frame buffers are protected by the transaction
                // gate. Scrub the transferred mapping handle and reply bytes
                // before admitting the next request, including fault paths.
                _requestFrame.AsSpan().Clear();
                _responseFrame.AsSpan().Clear();
                _transactions.Release();
            }
            Interlocked.Decrement(ref _queued);
        }
    }

    /// <summary>
    /// Counts the caller into the bounded exchange queue. On failure the count
    /// is restored here; on success the exchange's finally releases it.
    /// </summary>
    private void EnterExchangeQueue()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        int queued = Interlocked.Increment(ref _queued);
        if (queued > _queueCapacity)
        {
            Interlocked.Decrement(ref _queued);
            throw new MarkdownSvgException(
                MarkdownRenderer.Images.MarkdownSvgFailureReason.ResourceLimitExceeded,
                "The SVG worker queue is full.");
        }
    }

    /// <summary>
    /// Runs once the transaction gate is held: records the queue wait, checks
    /// that the worker is still usable, and captures the transaction's trace
    /// and timeout-evidence starting points.
    /// </summary>
    private void BeginTransaction(
        ref WorkerExchangeTrace trace,
        WorkerRequest request,
        int diagnosticStageId,
        TimeSpan deadline,
        WorkerTimeoutEvidenceScope? timeoutEvidenceScope,
        long transactionQueueStartedAt)
    {
        SvgWorkerStageTrace.Record(
            trace.WorkId,
            SvgWorkerStageId.WorkerTransactionQueueWait,
            InstanceId,
            request.RequestId,
            trace.DocumentId,
            transactionQueueStartedAt,
            sourceBytes: trace.SourceBytes,
            targetPixels: trace.TargetPixels);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (HasExited)
            throw new WorkerProtocolException("The resvg worker exited before the request.");

        trace.EffectiveStageId = request.Operation == WorkerOperation.Render &&
            diagnosticStageId == (int)SvgWorkerStageId.RenderTransaction
            ? (int)SvgWorkerStageId.RenderTransaction
            : diagnosticStageId;
        trace.TransactionStartedAt = SvgWorkerStageTrace.Start(
            trace.WorkId > 0 && trace.EffectiveStageId != 0 ? trace.WorkId : 0);
        trace.WorkerCpuStartedAt = trace.TransactionStartedAt != 0 &&
            request.Operation is WorkerOperation.Open or WorkerOperation.Render
                ? GetProcessCpuTicks()
                : -1;
        // Diagnostic capture must not add a process query to every normal
        // SVG request. The listener is installed before diagnostic rendering.
        trace.CaptureTimeoutEvidence = WorkerTimeoutEvents.Log.IsEnabled();
        long workerCpuAtStart = trace.CaptureTimeoutEvidence
            ? timeoutEvidenceScope?.WorkerCpuAtStart ?? GetProcessCpuTicks()
            : -1;
        trace.EvidenceWindow = WorkerTimeoutEvidenceScope.Resolve(
            deadline,
            workerCpuAtStart,
            timeoutEvidenceScope);
    }

    /// <summary>
    /// Requires a shared-memory owner exactly for the operations that transfer
    /// a mapping, and requires the caller not to pre-set a mapping handle.
    /// </summary>
    private static void ValidateSharedMemoryOwner(WorkerRequest request, SharedMemoryLease? sharedMemory)
    {
        bool needsMapping = request.Operation is WorkerOperation.Open or WorkerOperation.Render or
            WorkerOperation.FontCatalog or WorkerOperation.RasterDecode;
        if (needsMapping != (sharedMemory is not null) || request.MappingHandle != 0)
            throw new WorkerProtocolException("The SVG exchange has an invalid shared-memory owner.");
    }

    /// <summary>
    /// Emits timeout evidence for a request that exceeded its own deadline.
    /// It reads process counters only when an evidence listener is installed.
    /// </summary>
    private void ReportTransportTimeout(
        in WorkerExchangeTrace trace,
        WorkerRequest request,
        SharedMemoryLease? sharedMemory)
    {
        WorkerTimeoutEvidenceScope evidenceWindow = trace.EvidenceWindow;
        long workerCpuAtTimeout = evidenceWindow.WorkerCpuAtStart >= 0
            ? GetProcessCpuTicks()
            : -1;
        int workerProcessCpuMilliseconds = evidenceWindow.WorkerCpuAtStart >= 0 &&
            workerCpuAtTimeout >= evidenceWindow.WorkerCpuAtStart
                ? (int)Math.Min((workerCpuAtTimeout - evidenceWindow.WorkerCpuAtStart) / 10_000, int.MaxValue)
                : -1;
        if (trace.CaptureTimeoutEvidence)
        {
            WorkerMemorySnapshot memory = GetProcessMemorySnapshot();
            WorkerTimeoutEvents.Log.Timeout(
                stage: (int)request.Operation,
                deadlineMilliseconds: (int)evidenceWindow.TotalDeadline.TotalMilliseconds,
                workerProcessCpuMilliseconds,
                trace.TransportPhase,
                trace.RequestWriteMilliseconds,
                workerExited: HasExited ? 1 : 0,
                openProgressPhase: request.Operation == WorkerOperation.Open
                    ? sharedMemory?.ReadOpenProgressPhase() ?? -1 : -1,
                workerWorkingSetKiB: memory.WorkingSetKiB,
                workerPrivateCommitKiB: memory.PrivateCommitKiB,
                workerPageFaults: memory.PageFaults,
                elapsedWallMilliseconds: GetElapsedMilliseconds(evidenceWindow.WallStartedAt),
                workerInputSha256: trace.CaptureTimeoutEvidence
                    ? Convert.ToHexString(request.ContentHash)
                    : string.Empty,
                workerExecutableSha256: WorkerExecutableSha256 ?? string.Empty);
        }
    }

    /// <summary>
    /// Decodes and authenticates the response frame. A malformed reply retires
    /// the worker before the caller can reclaim its mapping handle.
    /// </summary>
    private WorkerResponse DecodeResponse(byte[] responseFrame, WorkerRequest request)
    {
        try
        {
            return WorkerProtocol.DecodeResponse(
                responseFrame,
                request.RequestId,
                NonceLow,
                NonceHigh,
                request.Operation);
        }
        catch (Exception exception) when (exception is WorkerProtocolException or OverflowException)
        {
            // An untrusted/malformed reply does not acknowledge that the
            // worker released its view. Retire it before reclaiming the
            // remote mapping handle or admitting another transaction.
            DisposeForRestart();
            throw new WorkerProtocolException("The resvg worker returned an invalid response.", exception);
        }
    }

    /// <summary>Records the whole transaction stage with the worker CPU it consumed.</summary>
    private void RecordTransaction(ref WorkerExchangeTrace trace, WorkerRequest request, WorkerResponse decoded)
    {
        if (request.Operation == WorkerOperation.Render &&
            decoded.Status == WorkerStatus.DocumentMissing)
        {
            trace.EffectiveStageId = (int)SvgWorkerStageId.DocumentMissingProbe;
        }
        long diagnosticWorkerCpuTicks = -1;
        if (trace.WorkerCpuStartedAt >= 0 && trace.WorkerCpuStartedAt != -1)
        {
            long workerCpuAtEnd = GetProcessCpuTicks();
            if (workerCpuAtEnd >= trace.WorkerCpuStartedAt)
                diagnosticWorkerCpuTicks = workerCpuAtEnd - trace.WorkerCpuStartedAt;
        }
        if (trace.TransactionStartedAt != 0 && Enum.IsDefined((SvgWorkerStageId)trace.EffectiveStageId))
        {
            SvgWorkerStageTrace.Record(
                trace.WorkId,
                (SvgWorkerStageId)trace.EffectiveStageId,
                InstanceId,
                request.RequestId,
                trace.DocumentId,
                trace.TransactionStartedAt,
                diagnosticWorkerCpuTicks,
                trace.SourceBytes,
                trace.TargetPixels);
        }
    }

    public void Dispose() => DisposeCore(waitForExit: false);

    internal long GetProcessCpuTicks()
    {
        try
        {
            if (!WorkerNativeMethods.GetProcessTimes(_process, out _, out _, out WorkerNativeMethods.FileTime kernel, out WorkerNativeMethods.FileTime user))
                return -1;

            ulong kernelTicks = ((ulong)kernel.High << 32) | kernel.Low;
            ulong userTicks = ((ulong)user.High << 32) | user.Low;
            return userTicks <= long.MaxValue &&
                   kernelTicks <= (ulong)long.MaxValue - userTicks
                ? (long)(kernelTicks + userTicks)
                : -1;
        }
        catch (ObjectDisposedException)
        {
            return -1;
        }
    }

    internal WorkerMemorySnapshot GetProcessMemorySnapshot()
    {
        try
        {
            var counters = new WorkerNativeMethods.ProcessMemoryCountersEx
            {
                Size = (uint)Marshal.SizeOf<WorkerNativeMethods.ProcessMemoryCountersEx>()
            };
            if (!WorkerNativeMethods.GetProcessMemoryInfo(_process, ref counters, counters.Size))
                return WorkerMemorySnapshot.Unavailable;

            return new WorkerMemorySnapshot(
                ToKiB(counters.WorkingSetSize),
                ToKiB(counters.PrivateUsage),
                (int)Math.Min(counters.PageFaultCount, int.MaxValue));
        }
        catch (ObjectDisposedException)
        {
            return WorkerMemorySnapshot.Unavailable;
        }
    }

    private static int ToKiB(nuint bytes) =>
        (int)Math.Min((ulong)bytes / 1024UL, int.MaxValue);

    internal static int GetElapsedMilliseconds(long startedAt) =>
        startedAt == 0
            ? -1
            : (int)Math.Min(Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds, int.MaxValue);

    internal void DisposeForRestart() => DisposeCore(waitForExit: true);

    // The task owns only approved installed-font file mappings. Font parsing
    // stays in the worker. Geometry requests never wait for this preparation.
    internal Task EnsureFontCatalogSuppliedAsync(WorkerRequest request, CancellationToken cancellationToken) =>
        _fontCatalogSupplier.EnsureFontCatalogSuppliedAsync(request, cancellationToken);

    SafeProcessHandle IWorkerFontCatalogHost.Process => _process;

    WorkerObjectSecurity.WorkerObjectSecurityContext IWorkerFontCatalogHost.ObjectSecurity => ObjectSecurity;

    void IWorkerFontCatalogHost.DisposeForRestart() => DisposeForRestart();

    void IWorkerFontCatalogHost.ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

    ValueTask<WorkerResponse> IWorkerFontCatalogHost.ExchangeAsync(
        WorkerRequest request,
        TimeSpan deadline,
        CancellationToken cancellationToken,
        SharedMemoryLease? sharedMemory) =>
        ExchangeAsync(request, deadline, cancellationToken, sharedMemory);

    private void DisposeCore(bool waitForExit)
    {
        WorkerTerminationWait? terminationWait;
        bool waitForPrimaryHandle = false;
        bool waitedOnPrimaryHandle = false;
        bool retireFontPreparation = false;
        Exception? cleanupFailure = null;
        lock (_disposeGate)
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                retireFontPreparation = true;
                waitedOnPrimaryHandle = TerminateLocked(waitForExit, ref cleanupFailure);
            }

            terminationWait = _terminationWait;
            waitForPrimaryHandle = waitForExit && terminationWait is null && !_process.IsClosed &&
                !waitedOnPrimaryHandle;
        }

        // Cancellation may run continuations synchronously. Publish native
        // termination first, outside the dispose lock, before those callbacks
        // can attempt a restart wait or release remote font handles.
        if (retireFontPreparation)
        {
            try { _fontCatalogSupplier.Retire(); }
            catch (Exception exception) { cleanupFailure ??= exception; }
            ReleaseObjectSecurityWhenFontCatalogDrained();
        }

        WaitForTermination(waitForExit, terminationWait, waitForPrimaryHandle, ref cleanupFailure);
        if (cleanupFailure is not null)
            ExceptionDispatchInfo.Capture(cleanupFailure).Throw();

        // An active ExchangeAsync releases this gate in its finally block.
        // SemaphoreSlim owns no native handle, so retaining it avoids masking
        // the typed cancellation/transport failure during concurrent disposal.
    }

    /// <summary>
    /// The first dispose's native shutdown, run while holding the dispose
    /// gate: close the pipe, register an independent termination wait,
    /// terminate the process and, when no wait could be registered, wait on
    /// the primary handle for a restart. Every step runs even if an earlier
    /// one failed; the first failure is kept.
    /// </summary>
    /// <param name="waitForExit">Whether the caller is a restart that must observe exit.</param>
    /// <param name="cleanupFailure">Receives the first cleanup failure.</param>
    /// <returns>Whether this call already waited on the primary handle.</returns>
    private bool TerminateLocked(bool waitForExit, ref Exception? cleanupFailure)
    {
        bool waitedOnPrimaryHandle = false;
        try
        {
            _pipe.Dispose();
        }
        catch (Exception exception)
        {
            cleanupFailure ??= exception;
        }

        // Keep an independent, registered process wait before closing
        // the primary handle. A concurrent ordinary Dispose may start
        // shutdown first, but a later restart Dispose must still be
        // able to await process exit before admitting its replacement.
        try
        {
            _terminationWait = WorkerTerminationWait.Register(
                _process,
                checked((int)WorkerProcessSandbox.WorkerTerminationWaitMilliseconds));
        }
        catch (Exception exception)
        {
            // Termination remains best-effort if Windows refuses the
            // duplicate wait handle. Keep the SafeHandle open on an
            // ordinary Dispose so a later restart-dispose can wait on
            // it; SafeHandle finalization remains the last-resort
            // cleanup if no restart caller follows.
            cleanupFailure ??= exception;
        }

        try
        {
            _ = WorkerNativeMethods.TerminateProcess(_process, WorkerProcessSandbox.WorkerTerminationExitCode);
        }
        catch (Exception exception)
        {
            cleanupFailure ??= exception;
        }
        finally
        {
            if (_terminationWait is not null)
            {
                _process.Dispose();
            }
            else if (waitForExit)
            {
                try
                {
                    // TerminateProcess is asynchronous. The job counts
                    // a dying process until signaled, so preserve the
                    // bounded restart wait if registration failed.
                    _ = WorkerNativeMethods.WaitForSingleObject(_process, WorkerProcessSandbox.WorkerTerminationWaitMilliseconds);
                    waitedOnPrimaryHandle = true;
                }
                catch (Exception exception)
                {
                    cleanupFailure ??= exception;
                }
                finally
                {
                    _process.Dispose();
                }
            }
        }

        return waitedOnPrimaryHandle;
    }

    /// <summary>
    /// Waits, outside the dispose gate, for a restart's terminated worker to
    /// exit: through the registered wait when one exists, otherwise on the
    /// primary handle, which is then closed.
    /// </summary>
    /// <param name="waitForExit">Whether the caller is a restart that must observe exit.</param>
    /// <param name="terminationWait">The registered termination wait, if any.</param>
    /// <param name="waitForPrimaryHandle">Whether to wait on the still-open primary handle.</param>
    /// <param name="cleanupFailure">Receives the first cleanup failure.</param>
    private void WaitForTermination(
        bool waitForExit,
        WorkerTerminationWait? terminationWait,
        bool waitForPrimaryHandle,
        ref Exception? cleanupFailure)
    {
        if (waitForExit && terminationWait is not null)
        {
            // This is a task wait backed by a registered native wait, not a
            // blocked polling thread. Ordinary Dispose never waits here.
            _ = terminationWait.WaitForExit(checked((int)WorkerProcessSandbox.WorkerTerminationWaitMilliseconds));
        }
        else if (waitForPrimaryHandle)
        {
            try
            {
                _ = WorkerNativeMethods.WaitForSingleObject(_process, WorkerProcessSandbox.WorkerTerminationWaitMilliseconds);
            }
            catch (Exception exception)
            {
                cleanupFailure ??= exception;
            }
            finally
            {
                _process.Dispose();
            }
        }
    }

    private void ReleaseObjectSecurityWhenFontCatalogDrained()
    {
        Task? preparation = _fontCatalogSupplier.Preparation;

        if (preparation is null || preparation.IsCompleted)
        {
            ReleaseObjectSecurity();
            return;
        }

        _ = preparation.ContinueWith(
            static (_, state) => ((WindowsWorkerProcess)state!).ReleaseObjectSecurity(),
            this,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void ReleaseObjectSecurity() => Interlocked.Exchange(ref _objectSecurity, null)?.Dispose();

    public async ValueTask DisposeAsync()
    {
        Exception? failure = null;
        try { Dispose(); }
        catch (Exception exception) { failure = exception; }
        try { await _fontCatalogSupplier.DrainAsync().ConfigureAwait(false); }
        catch (Exception exception)
        {
            failure = failure is null ? exception : new AggregateException(failure, exception);
        }
        ReleaseObjectSecurity();

        try
        {
            WorkerTerminationWait? terminationWait;
            lock (_disposeGate)
                terminationWait = _terminationWait;
            if (terminationWait is null)
            {
                // If native wait registration failed, release the retained
                // primary handle after the existing bounded fallback wait.
                // This cannot certify shutdown; do not report a false success.
                await Task.Run(() => DisposeCore(waitForExit: true)).ConfigureAwait(false);
                throw new InvalidOperationException("The SVG worker shutdown could not be monitored.");
            }
            if (!await terminationWait.WaitForExitAsync().ConfigureAwait(false))
                throw new TimeoutException("The SVG worker did not exit within the shutdown deadline.");
        }
        catch (Exception exception)
        {
            failure = failure is null ? exception : new AggregateException(failure, exception);
        }

        if (failure is not null)
            ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
