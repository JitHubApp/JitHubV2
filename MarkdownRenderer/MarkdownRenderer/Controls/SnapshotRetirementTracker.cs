using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;

namespace MarkdownRenderer.Controls;

/// <summary>
/// Tracks only currently pending snapshot retirements. Retirement faults are
/// observed immediately and stored in bounded per-drain epochs, so one fault
/// cannot retain every later Task.WhenAll graph for the lifetime of a control.
/// </summary>
internal sealed class SnapshotRetirementTracker
{
    internal const int MaximumRetainedFailureDetails = 32;

    private readonly object _gate = new();
    private readonly Dictionary<Task, long> _pending = new();
    private readonly Dictionary<long, FailureBucket> _failureBuckets = new();
    private readonly Action<Exception>? _failureObserver;
    private long _currentEpoch;
    private long _sequence;

    internal SnapshotRetirementTracker(Action<Exception>? failureObserver = null)
    {
        _failureObserver = failureObserver;
    }

    internal int RetainedFailureDetailCount
    {
        get
        {
            lock (_gate)
            {
                int count = 0;
                foreach (FailureBucket bucket in _failureBuckets.Values)
                    count += bucket.Details.Count;
                return count;
            }
        }
    }

    internal long OmittedFailureCount
    {
        get
        {
            lock (_gate)
            {
                long count = 0;
                foreach (FailureBucket bucket in _failureBuckets.Values)
                    count += bucket.OmittedCount;
                return count;
            }
        }
    }

    internal void Track(Task retirement)
    {
        ArgumentNullException.ThrowIfNull(retirement);

        Task observed;
        lock (_gate)
        {
            long epoch = _currentEpoch;
            long sequence = ++_sequence;
            observed = ObserveAsync(retirement, epoch, sequence);
            if (!observed.IsCompleted)
                _pending.Add(observed, epoch);
        }

        if (observed.IsCompleted)
            return;

        _ = observed.ContinueWith(
            static (completed, state) =>
            {
                var tracker = (SnapshotRetirementTracker)state!;
                lock (tracker._gate)
                    tracker._pending.Remove(completed);
            },
            this,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    internal Task DrainAsync()
    {
        Task pending;
        long epoch;
        lock (_gate)
        {
            epoch = _currentEpoch++;
            List<Task>? epochTasks = null;
            foreach (KeyValuePair<Task, long> entry in _pending)
            {
                if (entry.Value == epoch)
                    (epochTasks ??= new List<Task>()).Add(entry.Key);
            }

            pending = epochTasks switch
            {
                null => Task.CompletedTask,
                { Count: 1 } => epochTasks[0],
                _ => Task.WhenAll(epochTasks),
            };
        }

        return DrainCoreAsync(pending, epoch);
    }

    private async Task ObserveAsync(Task retirement, long epoch, long sequence)
    {
        try
        {
            await retirement.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            lock (_gate)
            {
                if (!_failureBuckets.TryGetValue(epoch, out FailureBucket? bucket))
                {
                    bucket = new FailureBucket();
                    _failureBuckets.Add(epoch, bucket);
                }

                bucket.Add(sequence, exception);
            }

            try { _failureObserver?.Invoke(exception); }
            catch
            {
                // Diagnostics are observational and must not turn the flattened
                // pending set back into a faulted task.
            }
        }
    }

    private async Task DrainCoreAsync(Task pending, long epoch)
    {
        await pending.ConfigureAwait(false);

        FailureBucket? bucket;
        lock (_gate)
        {
            if (!_failureBuckets.Remove(epoch, out bucket))
                return;
        }

        bucket.Details.Sort(static (left, right) => left.Sequence.CompareTo(right.Sequence));
        if (bucket.Details.Count == 1 && bucket.OmittedCount == 0)
            ExceptionDispatchInfo.Capture(bucket.Details[0].Exception).Throw();

        var failures = new List<Exception>(
            bucket.Details.Count + (bucket.OmittedCount == 0 ? 0 : 1));
        foreach (RetirementFailure failure in bucket.Details)
            failures.Add(failure.Exception);
        if (bucket.OmittedCount != 0)
        {
            failures.Add(new InvalidOperationException(
                $"{bucket.OmittedCount} additional snapshot retirement failure(s) " +
                $"were logged but omitted from this bounded drain result."));
        }

        throw new AggregateException(failures);
    }

    private sealed class FailureBucket
    {
        internal List<RetirementFailure> Details { get; } = new(MaximumRetainedFailureDetails);

        internal long OmittedCount { get; private set; }

        internal void Add(long sequence, Exception exception)
        {
            var failure = new RetirementFailure(sequence, exception);
            if (Details.Count < MaximumRetainedFailureDetails)
            {
                Details.Add(failure);
                return;
            }

            int latestIndex = 0;
            for (int index = 1; index < Details.Count; index++)
            {
                if (Details[index].Sequence > Details[latestIndex].Sequence)
                    latestIndex = index;
            }

            if (sequence < Details[latestIndex].Sequence)
                Details[latestIndex] = failure;
            OmittedCount++;
        }
    }

    private readonly record struct RetirementFailure(long Sequence, Exception Exception);
}
