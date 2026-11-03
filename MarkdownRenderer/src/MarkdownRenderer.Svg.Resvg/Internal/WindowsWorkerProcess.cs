using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using MarkdownRenderer.Images;
using Microsoft.Win32.SafeHandles;

namespace MarkdownRenderer.Svg.Resvg.Internal;

internal sealed partial class WindowsWorkerProcess : IAsyncDisposable, IDisposable
{
    private static long s_nextInstanceId;
    private readonly SafeProcessHandle _process;
    private readonly NamedPipeServerStream _pipe;
    private WorkerObjectSecurity.WorkerObjectSecurityContext? _objectSecurity;
    private readonly object _disposeGate = new();
    private readonly SemaphoreSlim _transactions = new(1, 1);
    private readonly byte[] _requestFrame = new byte[WorkerProtocol.RequestSize];
    private readonly byte[] _responseFrame = new byte[WorkerProtocol.ResponseSize];
    internal WorkerDocumentStateGate DocumentStateGate { get; } = new();
    private readonly int _queueCapacity;
    private int _queued;
    private readonly WorkerFontCatalogGeneration _fontCatalogGeneration = new();
    private WorkerTerminationWait? _terminationWait;
    private int _disposed;

    private WindowsWorkerProcess(
        SafeProcessHandle process,
        NamedPipeServerStream pipe,
        ulong nonceLow,
        ulong nonceHigh,
        string? workerExecutableSha256,
        int queueCapacity,
        WorkerObjectSecurity.WorkerObjectSecurityContext objectSecurity)
    {
        _process = process;
        _pipe = pipe;
        _objectSecurity = objectSecurity;
        NonceLow = nonceLow;
        NonceHigh = nonceHigh;
        WorkerExecutableSha256 = workerExecutableSha256;
        _queueCapacity = queueCapacity;
        InstanceId = Interlocked.Increment(ref s_nextInstanceId);
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

    public static async Task<WindowsWorkerProcess> StartAsync(
        string executablePath,
        ResvgMarkdownSvgRendererOptions options,
        WorkerJob job,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("The resvg worker is packaged for Windows only.");
        if (!File.Exists(executablePath))
            throw new FileNotFoundException("The architecture-specific resvg worker is missing.", executablePath);

        byte[] nonceBytes = RandomNumberGenerator.GetBytes(16);
        ulong nonceLow = BitConverter.ToUInt64(nonceBytes, 0);
        ulong nonceHigh = BitConverter.ToUInt64(nonceBytes, 8);
        string nonce = $"{nonceHigh:x16}{nonceLow:x16}";
        string pipeName = $"MarkdownRenderer.Resvg.{Environment.ProcessId}.{nonce}";
        using SafeTokenHandle sourceToken = WorkerProcessSandbox.OpenCurrentPrimaryTokenForWorker();
        WorkerObjectSecurity.WorkerObjectSecurityContext? objectSecurity =
            WorkerObjectSecurity.CreateForPrimaryToken(sourceToken);

        SafeProcessHandle? process = null;
        NamedPipeServerStream? pipe = null;
        string? workerExecutableSha256 = null;
        long processStartedTimestamp = 0;
        try
        {
            var pipeSecurity = new PipeSecurity();
            pipeSecurity.SetSecurityDescriptorSddlForm(
                objectSecurity.RestrictedDaclSddl,
                AccessControlSections.Access);
            pipe = NamedPipeServerStreamAcl.Create(
                pipeName,
                PipeDirection.InOut,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.WriteThrough,
                WorkerProtocol.ResponseSize,
                WorkerProtocol.RequestSize,
                pipeSecurity,
                HandleInheritability.None,
                (PipeAccessRights)0);

            // Keep the newly created pipe inside the same ownership scope as
            // startup so a label failure cannot leak its server handle.
            WorkerObjectSecurity.SetLowIntegrityLabel(pipe.SafePipeHandle);
            FileStream? identityFile = null;
            try
            {
                // This is diagnostic-only work. Keep the read handle open until
                // CreateProcess has loaded the image so the recorded digest
                // cannot silently refer to a replacement file at the same path.
                if (WorkerTimeoutEvents.Log.IsEnabled())
                {
                    try
                    {
                        identityFile = new FileStream(
                            executablePath,
                            FileMode.Open,
                            FileAccess.Read,
                            FileShare.Read,
                            bufferSize: 64 * 1024,
                            FileOptions.Asynchronous | FileOptions.SequentialScan);
                        byte[] digest = await SHA256.HashDataAsync(identityFile, cancellationToken)
                            .ConfigureAwait(false);
                        workerExecutableSha256 = Convert.ToHexString(digest);
                    }
                    catch (IOException)
                    {
                        identityFile?.Dispose();
                        identityFile = null;
                    }
                    catch (UnauthorizedAccessException)
                    {
                        identityFile?.Dispose();
                        identityFile = null;
                    }
                }

                processStartedTimestamp = Stopwatch.GetTimestamp();
                process = WorkerProcessSandbox.StartRestrictedSuspended(
                    executablePath,
                    pipeName,
                    nonce,
                    Path.GetDirectoryName(executablePath) ?? AppContext.BaseDirectory,
                    job,
                    sourceToken,
                    objectSecurity);
            }
            finally
            {
                identityFile?.Dispose();
            }

            using var startup = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            startup.CancelAfter(WorkerSchedulingPolicy.InitializationDeadline);
            try
            {
                await WorkerStartupWait.WaitForConnectionAsync(
                    token => pipe.WaitForConnectionAsync(token),
                    process,
                    startup.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
            {
                WorkerTimeoutEvents.Log.Timeout(
                    stage: 0,
                    deadlineMilliseconds: (int)WorkerSchedulingPolicy.InitializationDeadline.TotalMilliseconds,
                    workerProcessCpuMilliseconds: -1,
                    transportPhase: -1,
                    requestWriteMilliseconds: -1,
                    workerExited: -1,
                    openProgressPhase: -1,
                    workerWorkingSetKiB: -1,
                    workerPrivateCommitKiB: -1,
                    workerPageFaults: -1,
                    elapsedWallMilliseconds: GetElapsedMilliseconds(processStartedTimestamp),
                    workerInputSha256: string.Empty,
                    workerExecutableSha256: workerExecutableSha256 ?? string.Empty);
                throw new WorkerInitializationDeadlineException(
                    "The resvg worker did not complete startup before its initialization deadline.",
                    exception);
            }

            var worker = new WindowsWorkerProcess(
                process,
                pipe,
                nonceLow,
                nonceHigh,
                workerExecutableSha256,
                options.QueueCapacity,
                objectSecurity);
            process = null;
            objectSecurity = null;
            pipe = null!;
            return worker;
        }
        finally
        {
            if (process is not null)
            {
                _ = WorkerNativeMethods.TerminateProcess(process, WorkerProcessSandbox.WorkerTerminationExitCode);
                _ = WorkerNativeMethods.WaitForSingleObject(process, WorkerProcessSandbox.WorkerTerminationWaitMilliseconds);
                process.Dispose();
            }
            pipe?.Dispose();
            objectSecurity?.Dispose();
        }
    }

    public void MarkFontCatalogReady(long generation) => _fontCatalogGeneration.MarkReady(generation);

    // Every internal caller consumes this operation exactly once. The runtime
    // pool clears its state machine (request, mapping and execution context)
    // when consumed; source/raster ownership is never pooled here. Callers that
    // need Task semantics must convert once with AsTask before sharing it.
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
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        int queued = Interlocked.Increment(ref _queued);
        if (queued > _queueCapacity)
        {
            Interlocked.Decrement(ref _queued);
            throw new MarkdownSvgException(
                MarkdownRenderer.Images.MarkdownSvgFailureReason.ResourceLimitExceeded,
                "The SVG worker queue is full.");
        }

        bool entered = false;
        long transactionQueueStartedAt = SvgWorkerStageTrace.Start(diagnosticWorkId);
        try
        {
            await _transactions.WaitAsync(cancellationToken).ConfigureAwait(false);
            entered = true;
            SvgWorkerStageTrace.Record(
                diagnosticWorkId,
                SvgWorkerStageId.WorkerTransactionQueueWait,
                InstanceId,
                request.RequestId,
                diagnosticDocumentId != 0 ? diagnosticDocumentId : request.DocumentId,
                transactionQueueStartedAt,
                sourceBytes: diagnosticSourceBytes >= 0 ? diagnosticSourceBytes : request.SourceLength,
                targetPixels: diagnosticTargetPixels >= 0
                    ? diagnosticTargetPixels
                    : (long)request.TargetWidth * request.TargetHeight);
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (HasExited)
                throw new WorkerProtocolException("The resvg worker exited before the request.");

            int effectiveStageId = request.Operation == WorkerOperation.Render &&
                diagnosticStageId == (int)SvgWorkerStageId.RenderTransaction
                ? (int)SvgWorkerStageId.RenderTransaction
                : diagnosticStageId;
            long diagnosticTransactionStartedAt = SvgWorkerStageTrace.Start(
                diagnosticWorkId > 0 && effectiveStageId != 0 ? diagnosticWorkId : 0);
            long diagnosticWorkerCpuStartedAt = diagnosticTransactionStartedAt != 0 &&
                request.Operation is WorkerOperation.Open or WorkerOperation.Render
                    ? GetProcessCpuTicks()
                    : -1;
            // Diagnostic capture must not add a process query to every normal
            // SVG request. The listener is installed before diagnostic rendering.
            bool captureTimeoutEvidence = WorkerTimeoutEvents.Log.IsEnabled();
            long workerCpuAtStart = captureTimeoutEvidence
                ? timeoutEvidenceScope?.WorkerCpuAtStart ?? GetProcessCpuTicks()
                : -1;
            WorkerTimeoutEvidenceScope evidenceWindow = WorkerTimeoutEvidenceScope.Resolve(
                deadline,
                workerCpuAtStart,
                timeoutEvidenceScope);
            using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            operation.CancelAfter(deadline);
            bool needsMapping = request.Operation is WorkerOperation.Open or WorkerOperation.Render or
                WorkerOperation.FontCatalog or WorkerOperation.RasterDecode;
            if (needsMapping != (sharedMemory is not null) || request.MappingHandle != 0)
                throw new WorkerProtocolException("The SVG exchange has an invalid shared-memory owner.");
            using WorkerMappingLease? mappingLease = sharedMemory?.DuplicateInto(_process,
                readOnly: request.Operation == WorkerOperation.FontCatalog);
            request = request with { MappingHandle = mappingLease?.ProtocolHandle ?? 0 };
            WorkerProtocol.Encode(request, _requestFrame);
            byte[] requestFrame = _requestFrame;
            byte[] responseFrame = _responseFrame;
            long requestStarted = captureTimeoutEvidence ? Stopwatch.GetTimestamp() : 0;
            int transportPhase = 0;
            int requestWriteMilliseconds = -1;
            try
            {
                long pipeStageStartedAt = SvgWorkerStageTrace.Start(diagnosticWorkId);
                await _pipe.WriteAsync(requestFrame, operation.Token).ConfigureAwait(false);
                SvgWorkerStageTrace.Record(
                    diagnosticWorkId,
                    SvgWorkerStageId.PipeRequestWrite,
                    InstanceId,
                    request.RequestId,
                    diagnosticDocumentId != 0 ? diagnosticDocumentId : request.DocumentId,
                    pipeStageStartedAt);
                pipeStageStartedAt = SvgWorkerStageTrace.Start(diagnosticWorkId);
                transportPhase = 1;
                await _pipe.FlushAsync(operation.Token).ConfigureAwait(false);
                SvgWorkerStageTrace.Record(
                    diagnosticWorkId,
                    SvgWorkerStageId.PipeRequestFlush,
                    InstanceId,
                    request.RequestId,
                    diagnosticDocumentId != 0 ? diagnosticDocumentId : request.DocumentId,
                    pipeStageStartedAt);
                if (captureTimeoutEvidence)
                {
                    requestWriteMilliseconds = (int)Math.Min(
                        Stopwatch.GetElapsedTime(requestStarted).TotalMilliseconds,
                        int.MaxValue);
                }
                transportPhase = 2;
                pipeStageStartedAt = SvgWorkerStageTrace.Start(diagnosticWorkId);
                await _pipe.ReadExactlyAsync(responseFrame, operation.Token).ConfigureAwait(false);
                SvgWorkerStageTrace.Record(
                    diagnosticWorkId,
                    SvgWorkerStageId.PipeResponseRead,
                    InstanceId,
                    request.RequestId,
                    diagnosticDocumentId != 0 ? diagnosticDocumentId : request.DocumentId,
                    pipeStageStartedAt);
            }
            catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
            {
                long workerCpuAtTimeout = evidenceWindow.WorkerCpuAtStart >= 0
                    ? GetProcessCpuTicks()
                    : -1;
                int workerProcessCpuMilliseconds = evidenceWindow.WorkerCpuAtStart >= 0 &&
                    workerCpuAtTimeout >= evidenceWindow.WorkerCpuAtStart
                        ? (int)Math.Min((workerCpuAtTimeout - evidenceWindow.WorkerCpuAtStart) / 10_000, int.MaxValue)
                        : -1;
                if (captureTimeoutEvidence)
                {
                    WorkerMemorySnapshot memory = GetProcessMemorySnapshot();
                    WorkerTimeoutEvents.Log.Timeout(
                        stage: (int)request.Operation,
                        deadlineMilliseconds: (int)evidenceWindow.TotalDeadline.TotalMilliseconds,
                        workerProcessCpuMilliseconds,
                        transportPhase,
                        requestWriteMilliseconds,
                        workerExited: HasExited ? 1 : 0,
                        openProgressPhase: request.Operation == WorkerOperation.Open
                            ? sharedMemory?.ReadOpenProgressPhase() ?? -1 : -1,
                        workerWorkingSetKiB: memory.WorkingSetKiB,
                        workerPrivateCommitKiB: memory.PrivateCommitKiB,
                        workerPageFaults: memory.PageFaults,
                        elapsedWallMilliseconds: GetElapsedMilliseconds(evidenceWindow.WallStartedAt),
                        workerInputSha256: captureTimeoutEvidence
                            ? Convert.ToHexString(request.ContentHash)
                            : string.Empty,
                        workerExecutableSha256: WorkerExecutableSha256 ?? string.Empty);
                }
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

            WorkerResponse decoded;
            try
            {
                decoded = WorkerProtocol.DecodeResponse(
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
            if (request.Operation == WorkerOperation.Render &&
                decoded.Status == WorkerStatus.DocumentMissing)
            {
                effectiveStageId = (int)SvgWorkerStageId.DocumentMissingProbe;
            }
            long diagnosticWorkerCpuTicks = -1;
            if (diagnosticWorkerCpuStartedAt >= 0 && diagnosticWorkerCpuStartedAt != -1)
            {
                long workerCpuAtEnd = GetProcessCpuTicks();
                if (workerCpuAtEnd >= diagnosticWorkerCpuStartedAt)
                    diagnosticWorkerCpuTicks = workerCpuAtEnd - diagnosticWorkerCpuStartedAt;
            }
            if (diagnosticTransactionStartedAt != 0 && Enum.IsDefined((SvgWorkerStageId)effectiveStageId))
            {
                SvgWorkerStageTrace.Record(
                    diagnosticWorkId,
                    (SvgWorkerStageId)effectiveStageId,
                    InstanceId,
                    request.RequestId,
                    diagnosticDocumentId != 0 ? diagnosticDocumentId : request.DocumentId,
                    diagnosticTransactionStartedAt,
                    diagnosticWorkerCpuTicks,
                    diagnosticSourceBytes >= 0 ? diagnosticSourceBytes : request.SourceLength,
                    diagnosticTargetPixels >= 0
                        ? diagnosticTargetPixels
                        : (long)request.TargetWidth * request.TargetHeight);
            }
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

    private static int GetElapsedMilliseconds(long startedAt) =>
        startedAt == 0
            ? -1
            : (int)Math.Min(Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds, int.MaxValue);

    internal void DisposeForRestart() => DisposeCore(waitForExit: true);

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
            try { RetireFontCatalogPreparation(); }
            catch (Exception exception) { cleanupFailure ??= exception; }
            ReleaseObjectSecurityWhenFontCatalogDrained();
        }

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

        if (cleanupFailure is not null)
            ExceptionDispatchInfo.Capture(cleanupFailure).Throw();

        // An active ExchangeAsync releases this gate in its finally block.
        // SemaphoreSlim owns no native handle, so retaining it avoids masking
        // the typed cancellation/transport failure during concurrent disposal.
    }

    private void ReleaseObjectSecurityWhenFontCatalogDrained()
    {
        Task? preparation;
        lock (_fontPreparationGate)
            preparation = _fontPreparationTask;

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
        try { await DrainFontCatalogPreparationAsync().ConfigureAwait(false); }
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
