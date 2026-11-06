using System;
using System.Threading;

namespace MarkdownRenderer.Layout;

/// <summary>
/// Durable custody of native slots after their layout owner retires. A record
/// and its intrusive link are allocated before native creation; terminal
/// handoff never allocates a queue node or retains a document/exception graph.
/// This bounds retry service work, not aggregate native memory or backlog.
/// </summary>
/// <remarks>
/// Each <see cref="MarkdownRenderResources"/> owns one instance with a retry
/// clock. While retryable records are pending the armed clock roots its
/// owner, so their retries continue after the engine that created them is
/// gone; quarantined records live as long as the owner.
/// </remarks>
internal sealed class NativeResourceRetirement
{
    private const int MaximumRecordsPerPass = 8;
    private const int MaximumSlotsPerPass = 128;
    private readonly object _gate = new();
    private readonly Timer? _timer;
    private NativeResourceRetirementRecord? _head;
    private NativeResourceRetirementRecord? _tail;
    private NativeResourceRetirementRecord? _quarantine;
    private int _pendingRecordCount;
    private long _pendingResourceCount;
    private long _quarantinedResourceCount;
    private int _drainActive;
    private int _retryDelayMilliseconds = 100;
    private long _publicationVersion;
    private int _lastClockFailureHResult;

    // Production owners create their recovery clock when they are
    // constructed, before any participating factory, so a timer is never
    // first allocated on an already-failing cleanup path.
    internal NativeResourceRetirement(bool createTimer)
    {
        if (createTimer)
        {
            _timer = new Timer(static owner => ((NativeResourceRetirement)owner!).DrainOnce(),
                this, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }
    }

    internal int PendingRecordCount
    {
        get { lock (_gate) return _pendingRecordCount; }
    }

    internal long PendingResourceCount
    {
        get { lock (_gate) return _pendingResourceCount; }
    }

    internal long QuarantinedResourceCount
    {
        get { lock (_gate) return _quarantinedResourceCount; }
    }

    internal bool AllowsTestDisposer => _timer is null;

    internal NativeResourceRetirementRecord Reserve(
        int resourceGroupCount = 4,
        int temporarySlotCount = 2,
        Action<IDisposable>? disposer = null)
    {
        // Arbitrary callback targets never enter process-owned production
        // custody. Fault plans are confined to an isolated timerless test owner.
        if (disposer is not null && !AllowsTestDisposer)
            throw new ArgumentException("Native disposal fault plans require an isolated retirement owner.", nameof(disposer));
        lock (_gate)
        {
            if (_head is not null && _lastClockFailureHResult != 0)
                ScheduleNoLock();
        }
        return new(this, resourceGroupCount, temporarySlotCount, disposer);
    }

    internal void Publish(NativeResourceRetirementRecord record)
    {
        // Only an exclusive terminal owner may populate this record. Reserve
        // precedes factories; compaction reuses its original arrays and retains
        // only failed native slots, never their former drawing/context.
        if (!record.TryMarkPublished())
            return;
        long resources = record.Compact();
        if (resources == 0)
        {
            record.Complete();
            return;
        }
        lock (_gate)
        {
            _quarantinedResourceCount += resources - record.RetryableResourceCount;
            if (record.RetryableResourceCount == 0)
            {
                QuarantineNoLock(record);
                return;
            }
            AppendNoLock(record);
            _pendingRecordCount++;
            _pendingResourceCount += record.RetryableResourceCount;
            _publicationVersion++;
            _retryDelayMilliseconds = 100;
            ScheduleNoLock();
        }
    }

    /// <summary>
    /// One non-overlapping, background retry quantum. Native Dispose is never
    /// called under the registry gate; reentrant publishers cannot deadlock it.
    /// Tests use an isolated clock-free owner and invoke this deterministically.
    /// </summary>
    internal bool DrainOnce()
    {
        if (Interlocked.CompareExchange(ref _drainActive, 1, 0) != 0)
            return false;
        bool releasedAny = false;
        long version;
        lock (_gate)
            version = _publicationVersion;
        try
        {
            int recordsRemaining;
            lock (_gate)
                recordsRemaining = Math.Min(MaximumRecordsPerPass, _pendingRecordCount);
            int slotsRemaining = MaximumSlotsPerPass;
            while (recordsRemaining-- > 0 && slotsRemaining > 0)
            {
                NativeResourceRetirementRecord? record;
                lock (_gate)
                {
                    record = _head;
                    if (record is null)
                        break;
                    _head = record.Next;
                    record.Next = null;
                    if (_head is null)
                        _tail = null;
                }

                long beforeRetryable = record.RetryableResourceCount;
                long beforeRemaining = record.RemainingResourceCount;
                slotsRemaining -= record.Drain(slotsRemaining);
                long released = beforeRemaining - record.RemainingResourceCount;
                long stoppedRetrying = beforeRetryable - record.RetryableResourceCount;
                releasedAny |= released != 0;
                bool completeRecord = false;
                lock (_gate)
                {
                    _pendingResourceCount -= stoppedRetrying;
                    // An attempted WinRT Close may leave an unknown state.
                    // Such a slot stops retrying without releasing its native
                    // owner or admission credit; distinguish that transition
                    // from successful disposal in both ledgers and outcomes.
                    _quarantinedResourceCount += stoppedRetrying - released;
                    if (record.RetryableResourceCount == 0)
                    {
                        _pendingRecordCount--;
                        if (record.RemainingResourceCount == 0)
                            completeRecord = true;
                        else
                            QuarantineNoLock(record);
                    }
                    else
                    {
                        AppendNoLock(record);
                    }
                }
                // Completing a record refunds its aggregate credit and can
                // notify capacity observers. Keep that callback graph outside
                // the retirement registry gate as well as the ledger gate.
                if (completeRecord)
                    record.Complete();
            }
        }
        finally
        {
            lock (_gate)
            {
                _retryDelayMilliseconds = releasedAny || version != _publicationVersion
                    ? 100
                    : Math.Min(5000, _retryDelayMilliseconds * 2);
                // Release the drain fence before arming, under the same gate
                // that serializes publishers. Even a delayed finalizer cannot
                // consume the only one-shot wake while the fence is still set.
                Volatile.Write(ref _drainActive, 0);
                ScheduleNoLock();
            }
        }
        return releasedAny;
    }

    private void AppendNoLock(NativeResourceRetirementRecord record)
    {
        if (_tail is null)
            _head = record;
        else
            _tail.Next = record;
        _tail = record;
    }

    private void QuarantineNoLock(NativeResourceRetirementRecord record)
    {
        // Session-bound active layers are custody-only. Never invoke their
        // close action on a background clock after the paint session returns.
        record.ClearFaultPlan();
        record.Next = _quarantine;
        _quarantine = record;
    }

    private void ScheduleNoLock()
    {
        if (_timer is null)
            return;
        try
        {
            if (!_timer.Change(_head is null ? Timeout.InfiniteTimeSpan :
                TimeSpan.FromMilliseconds(_retryDelayMilliseconds), Timeout.InfiniteTimeSpan))
                throw new InvalidOperationException("The native resource retirement clock could not be armed.");
            _lastClockFailureHResult = 0;
        }
        catch (Exception failure)
        {
            // The intrusive registry remains the durable root even if runtime
            // timer arming fails. Later reservations retry scheduling before
            // native work; no native slots or exception graph are discarded.
            _lastClockFailureHResult = failure.HResult;
        }
    }
}
