using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace MarkdownRenderer.Svg.Resvg.Internal;

/// <summary>
/// Races the worker's first pipe connection against its process signal without
/// occupying a thread while either event is pending.
/// </summary>
internal static partial class WorkerStartupWait
{
    internal static async Task WaitForConnectionAsync(
        Func<CancellationToken, Task> waitForConnection,
        SafeProcessHandle process,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(waitForConnection);
        ArgumentNullException.ThrowIfNull(process);

        await using var processExit = ProcessExitWait.Register(process);
        using var connectionCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task connectionTask = waitForConnection(connectionCancellation.Token);
        Task completed = await Task.WhenAny(connectionTask, processExit.ExitTask).ConfigureAwait(false);

        if (completed == connectionTask)
        {
            try
            {
                await connectionTask.ConfigureAwait(false);
                return;
            }
            catch (Exception exception) when (
                exception is IOException or InvalidOperationException)
            {
                if (!cancellationToken.IsCancellationRequested &&
                    processExit.TryGetExitCode(out uint exitCode))
                {
                    throw CreateExitException(exitCode, exception);
                }

                throw;
            }
        }

        // The child has exited. Cancel and fully observe the losing pipe wait
        // before its server stream or process handle can be disposed by the caller.
        connectionCancellation.Cancel();
        await ObserveCanceledConnectionAsync(connectionTask).ConfigureAwait(false);

        if (cancellationToken.IsCancellationRequested)
            cancellationToken.ThrowIfCancellationRequested();

        uint nativeExitCode = await processExit.ExitTask.ConfigureAwait(false);
        if (cancellationToken.IsCancellationRequested)
            cancellationToken.ThrowIfCancellationRequested();

        throw CreateExitException(nativeExitCode, innerException: null);
    }

    private static async Task ObserveCanceledConnectionAsync(Task connectionTask)
    {
        try
        {
            await connectionTask.ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is OperationCanceledException or IOException or InvalidOperationException)
        {
            // The process exit is the primary startup failure. The pipe task is
            // drained here solely to preserve ownership and avoid orphaned faults.
        }
    }

    private static WorkerProtocolException CreateExitException(uint exitCode, Exception? innerException) =>
        new(
            $"The resvg worker exited during startup with native exit code 0x{exitCode:X8}.",
            new WorkerStartupExitCodeException(exitCode, innerException));

    private sealed partial class ProcessExitWait : IAsyncDisposable
    {
        private readonly object _gate = new();
        private readonly ProcessWaitHandle _waitHandle;
        private readonly RegisteredWaitHandle _registration;
        private readonly TaskCompletionSource<uint> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _callbackCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _closing;
        private bool _callbackStarted;
        private int _disposed;

        private ProcessExitWait(SafeWaitHandle waitHandle)
        {
            _waitHandle = new ProcessWaitHandle(waitHandle);
            try
            {
                _registration = ThreadPool.RegisterWaitForSingleObject(
                    _waitHandle,
                    static (state, timedOut) => ((ProcessExitWait)state!).OnProcessSignaled(),
                    this,
                    Timeout.Infinite,
                    executeOnlyOnce: true);
            }
            catch
            {
                _waitHandle.Dispose();
                throw;
            }
        }

        internal Task<uint> ExitTask => _exit.Task;

        internal static ProcessExitWait Register(SafeProcessHandle process) =>
            new(DuplicateProcessHandle(process));

        internal bool TryGetExitCode(out uint exitCode)
        {
            uint waitResult = WaitForSingleObject(_waitHandle.SafeWaitHandle, milliseconds: 0);
            if (waitResult == WaitTimeout)
            {
                exitCode = 0;
                return false;
            }

            if (waitResult == WaitFailed)
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "The resvg worker process state could not be queried.");

            if (waitResult != WaitObject0)
                throw new InvalidOperationException("The resvg worker process wait returned an unexpected status.");

            if (!GetExitCodeProcess(_waitHandle.SafeWaitHandle, out exitCode))
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "The resvg worker exit code could not be queried.");

            return true;
        }

        private void OnProcessSignaled()
        {
            lock (_gate)
            {
                if (_closing)
                {
                    _callbackCompleted.TrySetResult();
                    return;
                }

                _callbackStarted = true;
            }

            try
            {
                if (!GetExitCodeProcess(_waitHandle.SafeWaitHandle, out uint exitCode))
                {
                    throw new Win32Exception(
                        Marshal.GetLastPInvokeError(),
                        "The resvg worker exit code could not be queried.");
                }

                _exit.TrySetResult(exitCode);
            }
            catch (Exception exception)
            {
                _exit.TrySetException(exception);
            }
            finally
            {
                _callbackCompleted.TrySetResult();
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            lock (_gate)
                _closing = true;

            bool unregistered = _registration.Unregister(null);
            bool callbackStarted;
            lock (_gate)
                callbackStarted = _callbackStarted;

            // A one-shot callback may already be queued/running when unregister
            // returns false (or may have entered immediately before _closing).
            // Drain that callback before releasing its duplicated process handle.
            if (!unregistered || callbackStarted)
                await _callbackCompleted.Task.ConfigureAwait(false);

            _waitHandle.Dispose();
        }

        private static SafeWaitHandle DuplicateProcessHandle(SafeProcessHandle process)
        {
            bool addedReference = false;
            try
            {
                process.DangerousAddRef(ref addedReference);
                nint currentProcess = GetCurrentProcess();
                if (!DuplicateHandle(
                        currentProcess,
                        process.DangerousGetHandle(),
                        currentProcess,
                        out nint duplicate,
                        desiredAccess: 0,
                        inheritHandle: false,
                        options: DuplicateSameAccess))
                {
                    throw new Win32Exception(
                        Marshal.GetLastPInvokeError(),
                        "The resvg worker process handle could not be duplicated for startup monitoring.");
                }

                return new SafeWaitHandle(duplicate, ownsHandle: true);
            }
            finally
            {
                if (addedReference)
                    process.DangerousRelease();
            }
        }

        private const uint DuplicateSameAccess = 0x00000002;
        private const uint WaitObject0 = 0;
        private const uint WaitTimeout = 0x00000102;
        private const uint WaitFailed = 0xFFFFFFFF;

        [LibraryImport("kernel32.dll")]
        private static partial nint GetCurrentProcess();

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool DuplicateHandle(
            nint sourceProcessHandle,
            nint sourceHandle,
            nint targetProcessHandle,
            out nint targetHandle,
            uint desiredAccess,
            [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
            uint options);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        private static partial uint WaitForSingleObject(SafeWaitHandle handle, uint milliseconds);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool GetExitCodeProcess(SafeWaitHandle process, out uint exitCode);

        private sealed class ProcessWaitHandle : WaitHandle
        {
            internal ProcessWaitHandle(SafeWaitHandle handle) => SafeWaitHandle = handle;
        }
    }
}

internal sealed class WorkerStartupExitCodeException : Exception
{
    internal WorkerStartupExitCodeException(uint nativeExitCode, Exception? innerException)
        : base($"The resvg worker exited with native exit code 0x{nativeExitCode:X8}.", innerException)
    {
        NativeExitCode = nativeExitCode;
    }

    internal uint NativeExitCode { get; }
}
