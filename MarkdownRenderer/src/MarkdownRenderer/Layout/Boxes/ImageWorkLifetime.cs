using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace MarkdownRenderer.Layout.Boxes;

/// <summary>
/// Owns an image box's image-work lock and everything that decides whether
/// the box may still start work: disposal, resource suspension, the count of
/// admitted operations and their drain signal, the lifetime and residency
/// cancellation sources, and the task-only retirement barrier with its
/// bounded failure list.
/// </summary>
/// <remarks>
/// This file is longer than 500 lines because admission, drain, both
/// cancellation sources and the retirement barrier read the same counters
/// under one lock; splitting them would split that lock. The barrier moves to
/// its own type once it can own a separate lock.
///
/// The box's SVG state, document custody and residency take
/// <see cref="Sync"/> for their own lock sections, so one lock orders every
/// image lifecycle change exactly as before. Members ending in NoLock expect
/// the caller to hold it; no member calls a host or provider under it.
/// </remarks>
internal sealed class ImageWorkLifetime
{
    private readonly IImageWorkLifetimeHost _host;
    private readonly MarkdownLayoutContext _context;
    private volatile bool _disposed;
    private volatile bool _suspended;
    private bool _residencyReleased;
    private int _workCount;
    private bool _cancellationDisposed;
    private bool _finalizing;
    private bool _completed;
    private ImageRetirementFailures? _detachedFailures;
    private List<Task>? _lateWork;
    private TaskCompletionSource? _workDrained;
    private TaskCompletionSource? _retirementCompletion;
    private CancellationTokenSource? _residencyCancellation;

    internal ImageWorkLifetime(
        IImageWorkLifetimeHost host,
        MarkdownLayoutContext context,
        CancellationTokenSource lifetimeCancellation)
    {
        _host = host;
        _context = context;
        LifetimeCancellation = lifetimeCancellation;
    }

    /// <summary>The image-work lock.</summary>
    internal readonly object Sync = new();

    /// <summary>Linked to the context's image token; canceled by disposal.</summary>
    internal CancellationTokenSource LifetimeCancellation { get; }

    /// <summary>True once the box is disposed.</summary>
    internal bool IsDisposed => _disposed;

    /// <summary>True while the box's resources are suspended for residency.</summary>
    internal bool IsSuspended => _suspended;

    /// <summary>True when the box is disposed or suspended.</summary>
    internal bool IsRetired => _disposed || _suspended;

    /// <summary>True once every admitted operation drained and terminal cleanup began.</summary>
    internal bool IsFinalizing => _finalizing;

    /// <summary>True once the retirement barrier completed.</summary>
    internal bool IsRetirementCompletedNoLock => _completed;

    /// <summary>True once disposal created the retirement barrier.</summary>
    internal bool HasRetirementBarrierNoLock => _retirementCompletion is not null;

    /// <summary>The number of admitted operations that have not drained.</summary>
    internal int WorkCountNoLock => _workCount;

    /// <summary>True when a residency cancellation source exists.</summary>
    internal bool HasResidencyCancellationNoLock => _residencyCancellation is not null;

    /// <summary>True once suspended resources were released.</summary>
    internal bool IsResidencyReleasedNoLock => _residencyReleased;

    /// <summary>The residency token while one exists, otherwise the lifetime token.</summary>
    internal CancellationToken CurrentWorkTokenNoLock =>
        (_residencyCancellation ?? LifetimeCancellation).Token;

    /// <summary>One read of the admitted image work, under the image-work lock.</summary>
    internal ImageWorkState GetWorkState()
    {
        lock (Sync)
            return new ImageWorkState(_workCount, _cancellationDisposed);
    }

    /// <summary>
    /// Completes after cancellation callbacks, admitted work, queued UI
    /// publications and native-resource release have drained.
    /// </summary>
    internal Task RetirementTask
    {
        get
        {
            lock (Sync)
                return _retirementCompletion?.Task ?? Task.CompletedTask;
        }
    }

