using System;
using System.Threading;

namespace MarkdownRenderer.Layout;

/// <summary>
/// Durable custody of native slots after their layout owner retires. A record
/// and its intrusive link are allocated before native creation; terminal
/// handoff never allocates a queue node or retains a document/exception graph.
/// This bounds retry service work, not aggregate native memory or backlog.
/// </summary>
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

    // Initialize the sole recovery clock before any participating factory.
    // It must not first allocate a timer on an already-failing cleanup path.
    internal static NativeResourceRetirement Shared { get; } = new(createTimer: true);

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

/// <summary>Source-free native custody reserved before construction begins.</summary>
internal sealed class NativeResourceRetirementRecord
{
    private static readonly Action<IDisposable> s_directDisposer = static resource => resource.Dispose();
    private readonly NativeResourceRetirement _owner;
    private readonly IDisposable?[]?[] _groups;
    private readonly int[] _lengths;
    private readonly int[] _remainingByGroup;
    private readonly bool[] _retryable;
    private readonly bool _singleSlotOnly;
    private bool _singleRetryable = true;
    private IDisposable? _singleResource;
    private Action<IDisposable>? _disposer;
    private VectorNativeResourceCredit? _admissionCredit;
    private int _state;
    private int _groupCursor;
    private int _slotCursor;

