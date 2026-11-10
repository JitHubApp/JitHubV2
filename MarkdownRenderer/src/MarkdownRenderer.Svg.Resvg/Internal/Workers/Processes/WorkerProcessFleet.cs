namespace MarkdownRenderer.Svg.Resvg.Internal.Workers.Processes;

/// <summary>
/// Owns the pool's worker processes as a group: the kill-on-close job they
/// run in, the lifetime registry that keeps every started or retired worker
/// until it drains, and the shutdown barrier that completes once they have.
/// </summary>
internal sealed class WorkerProcessFleet
{
    private readonly IWorkerProcessLauncher _launcher;
    private readonly Lazy<WorkerJob> _job;
    private readonly TaskCompletionSource _shutdown = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly WorkerLifetimeRegistry<WindowsWorkerProcess> _workerLifetimes = new();

    internal WorkerProcessFleet(
        IWorkerProcessLauncher launcher,
        ResvgMarkdownSvgRendererOptions options,
        bool supportsSecondaryHardware)
    {
        _launcher = launcher;
        _job = new Lazy<WorkerJob>(
            () => WorkerProcessSandbox.CreateConstrainedJob(
                options.WorkerCommitBytes,
                supportsSecondaryHardware ? 2 : 1),
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>Completes after shutdown once every worker owner has drained.</summary>
    internal Task Shutdown => _shutdown.Task;

    /// <summary>Reserves lifetime ownership for a worker that is about to start.</summary>
    /// <returns>The reservation the started worker attaches to.</returns>
    internal WorkerLifetimeRegistry<WindowsWorkerProcess>.Reservation ReserveStartup() => _workerLifetimes.Reserve();

    /// <summary>Starts one worker in the pool's job.</summary>
    /// <param name="cancellationToken">Cancels startup when the pool shuts down.</param>
    /// <returns>The started worker.</returns>
    internal Task<WindowsWorkerProcess> StartAsync(CancellationToken cancellationToken) =>
        _launcher.StartAsync(_job.Value, cancellationToken);

    internal void Retire(WindowsWorkerProcess? worker)
    {
        if (worker is null)
            return;
        try { worker.Dispose(); }
        finally { _workerLifetimes.Retire(worker); }
    }

    internal void RetireForRestart(WindowsWorkerProcess? worker)
    {
        if (worker is null)
            return;
        try { worker.DisposeForRestart(); }
        finally { _workerLifetimes.Retire(worker); }
    }

    /// <summary>
    /// Disposes the workers the slots held and the lifetime registry, then
    /// completes <see cref="Shutdown"/> in the background once they drain.
    /// </summary>
    /// <param name="primary">The primary slot's worker, if any.</param>
    /// <param name="secondary">The secondary slot's worker, if any.</param>
    /// <param name="failures">Collects cleanup failures; shared with the background completion.</param>
    internal void ShutDown(WindowsWorkerProcess? primary, WindowsWorkerProcess? secondary, ref List<Exception>? failures)
    {
        try { primary?.Dispose(); }
        catch (Exception exception) { (failures ??= []).Add(exception); }
        if (!ReferenceEquals(primary, secondary))
        {
            try { secondary?.Dispose(); }
            catch (Exception exception) { (failures ??= []).Add(exception); }
        }
        try { _workerLifetimes.Dispose(); }
        catch (Exception exception) { (failures ??= []).Add(exception); }
        _ = CompleteShutdownAsync(primary, secondary, failures);
    }

    private async Task CompleteShutdownAsync(WindowsWorkerProcess? primary,
        WindowsWorkerProcess? secondary, List<Exception>? failures)
    {
        try
        {
            if (primary is not null)
            {
                try { await primary.DisposeAsync().ConfigureAwait(false); }
                catch (Exception exception) { (failures ??= []).Add(exception); }
            }
            if (secondary is not null && !ReferenceEquals(primary, secondary))
            {
                try { await secondary.DisposeAsync().ConfigureAwait(false); }
                catch (Exception exception) { (failures ??= []).Add(exception); }
            }
            try { await _workerLifetimes.DisposeAsync().ConfigureAwait(false); }
            catch (Exception exception) { (failures ??= []).Add(exception); }
            // A startup admitted before shutdown can still be initializing
            // the lazy job. Keep it alive until every startup/retirement owner
            // drains, then close it exactly once; none can create it afterward.
            try
            {
                if (_job.IsValueCreated)
                    _job.Value.Dispose();
            }
            catch (Exception exception) { (failures ??= []).Add(exception); }
            if (failures is null)
                _shutdown.TrySetResult();
            else
                _shutdown.TrySetException(failures);
        }
        finally
        {
            primary = null;
            secondary = null;
        }
    }
}
