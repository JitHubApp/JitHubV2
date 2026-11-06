using System;
using System.Threading;

namespace MarkdownRenderer.Layout;

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

    /// <summary>
    /// The native owner a single-slot record holds in its in-record custody
    /// slot; null when that slot is empty or the record uses slot groups.
    /// </summary>
    internal IDisposable? SingleSlotResource => _singleResource;

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
