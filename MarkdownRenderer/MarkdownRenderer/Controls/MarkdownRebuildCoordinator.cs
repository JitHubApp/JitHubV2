using System;
using System.Threading.Tasks;

namespace MarkdownRenderer.Controls;

internal enum MarkdownRebuildCoordinatorReason
{
    // Keep the values ordered by merge precedence; SceneMaterialized is the
    // neutral (lowest-priority) queued reason, then Restyle, then Full.
    SceneMaterialized = 0,
    Restyle = 1,
    Full = 2,
}

internal enum MarkdownRebuildRequestDisposition
{
    Deferred,
    Queue,
    Supersede,
}

/// <summary>
/// Tracks the pending rebuild reason separately from the published pipeline
/// lifetime. Scene materialization can request one coalesced follow-up without
/// invalidating the snapshot currently being built or displayed.
/// </summary>
internal sealed class MarkdownRebuildCoordinator
{
    private readonly object _gate = new();
    private bool _hasPending;
    private MarkdownRebuildCoordinatorReason _pendingReason;
    private long _buildingGeneration;

    internal MarkdownRebuildRequestDisposition Request(MarkdownRebuildCoordinatorReason reason)
    {
        lock (_gate)
        {
            _pendingReason = _hasPending ? Merge(_pendingReason, reason) : reason;
            _hasPending = true;

            if (reason == MarkdownRebuildCoordinatorReason.SceneMaterialized && _buildingGeneration != 0)
                return MarkdownRebuildRequestDisposition.Deferred;

            return reason == MarkdownRebuildCoordinatorReason.SceneMaterialized
                ? MarkdownRebuildRequestDisposition.Queue
                : MarkdownRebuildRequestDisposition.Supersede;
        }
    }

    internal bool TryGetDispatchReason(out MarkdownRebuildCoordinatorReason reason)
    {
        lock (_gate)
        {
            reason = _pendingReason;
            return _hasPending &&
                (_buildingGeneration == 0 || _pendingReason != MarkdownRebuildCoordinatorReason.SceneMaterialized);
        }
    }

    internal bool TryBeginPipeline(long generation, out MarkdownRebuildCoordinatorReason reason)
    {
        if (generation <= 0)
            throw new ArgumentOutOfRangeException(nameof(generation));

        lock (_gate)
        {
            reason = _pendingReason;
            if (!_hasPending ||
                (_buildingGeneration != 0 && _pendingReason == MarkdownRebuildCoordinatorReason.SceneMaterialized))
            {
                return false;
            }

            _hasPending = false;
            _buildingGeneration = generation;
            return true;
        }
    }

    /// <summary>
    /// Marks only the matching build as finished. The caller retains the
    /// published pipeline CTS/generation until an actual supersession.
    /// </summary>
    internal bool CompletePipeline(long generation)
    {
        lock (_gate)
        {
            if (_buildingGeneration != generation)
                return false;

            _buildingGeneration = 0;
            return true;
        }
    }

    internal bool HasPending
    {
        get { lock (_gate) return _hasPending; }
    }

    /// <summary>
    /// Indicates that a replacement is requested or currently being built.
    /// Image reflow should wait while this is true, then resume against the
    /// still-published snapshot if the replacement terminates without commit.
    /// </summary>
    internal bool HasPendingOrBuilding
    {
        get
        {
            lock (_gate)
                return _hasPending || _buildingGeneration != 0;
        }
    }

    internal void Reset()
    {
        lock (_gate)
        {
            _hasPending = false;
            _pendingReason = default;
            _buildingGeneration = 0;
        }
    }

    internal static MarkdownRebuildCoordinatorReason Merge(
        MarkdownRebuildCoordinatorReason left,
        MarkdownRebuildCoordinatorReason right) =>
        (int)left >= (int)right ? left : right;

    /// <summary>
    /// Completes the prepublished pipeline lifetime with the terminal state of
    /// its actual worker. The lifetime is the teardown barrier, so it must not
    /// convert a faulted/canceled worker into successful completion.
    /// </summary>
    internal static void CompletePipelineLifetime(Task completed, TaskCompletionSource lifetime)
    {
        ArgumentNullException.ThrowIfNull(completed);
        ArgumentNullException.ThrowIfNull(lifetime);
        if (!completed.IsCompleted)
            throw new ArgumentException("The rebuild task has not completed.", nameof(completed));

        if (completed.IsFaulted)
        {
            AggregateException? failure = completed.Exception;
            if (failure is not null)
                lifetime.TrySetException(failure.InnerExceptions);
            else
                lifetime.TrySetException(new InvalidOperationException("The rebuild task faulted without an exception."));
        }
        else if (completed.IsCanceled)
        {
            lifetime.TrySetCanceled(new TaskCanceledException(completed).CancellationToken);
        }
        else
        {
            lifetime.TrySetResult();
        }
    }
}
