using System;
using System.Collections.Generic;
using System.Threading;

namespace MarkdownRenderer.Layout.Boxes;

internal interface IBitmapOutputWaiter
{
    void OnBitmapOutputCapacityAvailable(long capacityVersion);
}

/// <summary>
/// Process-wide admission for renderer-owned bitmap outputs. The ledger is
/// acquired before native bitmap construction and remains charged until the
/// owner confirms successful disposal. It bounds participating outputs, not
/// process RSS or allocations made by external/native libraries.
/// </summary>
internal sealed class BitmapOutputAdmission
{
    private const long MiB = 1024L * 1024L;
    private const int DefaultMaximumWaiters = 256;
    private const int DefaultVisibleWaiterReserve = 8;
    private static readonly Lazy<BitmapOutputAdmission> s_shared = new(CreateShared);

    private readonly object _sync = new();
    private readonly long _capacityBytes;
    private readonly long _transitionHeadroomBytes;
    private readonly long _visibleInitialLimitBytes;
    private readonly long _speculativeLimitBytes;
    private readonly int _maximumWaiters;
    private readonly int _maximumSpeculativeWaiters;
    private readonly Action<Action>? _testNotificationScheduler;
    private readonly Action<Action>? _testNotificationDeliveryScheduler;
    private readonly List<WaiterEntry> _waiters = new();

    // Charged includes both live reservations and permanently retained
    // uncertain charges. Uncertain is also tracked separately for diagnosis.
    private long _chargedBytes;
    private long _uncertainBytes;
    private int _activeReservationCount;
    private int _uncertainReservationCount;
    private long _capacityVersion;
    // Queue eligibility is intentionally independent of the byte-budget
    // generation: removing a waiter or promoting a speculative waiter can
    // free bounded queue capacity without freeing bitmap bytes.
    private long _waiterSlotVersion;
    private long _nextWaiterId;
    private long _nextWaiterSequence;
    private long _callbackFailureCount;
    private bool _notificationSweepQueued;

    internal static BitmapOutputAdmission Shared => s_shared.Value;

    internal long CapacityBytes => _capacityBytes;
    internal long TransitionHeadroomBytes => _transitionHeadroomBytes;
    internal long MaximumSingleOutputBytes => _transitionHeadroomBytes;
    internal long VisibleInitialLimitBytes => _visibleInitialLimitBytes;
    internal long SpeculativeLimitBytes => _speculativeLimitBytes;