    internal NativeResourceRetirementRecord(
        NativeResourceRetirement owner,
        int resourceGroupCount,
        int temporarySlotCount,
        Action<IDisposable>? disposer)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(resourceGroupCount);
        ArgumentOutOfRangeException.ThrowIfNegative(temporarySlotCount);
        _owner = owner;
        _singleSlotOnly = resourceGroupCount == 0 && temporarySlotCount == 1;
        if (_singleSlotOnly)
        {
            // The hot text-layout/format path needs one native owner, not five
            // small arrays. Keep its pre-reserved custody in the record itself.
            _groups = Array.Empty<IDisposable?[]?>();
            _lengths = Array.Empty<int>();
            _remainingByGroup = Array.Empty<int>();
            _retryable = Array.Empty<bool>();
        }
        else
        {
            _groups = new IDisposable?[]?[checked(resourceGroupCount + 1)];
            _lengths = new int[_groups.Length];
            _remainingByGroup = new int[_groups.Length];
            _retryable = new bool[_groups.Length];
            Array.Fill(_retryable, true);
            _groups[^1] = new IDisposable?[temporarySlotCount];
        }
        _disposer = disposer ?? s_directDisposer;
    }

    internal NativeResourceRetirementRecord? Next;
    internal long RemainingResourceCount { get; private set; }
    internal long RetryableResourceCount { get; private set; }
    internal long FailedAttemptCount { get; private set; }
    internal int LastFailureHResult { get; private set; }
    internal Action<IDisposable> Disposer => _disposer ?? s_directDisposer;

    // Arrays are covariant only for custody/clearing. Never store another
    // native type into a group borrowed from a concrete Win2D resource array.
    internal void SetGroup(int index, IDisposable?[]? resources, bool retryable = true)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _state) != 0, this);
        if (_singleSlotOnly)
            throw new InvalidOperationException("A temporary-only retirement record has no resource groups.");
        _groups[index] = resources;
        _retryable[index] = retryable;
    }
    internal void SetTemporary(int index, IDisposable? resource)
        => SetTemporary(index, resource, retryable: true);

    internal void SetTemporary(int index, IDisposable? resource, bool retryable)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _state) != 0, this);
        if (_singleSlotOnly)
        {
            if (index != 0)
                throw new ArgumentOutOfRangeException(nameof(index));
            _singleResource = resource;
            _singleRetryable = retryable;
            return;
        }
        if (!retryable)
            throw new InvalidOperationException("Session-bound temporary custody requires a single-slot record.");
        _groups[^1]![index] = resource;
    }

    internal void SetAdmissionCredit(VectorNativeResourceCredit? credit)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _state) != 0, this);
        if (_admissionCredit is not null)
            throw new InvalidOperationException("A retirement record already owns vector admission credit.");
        _admissionCredit = credit;
    }

    /// <summary>
    /// Preserves a single native owner whose post-close state is unknown.
    /// Only its exclusive initial/retry disposer may make this transition.
    /// No further disposal or native property access is permitted afterward.
    /// </summary>
    internal void StopSingleSlotRetries()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _state) == 2, this);
        if (!_singleSlotOnly)
            throw new InvalidOperationException("Only a single-slot native owner can stop retries.");
        _singleRetryable = false;
        if (Volatile.Read(ref _state) == 1)
            RetryableResourceCount = 0;
    }

    internal void Publish() => _owner.Publish(this);
    internal bool TryMarkPublished() => Interlocked.CompareExchange(ref _state, 1, 0) == 0;

    internal long Compact()
    {
        if (_singleSlotOnly)
        {
            RemainingResourceCount = _singleResource is null ? 0 : 1;
            RetryableResourceCount = _singleRetryable ? RemainingResourceCount : 0;
            return RemainingResourceCount;
        }
        long resources = 0;
        long retryableResources = 0;
        for (int group = 0; group < _groups.Length; group++)
        {
            if (_groups[group] is not { } slots)
                continue;
            int retained = 0;
            for (int index = 0; index < slots.Length; index++)
            {
                if (slots[index] is not { } resource)
                    continue;
                if (retained != index)
                {
                    slots[retained] = resource;
                    slots[index] = null;
                }
                retained++;
            }
            _lengths[group] = retained;
            _remainingByGroup[group] = retained;
            resources += retained;
            if (_retryable[group])
                retryableResources += retained;
            if (retained == 0)
                _groups[group] = null;
        }
        RetryableResourceCount = retryableResources;
        return RemainingResourceCount = resources;
    }

    internal int Drain(int maximumSlots)
    {
        if (_singleSlotOnly)
        {
            if (!_singleRetryable || maximumSlots <= 0 || _singleResource is not { } resource)
                return 0;
            try
            {
                Disposer(resource);
                _singleResource = null;
                RemainingResourceCount = RetryableResourceCount = 0;
            }
            catch (Exception failure)
            {
                FailedAttemptCount++;
                LastFailureHResult = failure.HResult;
            }
            return 1;
        }
        int visited = 0;
        int groupsVisited = 0;
        while (visited < maximumSlots && RetryableResourceCount > 0 && groupsVisited < _groups.Length)
        {
            IDisposable?[]? slots = _retryable[_groupCursor] ? _groups[_groupCursor] : null;
            int length = _retryable[_groupCursor] ? _lengths[_groupCursor] : 0;
            while (slots is not null && _slotCursor < length && visited < maximumSlots)
            {
                int index = _slotCursor++;
                visited++;
                if (slots[index] is not { } resource)
                    continue;
                try
                {
                    Disposer(resource);
                    slots[index] = null;
                    RemainingResourceCount--;
                    RetryableResourceCount--;
                    if (--_remainingByGroup[_groupCursor] == 0)
                        _groups[_groupCursor] = null;
                }
                catch (Exception failure)
                {
                    // Preserve custody but no exception/message/closure from
                    // the failed native operation. Only scalar diagnostics stay.
                    FailedAttemptCount++;
                    LastFailureHResult = failure.HResult;
                }
            }
            if (_slotCursor < length)
                break;
            _slotCursor = 0;
            _groupCursor = (_groupCursor + 1) % _groups.Length;
            groupsVisited++;
        }
        return visited;
    }

    internal void Complete()
    {
        if (Interlocked.Exchange(ref _state, 2) == 2)
            return;
        _singleResource = null;
        Array.Clear(_groups);
        Array.Clear(_lengths);
        Array.Clear(_remainingByGroup);
        _disposer = null;
        Next = null;
        VectorNativeResourceCredit? credit = _admissionCredit;
        _admissionCredit = null;
        credit?.Dispose();
    }

    internal void ClearFaultPlan() => _disposer = null;
}