    /// <summary>
    /// Admits one unit of image work unless the box is disposed or suspended.
    /// When offscreen release is enabled the work runs under a residency
    /// token linked to the lifetime token.
    /// </summary>
    internal bool TryBeginWork(out ImageWorkLease? work)
    {
        lock (Sync)
        {
            if (_disposed || _suspended)
            {
                work = null;
                return false;
            }

            if (_workCount == 0)
            {
                _workDrained = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            }

            if (_residencyCancellation is null &&
                _context.PerformanceSession?.Options.ReleaseOffscreenImageResources == true)
                _residencyCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                    LifetimeCancellation.Token);
            var createdWork = new ImageWorkLease(this,
                (_residencyCancellation ?? LifetimeCancellation).Token);
            _workCount++;
            work = createdWork;
            return true;
        }
    }

    /// <summary>Completes one unit of work; the last one signals the drain.</summary>
    internal void CompleteWork()
    {
        TaskCompletionSource? workDrained = null;
        lock (Sync)
        {
            if (_workCount <= 0)
                return;

            _workCount--;
            if (_workCount == 0)
                workDrained = _workDrained;
        }

        SignalWorkDrained(workDrained);
    }

    /// <summary>Admits one unit of work under the caller's image-work lock.</summary>
    internal ImageWorkLease BeginWorkNoLock(CancellationToken token)
    {
        var lease = new ImageWorkLease(this, token);
        AddWorkNoLock();
        return lease;
    }

    /// <summary>Counts one unit of work whose release the caller owns.</summary>
    internal void AddWorkNoLock()
    {
        if (_workCount == 0)
        {
            _workDrained = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
        }

        _workCount++;
    }

    /// <summary>
    /// Releases one unit counted by <see cref="AddWorkNoLock"/> and returns
    /// the drain signal to set once the caller leaves the lock.
    /// </summary>
    internal TaskCompletionSource? ReleaseWorkNoLock()
    {
        _workCount--;
        return _workCount == 0 ? _workDrained : null;
    }

    /// <summary>Sets a drain signal returned under the lock, then tells the host.</summary>
    internal void SignalWorkDrained(TaskCompletionSource? workDrained)
    {
        workDrained?.TrySetResult();
        if (workDrained is not null)
            _host.OnImageWorkDrained();
    }

    /// <summary>
    /// Keeps the retirement barrier open until <paramref name="detachedOperation"/>
    /// completes, recording its fault when <paramref name="failRetirementOnFault"/>.
    /// </summary>
    internal void TrackDetachedWork(
        Task detachedOperation,
        bool failRetirementOnFault = false)
    {
        ArgumentNullException.ThrowIfNull(detachedOperation);
        if (detachedOperation.IsCompleted)
        {
            if (detachedOperation.IsFaulted)
            {
                _ = detachedOperation.Exception;
                if (failRetirementOnFault)
                    RecordFailure(detachedOperation.Exception!.GetBaseException());
            }
            return;
        }

        if (!TryAdmitDetachedWork(out ImageWorkLease? lease, out TaskCompletionSource? lateWorkCompletion))
        {
            TaskFaultObservation.ObserveFault(detachedOperation);
            var escapedWork = new InvalidOperationException(
                "Detached image work was registered after its retirement barrier completed.");
            RecordFailure(escapedWork);
            throw escapedWork;
        }

        _ = detachedOperation.ContinueWith(
            static (completed, state) => CompleteDetachedWork(
                completed,
                ((ImageWorkLifetime Owner, ImageWorkLease? Lease, bool FailRetirement, TaskCompletionSource? LateWorkCompletion))state!),
            (this, lease, failRetirementOnFault, lateWorkCompletion),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    /// <summary>
    /// Opens a work lease for a detached operation, or a task-only late
    /// barrier while native cleanup is finalizing. False once retirement has
    /// completed, when nothing can hold it open any more.
    /// </summary>
    private bool TryAdmitDetachedWork(
        out ImageWorkLease? lease,
        out TaskCompletionSource? lateWorkCompletion)
    {
        lease = null;
        lateWorkCompletion = null;
        lock (Sync)
        {
            if (_completed)
                return false;

            // A child operation normally registers while its parent lease
            // is still active. If a future caller violates that ordering,
            // reopen the drain before finalization or add a task-only late
            // barrier while native cleanup is finalizing. Never silently
            // report successful retirement with detached work outstanding.
            if (_disposed && _workCount == 0)
            {
                RecordFailureNoLock(new InvalidOperationException(
                    "Detached image work escaped its parent retirement lease."));
                if (_finalizing)
                {
                    lateWorkCompletion = new TaskCompletionSource(
                        TaskCreationOptions.RunContinuationsAsynchronously);
                    (_lateWork ??= []).Add(lateWorkCompletion.Task);
                    return true;
                }
            }

            if (_workCount == 0)
            {
                _workDrained = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            }

            _workCount++;
            lease = new ImageWorkLease(this, CancellationToken.None);
            return true;
        }
    }

    /// <summary>Records a tracked operation's fault, then releases what held retirement open.</summary>
    private static void CompleteDetachedWork(
        Task completed,
        (ImageWorkLifetime Owner, ImageWorkLease? Lease, bool FailRetirement, TaskCompletionSource? LateWorkCompletion) tracking)
    {
        if (completed.IsFaulted)
        {
            if (tracking.FailRetirement)
                tracking.Owner.RecordFailure(
                    completed.Exception!.GetBaseException());
            else
                _ = completed.Exception;
        }

        tracking.Lease?.Dispose();
        tracking.LateWorkCompletion?.TrySetResult();
    }

    /// <summary>Records a cleanup or callback failure with the retirement barrier.</summary>
    internal void RecordFailure(Exception failure)
    {
        lock (Sync)
            RecordFailureNoLock(failure);
    }

    private void RecordFailureNoLock(Exception failure) =>
        (_detachedFailures ??= new ImageRetirementFailures()).Add(failure);

    /// <summary>
    /// Runs one cleanup action. Its failure is retained by the retirement
    /// barrier instead of skipping the remaining cleanup or replacing the
    /// operation's primary failure.
    /// </summary>
    internal void TryCleanup(Action cleanup)
    {
        try
        {
            cleanup();
        }
        catch (Exception exception)
        {
            RecordFailure(exception);
        }
    }

    /// <summary>
    /// Marks the box disposed and creates its retirement barrier; false when
    /// it was already disposed.
    /// </summary>
    internal bool TryBeginDispose(out TaskCompletionSource? retirementCompletion)
    {
        lock (Sync)
        {
            if (_disposed)
            {
                retirementCompletion = null;
                return false;
            }

            _disposed = true;
            retirementCompletion = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _retirementCompletion = retirementCompletion;
            return true;
        }
    }

    /// <summary>Starts canceling the lifetime token; a synchronous failure becomes the task's fault.</summary>
    internal Task CancelLifetimeAsync()
    {
        try
        {
            return LifetimeCancellation.CancelAsync();
        }
        catch (Exception exception)
        {
            return Task.FromException(exception);
        }
    }

    /// <summary>
    /// Waits for <paramref name="cancellation"/> and every admitted operation,
    /// releases the box's resources through the host, disposes both
    /// cancellation sources, waits for late work, and completes
    /// <paramref name="completion"/> with any recorded failures.
    /// </summary>
    internal async Task RetireAsync(
        Task cancellation,
        TaskCompletionSource completion)
    {
        var failures = new ImageRetirementFailures();
        try
        {
            // A ready image has no outstanding cancellation/work to await.
            // Still leave the caller (including the UI thread) before native
            // cleanup; omitting ContinueOnCapturedContext keeps that work on
            // the pool even when the cancellation task is already complete.
            await cancellation.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }

        await WaitForWorkDrainAsync(failures).ConfigureAwait(false);

        try
        {
            _host.ReleaseRetiredResources(failures.Add);
        }
        finally
        {
            DisposeCancellationSources(failures);
        }

        await CompleteAfterLateWorkAsync(completion, failures).ConfigureAwait(false);
    }

    // Marks finalizing once no admitted work remains, so new detached work
    // becomes a late barrier instead of reopening the drain.
    private async Task WaitForWorkDrainAsync(ImageRetirementFailures failures)
    {
        while (true)
        {
            Task? activeWork = null;
            lock (Sync)
            {
                if (_workCount == 0)
                {
                    _finalizing = true;
                }
                else
                {
                    activeWork = _workDrained?.Task ?? Task.CompletedTask;
                }
            }

            if (activeWork is null)
                break;

            try
            {
                await activeWork.ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }
        }
    }

    private void DisposeCancellationSources(ImageRetirementFailures failures)
    {
        try { _residencyCancellation?.Dispose(); } catch (Exception exception) { failures.Add(exception); }
        try { LifetimeCancellation.Dispose(); } catch (Exception exception) { failures.Add(exception); }
        lock (Sync)
            _cancellationDisposed = true;
    }

    private async Task CompleteAfterLateWorkAsync(
        TaskCompletionSource completion,
        ImageRetirementFailures failures)
    {
        while (true)
        {
            Task[] lateWork;
            lock (Sync)
            {
                if (_lateWork is null || _lateWork.Count == 0)
                {
                    _host.ClearRetiredStateNoLock();
                    _completed = true;
                    if (_detachedFailures is { } detachedFailures)
                        failures.MergeFrom(detachedFailures);
                    failures.Complete(completion);
                    return;
                }

                lateWork = _lateWork.ToArray();
                _lateWork.Clear();
            }

            // These entries are task-only completion signals. Their continuations
            // record cleanup failures before signaling, avoiding an ordering race
            // between observation and retirement completion.
            await Task.WhenAll(lateWork).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Closes admission for residency. Returns false when the box is disposed,
    /// already suspended, or offscreen release is off. Otherwise returns the
    /// residency source to cancel, if any, and a work unit that keeps the
    /// drain open until that cancellation finishes.
    /// </summary>
    internal bool TrySuspend(
        out CancellationTokenSource? cancellation,
        out ImageWorkLease? cancellationWork)
    {
        cancellation = null;
        cancellationWork = null;
        lock (Sync)
        {
            if (_disposed || _suspended ||
                _context.PerformanceSession?.Options.ReleaseOffscreenImageResources != true)
                return false;

            cancellation = _residencyCancellation;
            if (cancellation is not null)
            {
                cancellationWork = new ImageWorkLease(this, CancellationToken.None);
                if (_workCount == 0)
                    _workDrained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _workCount++;
            }
            _suspended = true;
            return true;
        }
    }

    /// <summary>Records that suspended resources were released.</summary>
    internal void MarkResidencyReleasedNoLock() => _residencyReleased = true;

    /// <summary>Disposes and forgets the residency cancellation source.</summary>
    internal void DisposeResidencyCancellationNoLock()
    {
        _residencyCancellation?.Dispose();
        _residencyCancellation = null;
    }

    /// <summary>
    /// Starts a new residency: a fresh source linked to the lifetime token
    /// replaces the old one, and the released mark is cleared.
    /// </summary>
    internal void ReplaceResidencyCancellationNoLock()
    {
        var replacement = CancellationTokenSource.CreateLinkedTokenSource(LifetimeCancellation.Token);
        _residencyCancellation?.Dispose();
        _residencyCancellation = replacement;
        _residencyReleased = false;
    }

    /// <summary>Reopens admission after a residency resumed.</summary>
    internal void EndSuspensionNoLock() => _suspended = false;
}
