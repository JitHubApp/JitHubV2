using System;
using System.Threading;

namespace MarkdownRenderer.Layout;

/// <summary>
/// Process-wide weighted admission for prepared native vector resources. The
/// units are conservative complexity credits, not measured native bytes, RSS,
/// or a promise about a graphics driver’s allocation behavior.
/// </summary>
internal sealed class VectorNativeResourceAdmission
{
    internal const long X64OrArm64CapacityWeightUnits = 64L * 1024 * 1024;
    internal const long X86CapacityWeightUnits = 32L * 1024 * 1024;
    private const int ObserverRegistrationsBetweenCompactions = 256;
    private const int ObserverTombstonesBeforeCompaction = 64;

    private static readonly Lazy<VectorNativeResourceAdmission> s_shared = new(
        static () => new VectorNativeResourceAdmission(
            IntPtr.Size == 8 ? X64OrArm64CapacityWeightUnits : X86CapacityWeightUnits,
            IntPtr.Size == 8 ? X64OrArm64CapacityWeightUnits : X86CapacityWeightUnits),
        LazyThreadSafetyMode.ExecutionAndPublication);

    private readonly object _gate = new();
    private readonly long _capacityWeightUnits;
    private readonly long _maximumSceneWeightUnits;
    private CapacityObserverNode? _observerHead;
    private int _observerRegistrationsSinceCompaction;
    private int _observerTombstoneCount;
    private long _reservedWeightUnits;
    private long _capacityGeneration;

    internal static VectorNativeResourceAdmission Shared => s_shared.Value;

    /// <summary>
    /// Creates an isolated admission ledger. Limits may only lower the
    /// architecture default; tests use smaller values to exercise contention.
    /// </summary>
    internal VectorNativeResourceAdmission(long capacityWeightUnits, long maximumSceneWeightUnits)
    {
        long architectureLimit = IntPtr.Size == 8
            ? X64OrArm64CapacityWeightUnits
            : X86CapacityWeightUnits;
        if (capacityWeightUnits <= 0 || capacityWeightUnits > architectureLimit)
            throw new ArgumentOutOfRangeException(nameof(capacityWeightUnits));
        if (maximumSceneWeightUnits <= 0 || maximumSceneWeightUnits > capacityWeightUnits)
            throw new ArgumentOutOfRangeException(nameof(maximumSceneWeightUnits));

        _capacityWeightUnits = capacityWeightUnits;
        _maximumSceneWeightUnits = maximumSceneWeightUnits;
    }

    internal long CapacityWeightUnits => _capacityWeightUnits;
    internal long MaximumSceneWeightUnits => _maximumSceneWeightUnits;
    internal long ReservedWeightUnits
    {
        get { lock (_gate) return _reservedWeightUnits; }
    }
    internal long CapacityGeneration => Interlocked.Read(ref _capacityGeneration);

    internal VectorNativeAdmissionResult TryReserve(
        long weightUnits,
        out VectorNativeResourceCredit? credit,
        out long observedGeneration)
    {
        if (weightUnits <= 0)
            throw new ArgumentOutOfRangeException(nameof(weightUnits));

        lock (_gate)
        {
            observedGeneration = _capacityGeneration;
            credit = null;
            if (weightUnits > _maximumSceneWeightUnits)
                return VectorNativeAdmissionResult.TooLarge;

            // Keep the invariant by subtraction instead of adding untrusted
            // weight to the current reservation where overflow could wrap.
            if (weightUnits > _capacityWeightUnits - _reservedWeightUnits)
                return VectorNativeAdmissionResult.DeferredCapacity;

            // Allocate the lease before mutating accounting so allocation
            // failure cannot strand aggregate capacity without an owner.
            credit = new VectorNativeResourceCredit(this, weightUnits);
            _reservedWeightUnits += weightUnits;
            return VectorNativeAdmissionResult.Granted;
        }
    }

    internal IDisposable RegisterCapacityObserver(IVectorNativeCapacityObserver observer)
    {
        ArgumentNullException.ThrowIfNull(observer);
        lock (_gate)
        {
            if (_observerRegistrationsSinceCompaction >= ObserverRegistrationsBetweenCompactions ||
                Volatile.Read(ref _observerTombstoneCount) >= ObserverTombstonesBeforeCompaction)
            {
                CompactObserversNoLock();
            }

            var registration = new ObserverRegistration(observer);
            var node = new CapacityObserverNode(registration, _observerHead);
            var subscription = new ObserverSubscription(this, registration);
            _observerHead = node;
            _observerRegistrationsSinceCompaction++;
            return subscription;
        }
    }