    internal BitmapOutputAdmission(
        long capacityBytes,
        long transitionHeadroomBytes,
        int maximumWaiters = DefaultMaximumWaiters,
        Action<Action>? testNotificationScheduler = null,
        Action<Action>? testNotificationDeliveryScheduler = null)
    {
        if (capacityBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacityBytes));
        if (transitionHeadroomBytes <= 0 || transitionHeadroomBytes >= capacityBytes)
            throw new ArgumentOutOfRangeException(nameof(transitionHeadroomBytes));
        if (capacityBytes <= checked(transitionHeadroomBytes * 2))
            throw new ArgumentOutOfRangeException(
                nameof(capacityBytes),
                "Capacity must leave distinct speculative, visible-initial, and replacement tiers.");
        if (maximumWaiters <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumWaiters));

        _capacityBytes = capacityBytes;
        _transitionHeadroomBytes = transitionHeadroomBytes;
        _visibleInitialLimitBytes = capacityBytes - transitionHeadroomBytes;
        _speculativeLimitBytes = capacityBytes - checked(transitionHeadroomBytes * 2);
        _maximumWaiters = maximumWaiters;
        // Keep a small portion of the existing bounded queue available to
        // visible work. Tiny custom limits remain useful in focused tests and
        // retain their historical all-class behavior.
        _maximumSpeculativeWaiters = maximumWaiters > DefaultVisibleWaiterReserve
            ? maximumWaiters - DefaultVisibleWaiterReserve
            : maximumWaiters;
        _testNotificationScheduler = testNotificationScheduler;
        _testNotificationDeliveryScheduler = testNotificationDeliveryScheduler;
    }

    internal bool TryReserve(
        long bytes,
        bool isVisible,
        bool isReplacement,
        out Reservation? reservation,
        out long observedCapacityVersion)
    {
        ValidateDemand(bytes);
        lock (_sync)
        {
            observedCapacityVersion = _capacityVersion;
            if (!CanReserveLocked(bytes, isVisible, isReplacement))
            {
                reservation = null;
                return false;
            }

            // Allocate before changing the ledger so an allocation failure
            // cannot leak capacity.
            var candidate = new Reservation(this, bytes);
            _chargedBytes += bytes;
            _activeReservationCount++;
            reservation = candidate;
            return true;
        }
    }

    internal bool TryRegisterWaiter(
        IBitmapOutputWaiter owner,
        long requiredBytes,
        bool isVisible,
        bool isReplacement,
        long observedCapacityVersion,
        out WaiterRegistration? registration)
        => TryRegisterWaiter(
            owner,
            requiredBytes,
            isVisible,
            isReplacement,
            observedCapacityVersion,
            out registration,
            out _,
            out _);

    internal bool TryRegisterWaiter(
        IBitmapOutputWaiter owner,
        long requiredBytes,
        bool isVisible,
        bool isReplacement,
        long observedCapacityVersion,
        out WaiterRegistration? registration,
        out long capacityVersionAtAttempt,
        out long waiterSlotVersionAtAttempt)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ValidateDemand(requiredBytes);

        lock (_sync)
        {
            PruneDeadWaitersLocked();
            capacityVersionAtAttempt = _capacityVersion;
            waiterSlotVersionAtAttempt = _waiterSlotVersion;
            int speculativeWaiterCount = 0;
            foreach (WaiterEntry existing in _waiters)
            {
                if (!existing.IsVisible)
                    speculativeWaiterCount++;

                if (existing.Owner.TryGetTarget(out IBitmapOutputWaiter? existingOwner) &&
                    ReferenceEquals(existingOwner, owner))
                {
                    // Each owner keeps one registration and updates it in
                    // place. Returning another token for the same entry would
                    // let stale Dispose calls unregister a promoted waiter.
                    registration = null;
                    return false;
                }
            }

            if (_waiters.Count >= _maximumWaiters ||
                (!isVisible && speculativeWaiterCount >= _maximumSpeculativeWaiters))
            {
                registration = null;
                return false;
            }

            long id = NextWaiterIdLocked();
            var entry = new WaiterEntry(
                id,
                owner,
                requiredBytes,
                isVisible,
                isReplacement,
                ++_nextWaiterSequence);
            var candidate = new WaiterRegistration(this, id);
            _waiters.Add(entry);
            registration = candidate;

            // This is deliberately notification-only. A stale observed
            // version can close the lost-wake window when demand fits, but
            // only TryReserve can claim bytes.
            bool fitsNow = CanReserveLocked(requiredBytes, isVisible, isReplacement);
            bool denialVersionIsStale = observedCapacityVersion != _capacityVersion;
            if (fitsNow && (denialVersionIsStale || entry.LastNotifiedVersion != _capacityVersion))
                QueueNotificationSweepLocked();

            return true;
        }
    }

    internal static bool ShouldRetryWaiterRegistration(
        bool registrationFailed,
        bool waiterPresent,
        long observedCapacityVersion,
        long currentCapacityVersion,
        long observedWaiterSlotVersion,
        long currentWaiterSlotVersion,
        bool visibilityPromoted) =>
        registrationFailed && !waiterPresent &&
        (observedCapacityVersion != currentCapacityVersion ||
         observedWaiterSlotVersion != currentWaiterSlotVersion ||
         visibilityPromoted);

    internal Snapshot GetSnapshot()
    {
        lock (_sync)
        {
            PruneDeadWaitersLocked();
            long offeredBytes = 0;
            foreach (WaiterEntry waiter in _waiters)
            {
                if (waiter.OfferedBytes > 0)
                    offeredBytes += waiter.OfferedBytes;
            }

            return new Snapshot(
                _capacityBytes,
                _speculativeLimitBytes,
                _visibleInitialLimitBytes,
                _transitionHeadroomBytes,
                _chargedBytes,
                _chargedBytes - _uncertainBytes,
                _uncertainBytes,
                _activeReservationCount,
                _uncertainReservationCount,
                _capacityVersion,
                _waiterSlotVersion,
                _waiters.Count,
                _callbackFailureCount,
                offeredBytes);
        }
    }

    private static BitmapOutputAdmission CreateShared()
    {
        long capacity = Environment.Is64BitProcess ? 64 * MiB : 32 * MiB;
        long headroom = Environment.Is64BitProcess ? 16 * MiB : 8 * MiB;
        return new BitmapOutputAdmission(capacity, headroom);
    }

    private void ValidateDemand(long bytes)
    {
        if (bytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(bytes));
        if (bytes > MaximumSingleOutputBytes)
            throw new ArgumentOutOfRangeException(
                nameof(bytes),
                "A single bitmap output cannot exceed the transition headroom.");
    }

    private bool CanReserveLocked(long bytes, bool isVisible, bool isReplacement) =>
        CanReserveAtChargeLocked(bytes, isVisible, isReplacement, _chargedBytes);

    private bool CanReserveAtChargeLocked(
        long bytes,
        bool isVisible,
        bool isReplacement,
        long chargedBytes)
    {
        long limit = !isVisible
            ? _speculativeLimitBytes
            : isReplacement
                ? _capacityBytes
                : _visibleInitialLimitBytes;
        return bytes <= limit && chargedBytes <= limit - bytes;
    }

    private long NextWaiterIdLocked()
    {
        _nextWaiterId = _nextWaiterId == long.MaxValue ? 1 : _nextWaiterId + 1;
        return _nextWaiterId;
    }

    private void PruneDeadWaitersLocked()
    {
        bool returnedCurrentOffer = false;
        bool removedAny = false;
        for (int i = _waiters.Count - 1; i >= 0; i--)
        {
            if (!_waiters[i].Owner.TryGetTarget(out _))
            {
                returnedCurrentOffer |= _waiters[i].OfferedBytes > 0;
                _waiters.RemoveAt(i);
                removedAny = true;
            }
        }

        if (removedAny)
            AdvanceWaiterSlotVersionLocked();
        if (returnedCurrentOffer)
            QueueNotificationSweepLocked();
    }

    private void QueueNotificationSweepLocked()
    {
        if (_notificationSweepQueued || _waiters.Count == 0)
            return;

        _notificationSweepQueued = true;
        try
        {
            if (_testNotificationScheduler is not null)
            {
                // This internal dependency is used only by tests; its
                // contract is to enqueue the action rather than execute it
                // inline while the admission lock is held.
                _testNotificationScheduler(RunNotificationSweepSafely);
                return;
            }

            if (!ThreadPool.QueueUserWorkItem(
                    static state => ((BitmapOutputAdmission)state!).RunNotificationSweepSafely(),
                    this,
                    preferLocal: false))
            {
                _notificationSweepQueued = false;
                _callbackFailureCount++;
            }
        }
        catch
        {
            _notificationSweepQueued = false;
            _callbackFailureCount++;
        }
    }

    private void RunNotificationSweepSafely()
    {
        try
        {
            RunNotificationSweep();
        }
        catch
        {
            // A bookkeeping/allocation failure must not escape a ThreadPool
            // callback. Roll back unissued offers so a later capacity change
            // can retry rather than leaving a phantom reservation in the
            // notification ledger.
            lock (_sync)
            {
                _callbackFailureCount++;
                _notificationSweepQueued = false;
                foreach (WaiterEntry entry in _waiters)
                {
                    if (entry.OfferedVersion == _capacityVersion)
                    {
                        entry.OfferedBytes = 0;
                        entry.OfferedVersion = -1;
                    }
                    if (entry.LastNotifiedVersion == _capacityVersion)
                        entry.LastNotifiedVersion = -1;
                }
            }
        }
    }

    private void RunNotificationSweep()
    {
        List<Notification>? notifications = null;
        lock (_sync)
        {
            _notificationSweepQueued = false;
            PruneDeadWaitersLocked();
            long version = _capacityVersion;

            // Callbacks are hints rather than grants. Simulate the whole
            // notification wave so we never wake more aggregate demand than
            // could fit if each owner reserved immediately.
            var ordered = new List<WaiterEntry>(_waiters.Count);
            ordered.AddRange(_waiters);
            ordered.Sort(static (left, right) =>
            {
                int visibility = right.IsVisible.CompareTo(left.IsVisible);
                return visibility != 0 ? visibility : left.Sequence.CompareTo(right.Sequence);
            });

            long simulatedCharge = _chargedBytes;
            foreach (WaiterEntry entry in _waiters)
            {
                if (entry.OfferedBytes > 0)
                    simulatedCharge += entry.OfferedBytes;
            }

            foreach (WaiterEntry entry in ordered)
            {
                if (entry.OfferedBytes > 0 || entry.LastNotifiedVersion == version ||
                    !CanReserveAtChargeLocked(
                        entry.RequiredBytes,
                        entry.IsVisible,
                        entry.IsReplacement,
                        simulatedCharge) ||
                    !entry.Owner.TryGetTarget(out IBitmapOutputWaiter? owner))
                {
                    continue;
                }

                // Hold a strong reference only for the duration of this
                // out-of-lock callback batch. Persistent ownership is weak.
                notifications ??= new List<Notification>();
                notifications.Add(new Notification(entry.Id, entry.Sequence, version, owner));
                entry.LastNotifiedVersion = version;
                entry.OfferedVersion = version;
                entry.OfferedBytes = entry.RequiredBytes;
                simulatedCharge += entry.RequiredBytes;
            }
        }

        if (notifications is null)
            return;

        foreach (Notification notification in notifications)
        {
            try
            {
                if (_testNotificationDeliveryScheduler is { } scheduler)
                    scheduler(() => DeliverNotificationSafely(notification));
                else
                    DeliverNotificationSafely(notification);
            }
            catch
            {
                lock (_sync)
                {
                    _callbackFailureCount++;
                    RemoveWaiterLocked(notification.Id);
                    // A callback that threw cannot consume its notification.
                    // Remove it and offer the same-version wave to the next
                    // fitting waiter.
                    QueueNotificationSweepLocked();
                }
            }
        }
    }

    private void DeliverNotificationSafely(Notification notification)
    {
        try
        {
            notification.Owner.OnBitmapOutputCapacityAvailable(notification.CapacityVersion);
        }
        catch
        {
            lock (_sync)
            {
                _callbackFailureCount++;
                RemoveWaiterLocked(notification.Id);
                // A callback that threw cannot consume its notification.
                // Remove it and offer the same-version wave to the next
                // fitting waiter.
                QueueNotificationSweepLocked();
            }
        }
    }

    private bool UpdateWaiter(
        long id,
        long requiredBytes,
        bool isVisible,
        bool isReplacement,
        long observedCapacityVersion,
        out long capacityVersionAtAttempt,
        out long waiterSlotVersionAtAttempt)
    {
        ValidateDemand(requiredBytes);
        lock (_sync)
        {
            capacityVersionAtAttempt = _capacityVersion;
            waiterSlotVersionAtAttempt = _waiterSlotVersion;
            WaiterEntry? entry = FindWaiterLocked(id);
            if (entry is null)
                return false;

            bool releasedSpeculativeSlot = !entry.IsVisible && isVisible;
            bool changed = entry.RequiredBytes != requiredBytes ||
                entry.IsVisible != isVisible ||
                entry.IsReplacement != isReplacement;
            bool callbackOutstanding = entry.OfferedBytes > 0;
            entry.RequiredBytes = requiredBytes;
            entry.IsVisible = isVisible;
            entry.IsReplacement = isReplacement;
            if (releasedSpeculativeSlot)
            {
                // Promotion does not free bitmap bytes, but it does free one
                // speculative queue entitlement. Recovery scans use this
                // generation to retry rejected speculative registrations.
                AdvanceWaiterSlotVersionLocked();
                waiterSlotVersionAtAttempt = _waiterSlotVersion;
            }

            bool staleVersion = observedCapacityVersion != _capacityVersion;
            if (!callbackOutstanding && (changed || staleVersion))
            {
                entry.LastNotifiedVersion = -1;
                entry.OfferedVersion = -1;
                entry.OfferedBytes = 0;
            }

            if (!callbackOutstanding && CanReserveLocked(requiredBytes, isVisible, isReplacement) &&
                entry.LastNotifiedVersion != _capacityVersion)
            {
                QueueNotificationSweepLocked();
            }

            return true;
        }
    }

    private bool RemoveWaiter(long id)
    {
        lock (_sync)
        {
            WaiterEntry? entry = FindWaiterLocked(id);
            if (entry is null)
                return false;

            bool alreadyNotifiedThisVersion = entry.LastNotifiedVersion == _capacityVersion;
            bool returnedOutstandingOffer = entry.OfferedBytes > 0;
            _waiters.Remove(entry);
            AdvanceWaiterSlotVersionLocked();
            // A disposed registration is canceled work, not an active retry.
            // If a selected notification has not yet been settled by
            // CompleteRetry, return its virtual demand so the next fitting
            // waiter can use the capacity. The image owner retains its token
            // across an actual retry and settles it only after TryReserve (or
            // a cache hit), so normal retry setup cannot accidentally fan out
            // the same capacity.
            if (returnedOutstandingOffer || !alreadyNotifiedThisVersion)
                QueueNotificationSweepLocked();
            return true;
        }
    }

    private bool ReturnUnusedNotification(long id)
    {
        lock (_sync)
        {
            if (!RemoveWaiterLocked(id))
                return false;

            // The caller explicitly confirms that it canceled before starting
            // the retry. It did not consume the notification's virtual demand,
            // so another waiter may be offered the same-version capacity.
            QueueNotificationSweepLocked();
            return true;
        }
    }

    private bool CompleteRetry(long id, bool capacityClaimed)
    {
        lock (_sync)
        {
            WaiterEntry? entry = FindWaiterLocked(id);
            if (entry is null)
                return false;

            // The active charge created by a successful TryReserve (or a
            // cache hit's pre-existing owner) now represents this demand. A
            // failed attempt returns only its virtual offer; the registration
            // remains dormant until a later capacity generation.
            entry.OfferedBytes = 0;
            entry.OfferedVersion = -1;
            if (capacityClaimed)
            {
                _waiters.Remove(entry);
                AdvanceWaiterSlotVersionLocked();
                QueueNotificationSweepLocked();
                return true;
            }

            // Only one notification may be in flight per owner. When it
            // returns without claiming, suppress another same-version retry
            // for that owner but offer newly available virtual demand to the
            // next waiter. A later capacity version can wake this owner again.
            entry.LastNotifiedVersion = _capacityVersion;
            QueueNotificationSweepLocked();
            return true;
        }
    }

    private bool RemoveWaiterLocked(long id)
    {
        WaiterEntry? entry = FindWaiterLocked(id);
        if (entry is null)
            return false;
        _waiters.Remove(entry);
        AdvanceWaiterSlotVersionLocked();
        return true;
    }

    private WaiterEntry? FindWaiterLocked(long id)
    {
        for (int i = 0; i < _waiters.Count; i++)
        {
            if (_waiters[i].Id == id)
                return _waiters[i];
        }

        return null;
    }

    private Reservation Split(Reservation reservation, long bytes)
    {
        lock (_sync)
        {
            if (reservation.State != ReservationState.Reserved)
                throw new InvalidOperationException("Only an uncommitted reservation can be split.");
            if (bytes <= 0 || bytes >= reservation.ByteCount)
                throw new ArgumentOutOfRangeException(nameof(bytes));

            // Construct the child before mutating the parent; if allocation
            // fails, the original reservation remains intact.
            var child = new Reservation(this, bytes);
            reservation.ByteCount -= bytes;
            _activeReservationCount++;
            return child;
        }
    }

    private bool Commit(Reservation reservation)
    {
        lock (_sync)
        {
            if (reservation.State is ReservationState.Committed or ReservationState.Transferred)
                return true;
            if (reservation.State != ReservationState.Reserved)
                return false;
            reservation.State = ReservationState.Committed;
            return true;
        }
    }

    private bool TransferToEntry(Reservation reservation)
    {
        lock (_sync)
        {
            if (reservation.State != ReservationState.Committed)
                return false;
            reservation.State = ReservationState.Transferred;
            return true;
        }
    }

    private bool ResizeDown(Reservation reservation, long actualBytes)
    {
        if (actualBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(actualBytes));

        lock (_sync)
        {
            if (reservation.State is not (ReservationState.Reserved or
                ReservationState.Committed or ReservationState.Transferred))
            {
                return false;
            }

            if (actualBytes > reservation.ByteCount)
                return false;
            if (actualBytes == reservation.ByteCount)
                return true;

            long released = reservation.ByteCount - actualBytes;
            reservation.ByteCount = actualBytes;
            _chargedBytes -= released;
            AdvanceCapacityVersionLocked();
            QueueNotificationSweepLocked();
            return true;
        }
    }

    private bool ReleaseAfterSuccessfulCleanup(Reservation reservation)
    {
        lock (_sync)
        {
            if (reservation.State == ReservationState.Released)
                return true;
            if (reservation.State is not (ReservationState.Committed or ReservationState.Transferred))
                return false;

            _chargedBytes -= reservation.ByteCount;
            _activeReservationCount--;
            reservation.State = ReservationState.Released;
            AdvanceCapacityVersionLocked();
            QueueNotificationSweepLocked();
            return true;
        }
    }

    private bool MarkUncertain(Reservation reservation)
    {
        lock (_sync)
        {
            if (reservation.State == ReservationState.Uncertain)
                return true;
            if (reservation.State is ReservationState.Released)
                return false;

            _activeReservationCount--;
            _uncertainReservationCount++;
            _uncertainBytes += reservation.ByteCount;
            reservation.State = ReservationState.Uncertain;
            return true;
        }
    }

    private void DisposeReservation(Reservation reservation)
    {
        lock (_sync)
        {
            if (reservation.State == ReservationState.Reserved)
            {
                _chargedBytes -= reservation.ByteCount;
                _activeReservationCount--;
                reservation.State = ReservationState.Released;
                AdvanceCapacityVersionLocked();
                QueueNotificationSweepLocked();
                return;
            }

            if (reservation.State is ReservationState.Committed or ReservationState.Transferred)
            {
                _activeReservationCount--;
                _uncertainReservationCount++;
                _uncertainBytes += reservation.ByteCount;
                reservation.State = ReservationState.Uncertain;
            }
        }
    }

    private void AdvanceCapacityVersionLocked()
    {
        if (_capacityVersion == long.MaxValue)
        {
            _capacityVersion = 1;
            foreach (WaiterEntry entry in _waiters)
            {
                entry.LastNotifiedVersion = -1;
                entry.OfferedVersion = -1;
                entry.OfferedBytes = 0;
            }
        }
        else
        {
            _capacityVersion++;
        }
    }

    private void AdvanceWaiterSlotVersionLocked() =>
        _waiterSlotVersion = _waiterSlotVersion == long.MaxValue
            ? 1
            : _waiterSlotVersion + 1;

    internal sealed class WaiterRegistration : IDisposable
    {
        private readonly BitmapOutputAdmission _admission;
        private readonly long _id;
        private int _disposed;

        internal WaiterRegistration(BitmapOutputAdmission admission, long id)
        {
            _admission = admission;
            _id = id;
        }

        internal bool UpdateDemand(
            long requiredBytes,
            bool isVisible,
            bool isReplacement,
            long observedCapacityVersion)
            => UpdateDemand(
                requiredBytes,
                isVisible,
                isReplacement,
                observedCapacityVersion,
                out _,
                out _);

        internal bool UpdateDemand(
            long requiredBytes,
            bool isVisible,
            bool isReplacement,
            long observedCapacityVersion,
            out long capacityVersionAtAttempt,
            out long waiterSlotVersionAtAttempt)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                Snapshot snapshot = _admission.GetSnapshot();
                capacityVersionAtAttempt = snapshot.CapacityVersion;
                waiterSlotVersionAtAttempt = snapshot.WaiterSlotVersion;
                return false;
            }
            return _admission.UpdateWaiter(
                _id,
                requiredBytes,
                isVisible,
                isReplacement,
                observedCapacityVersion,
                out capacityVersionAtAttempt,
                out waiterSlotVersionAtAttempt);
        }

        /// <summary>
        /// Settles a delivered retry hint. The caller passes true only after
        /// admission succeeded (or an already-admitted cache lease satisfied
        /// the request); false releases the virtual offer but keeps the waiter
        /// dormant until a later capacity version.
        /// </summary>
        internal bool CompleteRetry(bool capacityClaimed)
        {
            if (Volatile.Read(ref _disposed) != 0)
                return false;

            bool completed = _admission.CompleteRetry(_id, capacityClaimed);
            if (completed && capacityClaimed)
                Interlocked.Exchange(ref _disposed, 1);
            return completed;
        }

        /// <summary>
        /// Abandons a notification before starting the associated retry and
        /// passes its same-version opportunity to another waiter.
        /// </summary>
        internal bool ReturnUnusedNotification()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return false;
            return _admission.ReturnUnusedNotification(_id);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            _admission.RemoveWaiter(_id);
        }
    }

    internal sealed class Reservation : IDisposable
    {
        private readonly BitmapOutputAdmission _admission;
        private long _byteCount;
        private ReservationState _state;

        // Read or mutate only while holding the owning admission's ledger lock.
        internal long ByteCount
        {
            get => _byteCount;
            set => _byteCount = value;
        }

        internal ReservationState State
        {
            get => _state;
            set => _state = value;
        }

        internal Reservation(BitmapOutputAdmission admission, long bytes)
        {
            _admission = admission;
            _byteCount = bytes;
        }

        internal long Bytes => _admission.GetBytes(this);
        internal long ReservedBytes => Bytes;
        internal bool IsCommitted => _admission.IsCommitted(this);
        internal bool IsTransferred => _admission.IsTransferred(this);

        internal bool Commit() => _admission.Commit(this);
        internal bool TransferToEntry() => _admission.TransferToEntry(this);
        internal bool ResizeDown(long actualBytes) => _admission.ResizeDown(this, actualBytes);
        internal Reservation Split(long bytes) => _admission.Split(this, bytes);
        internal bool ReleaseAfterSuccessfulCleanup() => _admission.ReleaseAfterSuccessfulCleanup(this);
        internal bool MarkUncertain() => _admission.MarkUncertain(this);

        public void Dispose() => _admission.DisposeReservation(this);
    }

    internal readonly struct Snapshot(
        long CapacityBytes,
        long SpeculativeLimitBytes,
        long VisibleInitialLimitBytes,
        long MaximumSingleOutputBytes,
        long ChargedBytes,
        long ActiveBytes,
        long UncertainBytes,
        int ActiveReservationCount,
        int UncertainReservationCount,
        long CapacityVersion,
        long WaiterSlotVersion,
        int WaiterCount,
        long CallbackFailureCount,
        long OfferedBytes)
    {
        public long CapacityBytes { get; init; } = CapacityBytes;
        public long SpeculativeLimitBytes { get; init; } = SpeculativeLimitBytes;
        public long VisibleInitialLimitBytes { get; init; } = VisibleInitialLimitBytes;
        public long MaximumSingleOutputBytes { get; init; } = MaximumSingleOutputBytes;
        public long ChargedBytes { get; init; } = ChargedBytes;
        public long ActiveBytes { get; init; } = ActiveBytes;
        public long UncertainBytes { get; init; } = UncertainBytes;
        public int ActiveReservationCount { get; init; } = ActiveReservationCount;
        public int UncertainReservationCount { get; init; } = UncertainReservationCount;
        public long CapacityVersion { get; init; } = CapacityVersion;
        public long WaiterSlotVersion { get; init; } = WaiterSlotVersion;
        public int WaiterCount { get; init; } = WaiterCount;
        public long CallbackFailureCount { get; init; } = CallbackFailureCount;
        public long OfferedBytes { get; init; } = OfferedBytes;
    }

    internal enum ReservationState
    {
        Reserved,
        Committed,
        Transferred,
        Released,
        Uncertain,
    }

    private sealed class WaiterEntry(
        long id,
        IBitmapOutputWaiter owner,
        long requiredBytes,
        bool isVisible,
        bool isReplacement,
        long sequence)
    {
        internal readonly long Id = id;
        internal readonly WeakReference<IBitmapOutputWaiter> Owner = new(owner);
        internal readonly long Sequence = sequence;
        internal long RequiredBytes = requiredBytes;
        internal bool IsVisible = isVisible;
        internal bool IsReplacement = isReplacement;
        internal long LastNotifiedVersion = -1;
        internal long OfferedVersion = -1;
        internal long OfferedBytes;
    }

    private readonly struct Notification(
        long Id,
        long Sequence,
        long CapacityVersion,
        IBitmapOutputWaiter Owner)
    {
        public long Id { get; init; } = Id;
        public long Sequence { get; init; } = Sequence;
        public long CapacityVersion { get; init; } = CapacityVersion;
        public IBitmapOutputWaiter Owner { get; init; } = Owner;
    }

    private long GetBytes(Reservation reservation)
    {
        lock (_sync)
            return reservation.ByteCount;
    }

    private bool IsCommitted(Reservation reservation)
    {
        lock (_sync)
            return reservation.State is ReservationState.Committed or ReservationState.Transferred;
    }

    private bool IsTransferred(Reservation reservation)
    {
        lock (_sync)
            return reservation.State == ReservationState.Transferred;
    }
}
