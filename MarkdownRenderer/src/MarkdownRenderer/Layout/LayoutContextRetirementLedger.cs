using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace MarkdownRenderer.Layout;

/// <summary>
/// Native retirement registered by one layout construction context. Each
/// distinct task is recorded once and forwarded to the host sink; the owning
/// pipeline drains every batch outside layout locks and reports a bounded
/// summary of the failures.
/// </summary>
internal sealed class LayoutContextRetirementLedger
{
    private const int MaximumRetirementFailureDetails = 32;

    private readonly object _gate = new();
    private List<Task>? _pendingRetirementTasks;
    private HashSet<Task>? _retirementTaskIdentities;

    internal void Register(Task retirementTask, Action<Task>? sink)
    {
        if (retirementTask.IsCompletedSuccessfully)
            return;

        lock (_gate)
        {
            _retirementTaskIdentities ??= new HashSet<Task>(ReferenceEqualityComparer.Instance);
            if (!_retirementTaskIdentities.Add(retirementTask))
                return;

            (_pendingRetirementTasks ??= new List<Task>()).Add(retirementTask);
        }

        try
        {
            sink?.Invoke(retirementTask);
        }
        catch
        {
            // The pipeline-owned drain below remains authoritative if an
            // optional host observer is unavailable during teardown.
        }
    }

    internal async Task DrainAsync(Exception? primaryFailure)
    {
        List<Exception>? failures = null;
        int failureCount = 0;

        void AddFailure(Exception exception)
        {
            failureCount++;
            if (failureCount <= MaximumRetirementFailureDetails)
                (failures ??= new List<Exception>()).Add(new MarkdownLayoutRetirementFailureSummary(exception));
        }

        while (true)
        {
            List<Task>? batch;
            lock (_gate)
            {
                batch = _pendingRetirementTasks;
                _pendingRetirementTasks = null;
                if (batch is null || batch.Count == 0)
                    _retirementTaskIdentities = null;
            }

            if (batch is null || batch.Count == 0)
                break;

            try
            {
                await Task.WhenAll(batch).ConfigureAwait(false);
            }
            catch
            {
                foreach (Task task in batch)
                {
                    if (task.IsFaulted && task.Exception is { } taskFailure)
                    {
                        foreach (Exception exception in taskFailure.Flatten().InnerExceptions)
                            AddFailure(exception);
                    }
                    else if (task.IsCanceled)
                        AddFailure(new TaskCanceledException(task));
                }
            }
        }

        if (failures is not null)
        {
            if (failureCount > MaximumRetirementFailureDetails)
            {
                failures.Add(new MarkdownLayoutRetirementFailureSummary(
                    failureCount - MaximumRetirementFailureDetails));
            }

            throw new MarkdownLayoutRetirementException(
                new AggregateException("Failed layout resources did not retire cleanly.", failures),
                primaryFailure);
        }
    }
}