    internal void Refund(long weightUnits)
    {
        CapacityObserverNode? observerHead;
        long generation;
        lock (_gate)
        {
            // A credit is single-owner/idempotent. A violated invariant must
            // fail closed rather than underflow and admit beyond capacity.
            if (weightUnits <= 0 || weightUnits > _reservedWeightUnits)
                throw new InvalidOperationException("Vector native admission credit accounting was corrupted.");

            _reservedWeightUnits -= weightUnits;
            generation = _capacityGeneration == long.MaxValue
                ? long.MaxValue
                : _capacityGeneration + 1;
            Interlocked.Exchange(ref _capacityGeneration, generation);
            observerHead = _observerHead;
        }

        // The captured chain is immutable, so registrations/unregistrations
        // cannot sever this traversal. It contains only weak observer handles;
        // resolve and retain one observer only for the duration of its callback.
        // No notification allocation means a stationary waiter cannot lose
        // its sole wake because memory pressure prevented a snapshot copy.
        for (CapacityObserverNode? node = observerHead; node is not null; node = node.Next)
        {
            ObserverRegistration registration = node.Registration;
            if (!registration.IsActive)
                continue;
            if (!registration.Observer.TryGetTarget(out IVectorNativeCapacityObserver? observer))
            {
                if (registration.TryDeactivate())
                    Interlocked.Increment(ref _observerTombstoneCount);
                continue;
            }

            try { observer.OnCapacityAvailable(generation); }
            catch { /* A recovery hint must never invalidate native disposal. */ }
            finally { observer = null; }
        }
    }

    private void Unregister(ObserverRegistration registration)
    {
        if (registration.TryDeactivate())
            Interlocked.Increment(ref _observerTombstoneCount);
    }

    private void CompactObserversNoLock()
    {
        CapacityObserverNode? compactedHead = null;
        for (CapacityObserverNode? node = _observerHead; node is not null; node = node.Next)
        {
            ObserverRegistration registration = node.Registration;
            if (!registration.IsActive)
                continue;
            if (!registration.Observer.TryGetTarget(out _))
            {
                if (registration.TryDeactivate())
                    _observerTombstoneCount++;
                continue;
            }

            compactedHead = new CapacityObserverNode(registration, compactedHead);
        }

        _observerHead = compactedHead;
        _observerRegistrationsSinceCompaction = 0;
        _observerTombstoneCount = 0;
    }

    private sealed class CapacityObserverNode(ObserverRegistration registration, CapacityObserverNode? next)
    {
        internal ObserverRegistration Registration { get; } = registration;
        internal CapacityObserverNode? Next { get; } = next;
    }

    private sealed class ObserverRegistration(IVectorNativeCapacityObserver observer)
    {
        private int _active = 1;
        internal WeakReference<IVectorNativeCapacityObserver> Observer { get; } = new(observer);
        internal bool IsActive => Volatile.Read(ref _active) != 0;
        internal bool TryDeactivate() => Interlocked.Exchange(ref _active, 0) != 0;
    }

    private sealed class ObserverSubscription(
        VectorNativeResourceAdmission owner,
        ObserverRegistration registration) : IDisposable
    {
        private VectorNativeResourceAdmission? _owner = owner;
        private readonly ObserverRegistration _registration = registration;

        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Unregister(_registration);
    }

}

internal sealed class VectorNativeResourceCredit : IDisposable
{
    private VectorNativeResourceAdmission? _owner;
    private readonly long _weightUnits;

    internal VectorNativeResourceCredit(VectorNativeResourceAdmission owner, long weightUnits)
    {
        _owner = owner;
        _weightUnits = weightUnits;
    }

    internal long WeightUnits => _weightUnits;

    public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Refund(_weightUnits);
}

internal enum VectorNativeAdmissionResult
{
    Granted,
    DeferredCapacity,
    TooLarge,
}

internal interface IVectorNativeCapacityObserver
{
    void OnCapacityAvailable(long generation);
}

internal enum VectorNativePreparationStatus
{
    NotPrepared,
    Prepared,
    DeferredCapacity,
    TooLarge,
}

internal readonly struct VectorNativePreparationOutcome(
    VectorNativePreparationStatus Status,
    long WeightUnits,
    long CapacityGeneration)
{
    public VectorNativePreparationStatus Status { get; init; } = Status;
    public long WeightUnits { get; init; } = WeightUnits;
    public long CapacityGeneration { get; init; } = CapacityGeneration;
}
