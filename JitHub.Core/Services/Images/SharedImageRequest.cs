using System;
using System.Threading;
using System.Threading.Tasks;
using JitHub.Markdown.Images.Caching;

namespace JitHub.Services;

/// <summary>
/// One shared, reference-counted image fetch. The last waiter to leave retires
/// the flight and cancels it if it has not completed.
/// </summary>
internal sealed class SharedImageRequest
{
    private readonly object _gate = new();
    private readonly CancellationTokenSource _cancellationTokenSource = new();
    private readonly Lazy<Task<GitHubCachedImage?>> _task;
    private int _waiterCount;
    private int _disposeScheduled;
    private bool _retired;

    public SharedImageRequest(Func<CancellationToken, Task<GitHubCachedImage?>> taskFactory)
    {
        _task = new Lazy<Task<GitHubCachedImage?>>(
            () => taskFactory(_cancellationTokenSource.Token),
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public Task<GitHubCachedImage?> Task => _task.Value;

    public bool TryAddWaiter()
    {
        lock (_gate)
        {
            if (_retired)
            {
                return false;
            }

            _waiterCount++;
            return true;
        }
    }

    public SharedImageRequestRelease ReleaseWaiter(bool taskCompleted)
    {
        lock (_gate)
        {
            if (_waiterCount <= 0)
            {
                throw new InvalidOperationException("Shared image request waiter count is already zero.");
            }

            _waiterCount--;
            if (_waiterCount > 0)
            {
                return new SharedImageRequestRelease(ShouldRetire: false, ShouldCancel: false);
            }

            // Retire while holding the same lock used by TryAddWaiter. A late caller can no
            // longer join this transfer between the zero-count decision and cancellation.
            _retired = true;
            return new SharedImageRequestRelease(
                ShouldRetire: true,
                ShouldCancel: !taskCompleted);
        }
    }

    public void Cancel()
    {
        try
        {
            _cancellationTokenSource.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The flight already retired and released its token source; there is
            // nothing left to cancel.
        }
    }

    public void DisposeWhenComplete()
    {
        if (Interlocked.Exchange(ref _disposeScheduled, 1) != 0)
        {
            return;
        }

        _ = DisposeRequestWhenCompleteAsync();
    }

    private async Task DisposeRequestWhenCompleteAsync()
    {
        try
        {
            await Task.ConfigureAwait(false);
        }
        catch
        {
            // Waiters own user-visible failures; disposal only observes completion.
        }
        finally
        {
            _cancellationTokenSource.Dispose();
        }
    }
}
