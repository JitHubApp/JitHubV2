using System;
using System.Threading;
using System.Threading.Tasks;

namespace JitHub.Services;

/// <summary>
/// Debounces predictive intent and keeps at most one started prediction alive.
/// The caller owns route lifetime and must call <see cref="Cancel"/> when leaving it.
/// </summary>
public sealed partial class LatestWinsPrefetchScheduler
{
    private readonly object _gate = new();
    private CancellationTokenSource? _pendingCancellation;
    private IDisposable? _activePrefetch;
    private long _generation;

    public void Schedule(TimeSpan debounce, Func<IDisposable?> startPrefetch)
    {
        ArgumentNullException.ThrowIfNull(startPrefetch);
        if (debounce < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(debounce));
        }

        CancellationTokenSource cancellation = new();
        CancellationTokenSource? supersededPending;
        IDisposable? supersededActive;
        long generation;
        lock (_gate)
        {
            supersededPending = _pendingCancellation;
            supersededActive = _activePrefetch;
            _pendingCancellation = cancellation;
            _activePrefetch = null;
            generation = ++_generation;
        }

        CancelSafely(supersededPending);
        DisposeSafely(supersededActive);
        _ = RunAsync(generation, debounce, startPrefetch, cancellation);
    }

    public void Cancel()
    {
        CancellationTokenSource? pending;
        IDisposable? active;
        lock (_gate)
        {
            ++_generation;
            pending = _pendingCancellation;
            active = _activePrefetch;
            _pendingCancellation = null;
            _activePrefetch = null;
        }

        CancelSafely(pending);
        DisposeSafely(active);
    }

    private async Task RunAsync(
        long generation,
        TimeSpan debounce,
        Func<IDisposable?> startPrefetch,
        CancellationTokenSource cancellation)
    {
        IDisposable? started = null;
        try
        {
            await Task.Delay(debounce, cancellation.Token).ConfigureAwait(false);
            cancellation.Token.ThrowIfCancellationRequested();
            started = startPrefetch() ?? EmptyDisposable.Instance;

            lock (_gate)
            {
                if (generation == _generation &&
                    ReferenceEquals(_pendingCancellation, cancellation) &&
                    !cancellation.IsCancellationRequested)
                {
                    _pendingCancellation = null;
                    _activePrefetch = started;
                    started = null;
                }
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch
        {
            // Predictive intent is best-effort and must never surface as a page failure.
        }
        finally
        {
            DisposeSafely(started);
            lock (_gate)
            {
                if (ReferenceEquals(_pendingCancellation, cancellation))
                {
                    _pendingCancellation = null;
                }
            }

            cancellation.Dispose();
        }
    }

    private static void CancelSafely(CancellationTokenSource? cancellation)
    {
        if (cancellation is null)
        {
            return;
        }

        try
        {
            cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private static void DisposeSafely(IDisposable? disposable)
    {
        try
        {
            disposable?.Dispose();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private sealed partial class EmptyDisposable : IDisposable
    {
        public static readonly IDisposable Instance = new EmptyDisposable();

        public void Dispose()
        {
        }
    }
}
