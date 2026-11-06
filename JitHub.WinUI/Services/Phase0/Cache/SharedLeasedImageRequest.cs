using System;
using System.Threading;
using System.Threading.Tasks;
using JitHub.Markdown.Images;
using JitHub.WinUI.Helpers;
using MarkdownRenderer.Performance;

namespace JitHub.Services;

/// <summary>
/// One shared, reference-counted leased image fetch. The producer's root lease is
/// retained once per waiter and released when the last waiter retires the flight.
/// </summary>
internal sealed class SharedLeasedImageRequest
{
    private readonly object _gate = new();
    private readonly object _cancellationGate = new();
    private readonly CancellationTokenSource _cancellationTokenSource = new();
    private readonly TaskCompletionSource<bool> _completion = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _retirement = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Func<CancellationToken, Task<GitHubImageSourceLease?>> _taskFactory;
    private GitHubImageSourceLease? _root;
    private bool _started;
    private int _waiterCount;
    private int _disposeScheduled;
    private bool _retired;
    private bool _completed;
    private bool _cancelStarted;
    private bool _cancellationSealed;
    private Task _cancellationCallbacks = System.Threading.Tasks.Task.CompletedTask;

    internal SharedLeasedImageRequest(
        Func<CancellationToken, Task<GitHubImageSourceLease?>> taskFactory)
    {
        _taskFactory = taskFactory;
    }

    internal Task<bool> Task => _completion.Task;

    internal void Start(IMarkdownImageSourceWorkTracker? workTracker)
    {
        lock (_gate)
        {
            if (_started)
                return;
            _started = true;
        }

        try
        {
            // Register the source-free retirement signal before starting the
            // shared producer. If the resolver scope has closed, fail the
            // flight without allowing untracked host work to begin.
            workTracker?.TrackWork(_retirement.Task);
        }
        catch (Exception exception)
        {
            lock (_gate)
                _completed = true;
            _completion.TrySetException(exception);
            return;
        }

        lock (_gate)
        {
            // Every waiter can cancel while TrackWork is registering. Do not
            // start a producer after the flight retired in that interval.
            if (_retired || _waiterCount == 0)
            {
                _completed = true;
                _completion.TrySetResult(false);
                return;
            }
        }

        _ = RunAsync();
    }

    internal bool TryAddWaiter()
    {
        lock (_gate)
        {
            if (_retired)
                return false;
            _waiterCount++;
            return true;
        }
    }

    internal SharedImageRequestRelease ReleaseWaiter(bool taskCompleted)
    {
        lock (_gate)
        {
            if (_waiterCount <= 0)
                throw new InvalidOperationException("Shared leased image waiter count is already zero.");

            _waiterCount--;
            if (_waiterCount > 0)
                return new SharedImageRequestRelease(ShouldRetire: false, ShouldCancel: false);

            _retired = true;
            return new SharedImageRequestRelease(
                ShouldRetire: true,
                ShouldCancel: !taskCompleted);
        }
    }

    internal GitHubImageSourceLease? RetainResult()
    {
        lock (_gate)
        {
            if (!_completed || _retired)
                throw new InvalidOperationException("The shared leased image result is no longer available.");
            return _root?.Retain();
        }
    }

    internal void Cancel()
    {
        lock (_cancellationGate)
        {
            if (_cancellationSealed || _cancelStarted)
                return;

            _cancelStarted = true;
            try
            {
                // CancelAsync marks the token canceled immediately, but does not
                // run arbitrary registrations inline on the waiter/disposal path.
                // Retirement awaits the returned task before disposing the CTS.
                _cancellationCallbacks = _cancellationTokenSource.CancelAsync();
            }
            catch (ObjectDisposedException)
            {
                _cancellationCallbacks = System.Threading.Tasks.Task.CompletedTask;
            }
            catch (Exception exception)
            {
                // Keep the failure observable by the retirement path while
                // guaranteeing that cancellation cannot skip cleanup.
                _cancellationCallbacks = System.Threading.Tasks.Task.FromException(exception);
            }
        }
    }

    internal void DisposeWhenComplete()
    {
        if (Interlocked.Exchange(ref _disposeScheduled, 1) != 0)
            return;

        _ = DisposeRequestWhenCompleteAsync();
    }

    private async Task RunAsync()
    {
        GitHubImageSourceLease? root = null;
        try
        {
            root = await _taskFactory(_cancellationTokenSource.Token).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            lock (_gate)
                _completed = true;
            _completion.TrySetException(exception);
            return;
        }

        bool publishImage;
        GitHubImageSourceLease? discard = null;
        lock (_gate)
        {
            _completed = true;
            if (root is not null && (_retired || _waiterCount == 0))
            {
                discard = root;
                root = null;
            }
            else
            {
                _root = root;
            }

            publishImage = _root is not null;
        }

        DisposeRootSafely(discard);
        _completion.TrySetResult(publishImage);
    }

    private async Task DisposeRequestWhenCompleteAsync()
    {
        try
        {
            try { await _completion.Task.ConfigureAwait(false); }
            catch { /* The awaiting consumers own user-visible failures. */ }

            Task callbacks;
            lock (_cancellationGate)
            {
                _cancellationSealed = true;
                callbacks = _cancellationCallbacks;
            }

            try
            {
                await callbacks.ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                HandledFailureReporter.Report(exception, "github-image-flight-cancellation-callback");
            }
            finally
            {
                DisposeRootAndCancellation();
            }
        }
        catch (Exception exception)
        {
            HandledFailureReporter.Report(exception, "github-image-flight-retirement");
        }
        finally
        {
            // The tracker receives only this source-free signal. Complete it
            // after callback observation, root clearing/disposal, and CTS teardown.
            _retirement.TrySetResult();
        }
    }

    private void DisposeRootAndCancellation()
    {
        GitHubImageSourceLease? root;
        lock (_gate)
        {
            root = _root;
            _root = null;
        }

        DisposeRootSafely(root);
        _cancellationTokenSource.Dispose();
    }

    private static void DisposeRootSafely(GitHubImageSourceLease? root)
    {
        try
        {
            root?.Dispose();
        }
        catch (Exception exception)
        {
            HandledFailureReporter.Report(exception, "github-image-flight-lease-release");
        }
    }
}
