using System.Runtime.ExceptionServices;

namespace MarkdownRenderer.Svg.Resvg.Internal;

/// <summary>
/// Coordinates render leases with cancellation and the final document cleanup.
/// Cancellation callbacks and the cleanup action always run outside the state lock.
/// </summary>
internal sealed class SvgDocumentLifetime : IDisposable
{
    private readonly object _gate = new();
    private readonly CancellationTokenSource _cancellation = new();
    private readonly TaskCompletionSource _retirement = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Action? _releaseResources;
    private Exception? _cancellationFailure;
    private int _activeRenders;
    private bool _disposeRequested;
    private bool _cancellationCompleted;
    private bool _resourcesReleased;

    public SvgDocumentLifetime(Action releaseResources)
    {
        _releaseResources = releaseResources ?? throw new ArgumentNullException(nameof(releaseResources));
        // A synchronous caller may use Dispose without observing the async
        // barrier. Preserve its failure for shutdown awaiters without an
        // unobserved task exception or a continuation capturing this owner.
        _ = _retirement.Task.ContinueWith(
            static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    internal Task RetirementTask => _retirement.Task;

    public RenderLease? TryEnterRender()
    {
        lock (_gate)
        {
            if (_disposeRequested)
                return null;

            // Reserve the release ticket before publishing its count. If the
            // allocation fails under memory pressure, retirement must not wait
            // forever for a render lease that was never returned to a caller.
            var lease = new RenderLease(this, _cancellation.Token);
            checked { _activeRenders++; }
            return lease;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposeRequested)
                return;

            // This flag also acts as a disposal-operation reference: cleanup cannot
            // finish until Cancel has returned from every registered callback.
            _disposeRequested = true;
        }

        Exception? cancellationFailure = null;
        try
        {
            _cancellation.Cancel();
        }
        catch (Exception exception)
        {
            // CancellationTokenSource.Cancel invokes all callbacks before reporting
            // their failures. Complete the lifetime transition and release resources
            // even when one of those callbacks throws.
            cancellationFailure = exception;
        }

        Action? releaseResources = CompleteCancellation(cancellationFailure);
        Exception? releaseFailure = null;
        if (releaseResources is not null)
        {
            try
            {
                ReleaseResources(releaseResources);
            }
            catch (Exception exception)
            {
                releaseFailure = exception;
            }
        }

        if (cancellationFailure is not null && releaseFailure is not null)
            throw new AggregateException(cancellationFailure, releaseFailure);

        if (releaseFailure is not null)
            ExceptionDispatchInfo.Capture(releaseFailure).Throw();

        if (cancellationFailure is not null)
            ExceptionDispatchInfo.Capture(cancellationFailure).Throw();
    }

    private Action? CompleteCancellation(Exception? failure)
    {
        lock (_gate)
        {
            _cancellationFailure = failure;
            _cancellationCompleted = true;
            return TryClaimResourceRelease();
        }
    }

    private void ExitRender()
    {
        Action? releaseResources;
        lock (_gate)
        {
            _activeRenders--;
            releaseResources = TryClaimResourceRelease();
        }

        if (releaseResources is not null)
            ReleaseResources(releaseResources);
    }

    private Action? TryClaimResourceRelease()
    {
        if (!_disposeRequested || !_cancellationCompleted || _activeRenders != 0 || _resourcesReleased)
            return null;

        _resourcesReleased = true;
        Action? releaseResources = _releaseResources;
        _releaseResources = null;
        return releaseResources;
    }

    private void ReleaseResources(Action releaseResources)
    {
        Exception? releaseFailure = null;
        try
        {
            releaseResources();
        }
        catch (Exception exception)
        {
            releaseFailure = exception;
            throw;
        }
        finally
        {
            try
            {
                _cancellation.Dispose();
            }
            finally
            {
                Exception? failure = _cancellationFailure is null ? releaseFailure :
                    releaseFailure is null ? _cancellationFailure :
                    new AggregateException(_cancellationFailure, releaseFailure);
                _cancellationFailure = null;
                if (failure is null)
                    _retirement.TrySetResult();
                else
                    _retirement.TrySetException(failure);
            }
        }
    }

    internal sealed class RenderLease(SvgDocumentLifetime owner, CancellationToken token) : IDisposable
    {
        private int _disposed;

        public CancellationToken Token { get; } = token;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                owner.ExitRender();
        }
    }
}
