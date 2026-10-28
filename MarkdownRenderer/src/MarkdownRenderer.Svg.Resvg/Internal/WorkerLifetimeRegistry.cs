namespace MarkdownRenderer.Svg.Resvg.Internal;

/// <summary>
/// Owns workers from before startup until their asynchronous retirement finishes.
/// Slot membership is scheduling state, not proof that native ownership ended.
/// </summary>
internal sealed class WorkerLifetimeRegistry<T> : IDisposable, IAsyncDisposable
    where T : class, IDisposable, IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly HashSet<Reservation> _owners = [];
    private readonly Dictionary<T, Reservation> _workers = new(ReferenceEqualityComparer.Instance);
    private readonly TaskCompletionSource _shutdown = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Exception? _failure;
    private bool _disposed;

    internal WorkerLifetimeRegistry()
    {
        // Synchronous disposal is supported, but asynchronous callers still
        // receive the original cleanup failure through the shared barrier.
        ObserveFailure(_shutdown.Task);
    }

    internal Reservation Reserve()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var owner = new Reservation(this);
            _owners.Add(owner);
            return owner;
        }
    }

    internal void Retire(T worker)
    {
        Reservation? owner;
        lock (_gate)
        {
            if (!_workers.TryGetValue(worker, out owner) || owner.Retiring)
                return;
            owner.Retiring = true;
        }
        _ = RetireAsync(owner, worker);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            CompleteShutdownLocked();
        }
        // Avoid allocating a shutdown snapshot under memory pressure. Publish
        // each retirement under the lock, then run cancellation/cleanup outside
        // it. Startups attaching after closure retire themselves.
        while (true)
        {
            Reservation? ready = null;
            T? worker = null;
            lock (_gate)
            {
                foreach (Reservation owner in _owners)
                {
                    if (owner.Worker is not null && !owner.Retiring)
                    {
                        ready = owner;
                        worker = owner.Worker;
                        owner.Retiring = true;
                        break;
                    }
                }
            }
            if (ready is null)
                return;
            _ = RetireAsync(ready, worker!);
        }
    }

    public async ValueTask DisposeAsync()
    {
        Dispose();
        await _shutdown.Task.ConfigureAwait(false);
    }

    private void Attach(Reservation owner, T worker)
    {
        bool retire;
        lock (_gate)
        {
            if (!_owners.Contains(owner) || owner.Worker is not null)
                throw new InvalidOperationException("The SVG worker startup reservation is no longer available.");
            _workers.Add(worker, owner);
            owner.Worker = worker;
            retire = owner.Retiring = _disposed;
        }
        if (retire)
            _ = RetireAsync(owner, worker);
    }

    private void AbandonStartup(Reservation owner)
    {
        lock (_gate)
        {
            if (owner.Worker is not null)
                return;
            _owners.Remove(owner);
            CompleteShutdownLocked();
        }
    }

    private async Task RetireAsync(Reservation owner, T worker)
    {
        Exception? failure = null;
        try { await worker.DisposeAsync().ConfigureAwait(false); }
        catch (Exception exception) { failure = exception; }
        finally
        {
            lock (_gate)
            {
                _workers.Remove(worker);
                _owners.Remove(owner);
                owner.Worker = null;
                // Keep one failure, not an unbounded history during a crash
                // storm. Any failure prevents certifying successful shutdown.
                _failure ??= failure;
                CompleteShutdownLocked();
            }
        }
    }

    private void CompleteShutdownLocked()
    {
        if (!_disposed || _owners.Count != 0)
            return;
        if (_failure is null)
            _shutdown.TrySetResult();
        else
            _shutdown.TrySetException(_failure);
    }

    private static void ObserveFailure(Task task) =>
        _ = task.ContinueWith(static completed => { _ = completed.Exception; },
            CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted |
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

    internal sealed class Reservation(WorkerLifetimeRegistry<T> registry) : IDisposable
    {
        internal T? Worker;
        internal bool Retiring;
        internal void Attach(T worker)
        {
            ArgumentNullException.ThrowIfNull(worker);
            registry.Attach(this, worker);
        }
        public void Dispose() => registry.AbandonStartup(this);
    }
}
