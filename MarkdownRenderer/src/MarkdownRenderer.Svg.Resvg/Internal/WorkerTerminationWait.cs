using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace MarkdownRenderer.Svg.Resvg.Internal;

/// <summary>
/// Observes worker termination without tying up a thread. The duplicated
/// process handle remains valid independently of the worker's primary handle.
/// </summary>
internal sealed partial class WorkerTerminationWait
{
    private readonly ProcessWaitHandle _waitHandle;
    private readonly RegisteredWaitHandle _registration;
    private readonly TaskCompletionSource<bool> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _callbackCompleted =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _callbackStarted;

    private WorkerTerminationWait(SafeWaitHandle waitHandle, int timeoutMilliseconds)
    {
        _waitHandle = new ProcessWaitHandle(waitHandle);
        try
        {
            _registration = ThreadPool.RegisterWaitForSingleObject(
                _waitHandle,
                static (state, timedOut) => ((WorkerTerminationWait)state!).OnProcessSignaled(timedOut),
                this,
                timeoutMilliseconds,
                executeOnlyOnce: true);
        }
        catch
        {
            _waitHandle.Dispose();
            throw;
        }
    }

    internal static WorkerTerminationWait Register(SafeProcessHandle process, int timeoutMilliseconds)
    {
        ArgumentNullException.ThrowIfNull(process);
        if (timeoutMilliseconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(timeoutMilliseconds));

        return new WorkerTerminationWait(DuplicateProcessHandle(process), timeoutMilliseconds);
    }

    /// <summary>
    /// Waits only for the already-registered process signal/deadline. No
    /// thread is occupied while an ordinary non-blocking Dispose is pending.
    /// </summary>
    internal bool WaitForExit(int timeoutMilliseconds)
    {
        if (!_completion.Task.Wait(timeoutMilliseconds))
            return false;

        bool unregistered = _registration.Unregister(null);
        if (!unregistered || Volatile.Read(ref _callbackStarted) != 0)
            _callbackCompleted.Task.GetAwaiter().GetResult();

        return _completion.Task.GetAwaiter().GetResult();
    }

    internal async Task<bool> WaitForExitAsync()
    {
        // The native registration already owns the bounded deadline. Await
        // its callback cleanup as well as its result, without blocking a UI
        // thread or allowing disposal to return before the wait handle closes.
        bool exited = await _completion.Task.ConfigureAwait(false);
        await _callbackCompleted.Task.ConfigureAwait(false);
        _registration.Unregister(null);
        return exited;
    }

    private void OnProcessSignaled(bool timedOut)
    {
        Volatile.Write(ref _callbackStarted, 1);
        try
        {
            _completion.TrySetResult(!timedOut);
        }
        finally
        {
            _waitHandle.Dispose();
            _callbackCompleted.TrySetResult();
        }
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
                    "The resvg worker process handle could not be duplicated for shutdown monitoring.");
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

    private sealed class ProcessWaitHandle : WaitHandle
    {
        internal ProcessWaitHandle(SafeWaitHandle handle) => SafeWaitHandle = handle;
    }
}
