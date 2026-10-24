using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
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
                return WaitForSingleObject(_process, 0) != WaitTimeout;
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
        using SafeTokenHandle sourceToken = OpenCurrentPrimaryTokenForWorker();
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
                // This is audit-only work. Keep the read handle open until
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
                process = StartRestrictedSuspended(
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
                _ = TerminateProcess(process, WorkerTerminationExitCode);
                _ = WaitForSingleObject(process, WorkerTerminationWaitMilliseconds);
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
#if MARKDOWN_LAYOUT_DIAGNOSTICS
        ,
        long diagnosticWorkId = 0,
        int diagnosticStageId = 0,
        ulong diagnosticDocumentId = 0,
        long diagnosticSourceBytes = -1,
        long diagnosticTargetPixels = -1)
#else
        )
#endif
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
#if MARKDOWN_LAYOUT_DIAGNOSTICS
        long transactionQueueStartedAt = SvgWorkerStageTrace.Start(diagnosticWorkId);
#endif
        try
        {
            await _transactions.WaitAsync(cancellationToken).ConfigureAwait(false);
            entered = true;
#if MARKDOWN_LAYOUT_DIAGNOSTICS
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
#endif
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (HasExited)
                throw new WorkerProtocolException("The resvg worker exited before the request.");

#if MARKDOWN_LAYOUT_DIAGNOSTICS
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
#endif
            // Audit-only evidence must not add a process query to every normal
            // SVG request. The listener is installed before audit rendering.
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
#if MARKDOWN_LAYOUT_DIAGNOSTICS
                long pipeStageStartedAt = SvgWorkerStageTrace.Start(diagnosticWorkId);
#endif
                await _pipe.WriteAsync(requestFrame, operation.Token).ConfigureAwait(false);
#if MARKDOWN_LAYOUT_DIAGNOSTICS
                SvgWorkerStageTrace.Record(
                    diagnosticWorkId,
                    SvgWorkerStageId.PipeRequestWrite,
                    InstanceId,
                    request.RequestId,
                    diagnosticDocumentId != 0 ? diagnosticDocumentId : request.DocumentId,
                    pipeStageStartedAt);
                pipeStageStartedAt = SvgWorkerStageTrace.Start(diagnosticWorkId);
#endif
                transportPhase = 1;
                await _pipe.FlushAsync(operation.Token).ConfigureAwait(false);
#if MARKDOWN_LAYOUT_DIAGNOSTICS
                SvgWorkerStageTrace.Record(
                    diagnosticWorkId,
                    SvgWorkerStageId.PipeRequestFlush,
                    InstanceId,
                    request.RequestId,
                    diagnosticDocumentId != 0 ? diagnosticDocumentId : request.DocumentId,
                    pipeStageStartedAt);
#endif
                if (captureTimeoutEvidence)
                {
                    requestWriteMilliseconds = (int)Math.Min(
                        Stopwatch.GetElapsedTime(requestStarted).TotalMilliseconds,
                        int.MaxValue);
                }
                transportPhase = 2;
#if MARKDOWN_LAYOUT_DIAGNOSTICS
                pipeStageStartedAt = SvgWorkerStageTrace.Start(diagnosticWorkId);
#endif
                await _pipe.ReadExactlyAsync(responseFrame, operation.Token).ConfigureAwait(false);
#if MARKDOWN_LAYOUT_DIAGNOSTICS
                SvgWorkerStageTrace.Record(
                    diagnosticWorkId,
                    SvgWorkerStageId.PipeResponseRead,
                    InstanceId,
                    request.RequestId,
                    diagnosticDocumentId != 0 ? diagnosticDocumentId : request.DocumentId,
                    pipeStageStartedAt);
#endif
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
#if MARKDOWN_LAYOUT_DIAGNOSTICS
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
            if (Enum.IsDefined(typeof(SvgWorkerStageId), effectiveStageId))
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
#endif
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
            if (!GetProcessTimes(_process, out _, out _, out FileTime kernel, out FileTime user))
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
            var counters = new ProcessMemoryCountersEx
            {
                Size = (uint)Marshal.SizeOf<ProcessMemoryCountersEx>()
            };
            if (!GetProcessMemoryInfo(_process, ref counters, counters.Size))
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

    internal readonly record struct WorkerMemorySnapshot(
        int WorkingSetKiB,
        int PrivateCommitKiB,
        int PageFaults)
    {
        internal static WorkerMemorySnapshot Unavailable => new(-1, -1, -1);
    }

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
                        checked((int)WorkerTerminationWaitMilliseconds));
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
                    _ = TerminateProcess(_process, WorkerTerminationExitCode);
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
                            _ = WaitForSingleObject(_process, WorkerTerminationWaitMilliseconds);
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
            _ = terminationWait.WaitForExit(checked((int)WorkerTerminationWaitMilliseconds));
        }
        else if (waitForPrimaryHandle)
        {
            try
            {
                _ = WaitForSingleObject(_process, WorkerTerminationWaitMilliseconds);
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

    internal static WorkerJob CreateConstrainedJob(long commitBytes, int activeProcessLimit)
    {
        nint rawJob = CreateJobObjectW(0, null);
        if (rawJob == 0 || rawJob == -1)
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "A resvg worker job object could not be created.");
        var job = new SafeJobHandle(rawJob);
        var information = new JobObjectExtendedLimitInformation
        {
            BasicLimitInformation = new JobObjectBasicLimitInformation
            {
                LimitFlags = JobObjectLimitFlags.ActiveProcess |
                             JobObjectLimitFlags.DieOnUnhandledException |
                             JobObjectLimitFlags.JobMemory |
                             JobObjectLimitFlags.KillOnJobClose,
                ActiveProcessLimit = checked((uint)activeProcessLimit),
            },
            JobMemoryLimit = checked((nuint)commitBytes),
        };
        if (!SetInformationJobObject(
                job,
                JobObjectInformationClass.ExtendedLimitInformation,
                ref information,
                (uint)Marshal.SizeOf<JobObjectExtendedLimitInformation>()))
        {
            int error = Marshal.GetLastPInvokeError();
            job.Dispose();
            throw new Win32Exception(error, "The resvg worker job limits could not be applied.");
        }
        return new WorkerJob(job);
    }

    private static unsafe SafeProcessHandle StartRestrictedSuspended(
        string executablePath,
        string pipeName,
        string nonce,
        string workingDirectory,
        WorkerJob job,
        SafeTokenHandle sourceToken,
        WorkerObjectSecurity.WorkerObjectSecurityContext objectSecurity)
    {
        byte[] restrictedCodeSid = GetSidBytes("S-1-5-12");
        fixed (byte* restrictedCodeSidPointer = restrictedCodeSid)
        {
            var restrictingSid = new SidAndAttributes
            {
                Sid = (nint)restrictedCodeSidPointer,
                Attributes = 0,
            };
            if (!CreateRestrictedToken(
                    sourceToken,
                    DisableMaxPrivilege,
                    0,
                    0,
                    0,
                    0,
                    1,
                    (nint)(&restrictingSid),
                    out SafeTokenHandle restrictedToken))
            {
                int error = Marshal.GetLastPInvokeError();
                throw new Win32Exception(error, $"A restricted SVG worker token could not be created (Win32 error {error}).");
            }

            using (restrictedToken)
            {
                SetLowIntegrity(restrictedToken);
                SetRestrictedDefaultDacl(restrictedToken, objectSecurity);
                string commandLine = $"\"{executablePath}\" --pipe {pipeName} --nonce {nonce}";
                char[] mutableCommandLine = [.. commandLine, '\0'];
                char[] sanitizedEnvironment = BuildSanitizedEnvironment();
                nuint attributeBytes = 0;
                _ = InitializeProcThreadAttributeList(0, 1, 0, ref attributeBytes);
                if (attributeBytes == 0)
                    throw new Win32Exception(Marshal.GetLastPInvokeError(), "The SVG worker mitigation list size could not be determined.");
                nint attributeList = Marshal.AllocHGlobal(checked((nint)attributeBytes));
                try
                {
                    if (!InitializeProcThreadAttributeList(attributeList, 1, 0, ref attributeBytes))
                    {
                        throw new Win32Exception(Marshal.GetLastPInvokeError(), "The SVG worker mitigation list could not be initialized.");
                    }

                    try
                    {
                        ulong mitigationPolicy = CreateMitigationPolicy();
                        if (!UpdateProcThreadAttribute(
                                attributeList,
                                0,
                                ProcThreadAttributeMitigationPolicy,
                                (nint)(&mitigationPolicy),
                                checked((nuint)sizeof(ulong)),
                                0,
                                0))
                        {
                            throw new Win32Exception(Marshal.GetLastPInvokeError(), "The SVG worker process mitigations could not be applied.");
                        }

                        var startup = new StartupInfoEx
                        {
                            StartupInfo = new StartupInfo
                            {
                                Size = checked((uint)Marshal.SizeOf<StartupInfoEx>()),
                            },
                            AttributeList = attributeList,
                        };
                        fixed (char* commandLinePointer = mutableCommandLine)
                        fixed (char* environmentPointer = sanitizedEnvironment)
                        {
                            if (!CreateProcessAsUserW(
                                    restrictedToken,
                                    executablePath,
                                    commandLinePointer,
                                    0,
                                    0,
                                    inheritHandles: false,
                                    CreateNoWindow | CreateSuspended | CreateUnicodeEnvironment | ExtendedStartupInfoPresent,
                                    (nint)environmentPointer,
                                    workingDirectory,
                                    ref startup,
                                    out ProcessInformation information))
                            {
                                throw new Win32Exception(Marshal.GetLastPInvokeError(), "The restricted SVG worker could not start.");
                            }

                            var process = new SafeProcessHandle(information.Process, ownsHandle: true);
                            using var thread = new SafeWaitHandle(information.Thread, ownsHandle: true);
                            try
                            {
                                job.Assign(process);
                                if (RuntimeInformation.ProcessArchitecture == Architecture.X86)
                                    AttachX86StartupToken(thread, sourceToken, objectSecurity);
                                if (ResumeThread(thread) == uint.MaxValue)
                                    throw new Win32Exception(Marshal.GetLastPInvokeError(), "The resvg worker could not leave its suspended launch state.");
                                return process;
                            }
                            catch
                            {
                                _ = TerminateProcess(process, WorkerTerminationExitCode);
                                _ = WaitForSingleObject(process, WorkerTerminationWaitMilliseconds);
                                process.Dispose();
                                throw;
                            }
                        }
                    }
                    finally
                    {
                        DeleteProcThreadAttributeList(attributeList);
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(attributeList);
                }
            }
        }
    }

    internal static SafeTokenHandle OpenCurrentPrimaryTokenForWorker()
    {
        if (!OpenProcessToken(
                GetCurrentProcess(),
                TokenAssignPrimary | TokenDuplicate | TokenQuery | TokenAdjustDefault,
                out SafeTokenHandle sourceToken))
        {
            throw new Win32Exception(
                Marshal.GetLastPInvokeError(),
                "The host process primary token could not be opened for SVG worker isolation.");
        }

        return sourceToken;
    }

    private static ulong CreateMitigationPolicy()
    {
        ulong policy = MitigationDepEnable |
            MitigationSehopEnable |
            MitigationHeapTerminateAlwaysOn |
            MitigationBottomUpAslrAlwaysOn |
            MitigationStrictHandleChecksAlwaysOn |
            MitigationWin32kDisableAlwaysOn |
            MitigationExtensionPointDisableAlwaysOn |
            MitigationProhibitDynamicCodeAlwaysOn |
            MitigationImageLoadNoRemoteAlwaysOn |
            MitigationImageLoadNoLowLabelAlwaysOn |
            MitigationImageLoadPreferSystem32AlwaysOn;
        if (Environment.Is64BitProcess)
            policy |= MitigationHighEntropyAslrAlwaysOn;
        return policy;
    }

    private static unsafe void SetLowIntegrity(SafeTokenHandle token)
    {
        byte[] lowIntegritySid = GetSidBytes("S-1-16-4096");
        fixed (byte* sidPointer = lowIntegritySid)
        {
            var label = new TokenMandatoryLabel
            {
                Label = new SidAndAttributes
                {
                    Sid = (nint)sidPointer,
                    Attributes = SeGroupIntegrity,
                },
            };
            uint length = checked((uint)(Marshal.SizeOf<TokenMandatoryLabel>() + lowIntegritySid.Length));
            if (!SetTokenInformation(
                    token,
                    TokenInformationClass.TokenIntegrityLevel,
                    (nint)(&label),
                    length))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "The SVG worker token could not be lowered to low integrity.");
            }
        }
    }

    private static unsafe void SetRestrictedDefaultDacl(
        SafeTokenHandle token,
        WorkerObjectSecurity.WorkerObjectSecurityContext objectSecurity)
    {
        // The inherited user-only DACL fails the token's second (restricting-SID)
        // access check when the loader or runtime opens its own kernel objects.
        // Change only defaults for objects created with this worker token; never
        // grant the worker access to host files, the desktop or existing objects.
        WorkerObjectSecurity.SecurityDescriptorHandle descriptor = objectSecurity.Descriptor;
        bool descriptorReferenceAdded = false;
        try
        {
            descriptor.DangerousAddRef(ref descriptorReferenceAdded);
            if (!GetSecurityDescriptorDacl(
                    descriptor.DangerousGetHandle(), out int present, out nint dacl, out _) ||
                present == 0 || dacl == 0)
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(),
                    "The SVG worker default object permissions could not be created.");
            }

            var information = new TokenDefaultDacl { DefaultDacl = dacl };
            if (!SetTokenInformation(token, TokenInformationClass.TokenDefaultDacl,
                    (nint)(&information), checked((uint)sizeof(TokenDefaultDacl))))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(),
                    "The SVG worker default object permissions could not be applied.");
            }
        }
        finally
        {
            if (descriptorReferenceAdded)
                descriptor.DangerousRelease();
        }
    }

    private static byte[] GetSidBytes(string value)
    {
        var sid = new SecurityIdentifier(value);
        byte[] bytes = new byte[sid.BinaryLength];
        sid.GetBinaryForm(bytes, 0);
        return bytes;
    }

    internal static char[] BuildSanitizedEnvironment()
    {
        string windowsDirectory = Path.GetDirectoryName(Environment.SystemDirectory) ?? @"C:\Windows";
        string localApplicationData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string temporaryDirectory = Path.GetFullPath(Path.GetTempPath());
        string[] entries =
        [
            $"LOCALAPPDATA={localApplicationData}",
            $"SystemRoot={windowsDirectory}",
            $"TEMP={temporaryDirectory}",
            $"TMP={temporaryDirectory}",
            $"USERPROFILE={userProfile}",
            $"WINDIR={windowsDirectory}",
        ];
        Array.Sort(entries, StringComparer.OrdinalIgnoreCase);
        return [.. string.Join('\0', entries), '\0', '\0'];
    }

    private const uint TokenAssignPrimary = 0x0001;
    private const uint TokenDuplicate = 0x0002;
    private const uint TokenQuery = 0x0008;
    private const uint TokenAdjustDefault = 0x0080;
    private const uint DisableMaxPrivilege = 0x00000001;
    private const uint CreateSuspended = 0x00000004;
    private const uint CreateNoWindow = 0x08000000;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint ExtendedStartupInfoPresent = 0x00080000;
    private const uint SeGroupIntegrity = 0x00000020;
    private const uint WaitTimeout = 0x00000102;
    private const uint WorkerTerminationExitCode = 0xE0000001;
    private const uint WorkerTerminationWaitMilliseconds = 3_000;
    private const nuint ProcThreadAttributeMitigationPolicy = 0x00020007;
    private const ulong MitigationDepEnable = 0x00000001;
    private const ulong MitigationSehopEnable = 0x00000004;
    private const ulong MitigationHeapTerminateAlwaysOn = 0x00001000;
    private const ulong MitigationBottomUpAslrAlwaysOn = 0x00010000;
    private const ulong MitigationHighEntropyAslrAlwaysOn = 0x00100000;
    private const ulong MitigationStrictHandleChecksAlwaysOn = 0x01000000;
    private const ulong MitigationWin32kDisableAlwaysOn = 0x10000000;
    private const ulong MitigationExtensionPointDisableAlwaysOn = 0x0000000100000000;
    private const ulong MitigationProhibitDynamicCodeAlwaysOn = 0x0000001000000000;
    private const ulong MitigationImageLoadNoRemoteAlwaysOn = 0x0010000000000000;
    private const ulong MitigationImageLoadNoLowLabelAlwaysOn = 0x0100000000000000;
    private const ulong MitigationImageLoadPreferSystem32AlwaysOn = 0x1000000000000000;

    [Flags]
    private enum JobObjectLimitFlags : uint
    {
        ActiveProcess = 0x00000008,
        JobMemory = 0x00000200,
        DieOnUnhandledException = 0x00000400,
        KillOnJobClose = 0x00002000,
    }

    private enum JobObjectInformationClass
    {
        ExtendedLimitInformation = 9,
    }

    private enum TokenInformationClass
    {
        TokenDefaultDacl = 6,
        TokenIntegrityLevel = 25,
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenDefaultDacl
    {
        public nint DefaultDacl;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SidAndAttributes
    {
        public nint Sid;
        public uint Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenMandatoryLabel
    {
        public SidAndAttributes Label;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public JobObjectLimitFlags LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        public uint Size;
        public nint Reserved;
        public nint Desktop;
        public nint Title;
        public uint X;
        public uint Y;
        public uint XSize;
        public uint YSize;
        public uint XCountChars;
        public uint YCountChars;
        public uint FillAttribute;
        public uint Flags;
        public ushort ShowWindow;
        public ushort Reserved2Length;
        public nint Reserved2;
        public nint StandardInput;
        public nint StandardOutput;
        public nint StandardError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx
    {
        public StartupInfo StartupInfo;
        public nint AttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public nint Process;
        public nint Thread;
        public uint ProcessId;
        public uint ThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        public uint Low;
        public uint High;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessMemoryCountersEx
    {
        public uint Size;
        public uint PageFaultCount;
        public nuint PeakWorkingSetSize;
        public nuint WorkingSetSize;
        public nuint QuotaPeakPagedPoolUsage;
        public nuint QuotaPagedPoolUsage;
        public nuint QuotaPeakNonPagedPoolUsage;
        public nuint QuotaNonPagedPoolUsage;
        public nuint PagefileUsage;
        public nuint PeakPagefileUsage;
        public nuint PrivateUsage;
    }

    internal sealed class WorkerJob : IDisposable
    {
        private SafeJobHandle? _handle;

        internal WorkerJob(SafeJobHandle handle)
        {
            _handle = handle;
        }

        internal void Assign(SafeProcessHandle process)
        {
            SafeJobHandle handle = _handle ?? throw new ObjectDisposedException(nameof(WorkerJob));
            if (!AssignProcessToJobObject(handle, process))
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "The resvg worker could not enter its aggregate kill-on-close job.");
        }

        public void Dispose() => Interlocked.Exchange(ref _handle, null)?.Dispose();
    }

    internal sealed class SafeJobHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        internal SafeJobHandle(nint handle)
            : base(ownsHandle: true)
        {
            SetHandle(handle);
        }

        protected override bool ReleaseHandle() => CloseHandle(handle);
    }

    internal sealed class SafeTokenHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public SafeTokenHandle()
            : base(ownsHandle: true)
        {
        }

        protected override bool ReleaseHandle() => CloseHandle(handle);
    }

    [LibraryImport("kernel32.dll")]
    private static partial nint GetCurrentProcess();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetProcessTimes(
        SafeProcessHandle process,
        out FileTime creation,
        out FileTime exit,
        out FileTime kernel,
        out FileTime user);

    [LibraryImport("psapi.dll", EntryPoint = "GetProcessMemoryInfo", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetProcessMemoryInfo(
        SafeProcessHandle process,
        ref ProcessMemoryCountersEx counters,
        uint size);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenProcessToken(
        nint process,
        uint desiredAccess,
        out SafeTokenHandle token);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreateRestrictedToken(
        SafeTokenHandle existingToken,
        uint flags,
        uint disableSidCount,
        nint sidsToDisable,
        uint deletePrivilegeCount,
        nint privilegesToDelete,
        uint restrictedSidCount,
        nint sidsToRestrict,
        out SafeTokenHandle restrictedToken);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetSecurityDescriptorDacl(
        nint securityDescriptor, out int daclPresent, out nint dacl, out int daclDefaulted);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetTokenInformation(
        SafeTokenHandle token,
        TokenInformationClass tokenInformationClass,
        nint tokenInformation,
        uint tokenInformationLength);

    [LibraryImport("advapi32.dll", EntryPoint = "CreateProcessAsUserW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool CreateProcessAsUserW(
        SafeTokenHandle token,
        string applicationName,
        char* commandLine,
        nint processAttributes,
        nint threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
        uint creationFlags,
        nint environment,
        string currentDirectory,
        ref StartupInfoEx startupInfo,
        out ProcessInformation processInformation);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool InitializeProcThreadAttributeList(
        nint attributeList,
        uint attributeCount,
        uint flags,
        ref nuint size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UpdateProcThreadAttribute(
        nint attributeList,
        uint flags,
        nuint attribute,
        nint value,
        nuint size,
        nint previousValue,
        nint returnSize);

    [LibraryImport("kernel32.dll")]
    private static partial void DeleteProcThreadAttributeList(nint attributeList);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateJobObjectW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CreateJobObjectW(nint jobAttributes, string? name);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetInformationJobObject(
        SafeJobHandle job,
        JobObjectInformationClass informationClass,
        ref JobObjectExtendedLimitInformation information,
        uint informationLength);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AssignProcessToJobObject(SafeJobHandle job, SafeProcessHandle process);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial uint ResumeThread(SafeWaitHandle thread);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial uint WaitForSingleObject(SafeProcessHandle process, uint milliseconds);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TerminateProcess(SafeProcessHandle process, uint exitCode);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);
}

internal readonly record struct WorkerTimeoutEvidenceScope(
    TimeSpan TotalDeadline,
    long WorkerCpuAtStart,
    long WallStartedAt = 0)
{
    internal static WorkerTimeoutEvidenceScope Resolve(
        TimeSpan requestDeadline,
        long requestCpuAtStart,
        WorkerTimeoutEvidenceScope? outerWindow) =>
        outerWindow ?? new WorkerTimeoutEvidenceScope(
            requestDeadline,
            requestCpuAtStart,
            Stopwatch.GetTimestamp());
}

internal class WorkerDeadlineException : Exception
{
    public WorkerDeadlineException(string message, Exception inner)
        : base(message, inner)
    {
    }
}

internal sealed class WorkerInitializationDeadlineException : WorkerDeadlineException
{
    public WorkerInitializationDeadlineException(string message, Exception inner)
        : base(message, inner)
    {
    }
}

internal sealed class WorkerInitializationException : Exception
{
    public WorkerInitializationException(string message, Exception inner)
        : base(message, inner)
    {
    }
}
