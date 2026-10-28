using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using MarkdownRenderer.Diagnostics;
using MarkdownRenderer.Images;

namespace MarkdownRenderer.Layout.Boxes;

/// <summary>
/// Weak, bounded host-side coordination for provider-owned tiled SVG source
/// snapshots. It stores no source arrays, documents, or strong ImageBox refs.
/// </summary>
internal static class SvgHostAdmissionCoordinator
{
    private const int MaximumAcceptedOwnersPerDomain = 512;
    private const int MaximumPendingDemandsPerDomain = 128;
    private const int MaximumVictimsPerAdmission = 8;

    private static readonly ConditionalWeakTable<object, DomainState> s_domains = new();

    internal sealed class OwnerSlot : IDisposable
    {
        private readonly object _sync = new();
        private DomainState? _domain;
        private OwnerEntry? _entry;
        private int _state;

        internal OwnerSlot(object admissionDomain, DomainState domain)
        {
            AdmissionDomain = admissionDomain;
            _domain = domain;
        }

        internal object AdmissionDomain { get; }

        internal void Commit(
            ImageBox owner,
            ImageBox.SvgTileRenderPlan plan,
            MarkdownSvgRenderDemand demand,
            long sourceReservationBytes)
        {
            ArgumentNullException.ThrowIfNull(owner);
            ArgumentNullException.ThrowIfNull(plan);
            ArgumentNullException.ThrowIfNull(demand);
            if (sourceReservationBytes <= 0)
                throw new ArgumentOutOfRangeException(nameof(sourceReservationBytes));
            lock (_sync)
            {
                if (_state != 0)
                    throw new InvalidOperationException("The SVG owner slot is no longer reserved.");

                DomainState domain = _domain ?? throw new ObjectDisposedException(nameof(OwnerSlot));
                OwnerEntry entry = domain.Commit(owner, plan, demand, this, sourceReservationBytes);
                _entry = entry;
                _state = 1;
            }
        }

        internal void Touch()
        {
            DomainState? domain;
            OwnerEntry? entry;
            lock (_sync)
            {
                if (_state != 1)
                    return;
                domain = _domain;
                entry = _entry;
            }

            if (domain is not null && entry is not null)
                domain.Touch(entry);
        }

        public void Dispose()
        {
            DomainState? domain;
            OwnerEntry? entry;
            lock (_sync)
            {
                if (_state == 2)
                    return;
                _state = 2;
                domain = _domain;
                entry = _entry;
                _domain = null;
                _entry = null;
            }

            if (domain is not null)
                domain.Release(this, entry);
        }
    }

    internal sealed class OwnerEntry(
        ImageBox owner,
        ImageBox.SvgTileRenderPlan plan,
        MarkdownSvgRenderDemand demand,
        long sourceReservationBytes,
        OwnerSlot slot,
        long lastUse)
    {
        internal readonly WeakReference<ImageBox> Owner = new(owner);
        internal readonly WeakReference<ImageBox.SvgTileRenderPlan> Plan = new(plan);
        internal readonly MarkdownSvgRenderDemand Demand = demand;
        internal readonly long SourceReservationBytes = sourceReservationBytes;
        internal readonly WeakReference<OwnerSlot> Slot = new(slot);
        internal long LastUse = lastUse;
    }

    internal sealed class PendingEntry(
        ImageBox owner,
        IMarkdownSvgAdmission admission,
        MarkdownSvgRenderDemand demand,
        int loadGeneration,
        long capacityVersion,
        long requiredSourceReservationBytes,
        bool requiresOwnerSlot,
        long ownerVersion,
        long demandVersion,
        long visualDemandEpoch,
        CancellationToken cancellationToken)
    {
        internal readonly WeakReference<ImageBox> Owner = new(owner);
        internal readonly WeakReference<IMarkdownSvgAdmission> Admission = new(admission);
        internal readonly MarkdownSvgRenderDemand Demand = demand;
        internal readonly int LoadGeneration = loadGeneration;
        internal readonly object Identity = new();
        internal long CapacityVersion = capacityVersion;
        internal long RequiredSourceReservationBytes = requiredSourceReservationBytes;
        internal bool RequiresOwnerSlot = requiresOwnerSlot;
        internal long OwnerVersion = ownerVersion;
        internal long DemandVersionAtDenial = demandVersion;
        internal long VisualDemandEpochAtDenial = visualDemandEpoch;
        internal readonly TaskCompletionSource Promotion = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        internal IDisposable? PromotionRegistration;
        internal readonly object CancellationSync = new();
        // Create the linked source last among the allocating initializers. If
        // an earlier allocation fails during construction, no parent-token
        // registration has been created that could keep this partial entry
        // alive without a reachable owner to dispose it.
        internal readonly CancellationTokenSource Cancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        internal Task? CancellationCallbacks;
        internal int Claimed;
        internal int Removed;
        internal int WaiterCompletionStarted;
        internal int WaiterFinished;
        internal int WaiterHandles = 1;
        // Initial row fields are provisional until one exact ImageBox
        // registration accepts the row. Concurrent rejected attempts must not
        // raise a live waiter's threshold or select its wait kind.
        internal bool HasAcceptedRegistration;
        // 0=no startup owner, 1=startup owner claimed, 2=waiter task scheduled.
        internal int WaiterStartState;
        internal int UnstartedCleanupStarted;
        // Every Defer call publishes its ImageBox identity outside this
        // registry lock. A ready waiter cannot claim the row until all such
        // callbacks have completed, so a capacity pulse cannot race the
        // accepted identity into the host retry gate.
        internal int RegistrationsInFlight;
        internal TaskCompletionSource? RegistrationsDrained;

        // Pressure parking is cumulative per pending demand. Duplicate
        // threshold increases may schedule additional victims, but can never
        // reset the eight-victim bound.
        internal long ParkedSourceBytes;
        internal int ParkedVictims;
        internal bool ParkingInProgress;
    }

    internal sealed class PendingWaiter(DomainState domain, PendingEntry entry) : IDisposable
    {
        private DomainState? _domain = domain;
        private PendingEntry? _entry = entry;
        private readonly object _identity = entry.Identity;

        internal object Identity => _identity;
        internal bool IsFinished => Volatile.Read(ref _entry) is not { } currentEntry ||
            Volatile.Read(ref currentEntry.WaiterCompletionStarted) != 0;

        public void Dispose()
        {
            DomainState? currentDomain = Interlocked.Exchange(ref _domain, null);
            PendingEntry? currentEntry = Interlocked.Exchange(ref _entry, null);
            if (currentDomain is not null && currentEntry is not null)
                currentDomain.ReleaseWaiterHandle(currentEntry);
        }
    }

    internal sealed class DomainState
    {
        private readonly object _gate = new();
        private readonly List<OwnerEntry> _owners = new(MaximumAcceptedOwnersPerDomain);
        private readonly List<PendingEntry> _pending = new(MaximumPendingDemandsPerDomain);
        private int _reservedOwnerSlots;
        private long _ownerVersion;
        private long _useClock;
        private TaskCompletionSource<long>? _ownerVersionChanged;

        internal long OwnerVersion
        {
            get { lock (_gate) return _ownerVersion; }
        }

        internal static int CompareParkCandidateOrder(
            int leftTier,
            long leftLastUse,
            int rightTier,
            long rightLastUse)
        {
            int tier = leftTier.CompareTo(rightTier);
            return tier != 0 ? tier : leftLastUse.CompareTo(rightLastUse);
        }

        internal OwnerSlot? TryReserveOwnerSlot(
            object admissionDomain,
            out long observedOwnerVersion)
        {
            PruneDeadOwners();
            lock (_gate)
            {
                observedOwnerVersion = _ownerVersion;
                if (_owners.Count + _reservedOwnerSlots >= MaximumAcceptedOwnersPerDomain)
                    return null;

                var slot = new OwnerSlot(admissionDomain, this);
                _reservedOwnerSlots++;
                return slot;
            }
        }

        internal OwnerEntry Commit(
            ImageBox owner,
            ImageBox.SvgTileRenderPlan plan,
            MarkdownSvgRenderDemand demand,
            OwnerSlot slot,
            long sourceReservationBytes)
        {
            var entry = new OwnerEntry(
                owner,
                plan,
                demand,
                sourceReservationBytes,
                slot,
                lastUse: 0);
            lock (_gate)
            {
                if (_reservedOwnerSlots <= 0 ||
                    _owners.Count >= MaximumAcceptedOwnersPerDomain)
                {
                    throw new InvalidOperationException("The reserved SVG owner slot was lost.");
                }

                _reservedOwnerSlots--;
                entry.LastUse = ++_useClock;
                _owners.Add(entry);
                return entry;
            }
        }

        internal void Touch(OwnerEntry entry)
        {
            lock (_gate)
            {
                if (_owners.Contains(entry))
                    entry.LastUse = ++_useClock;
            }
        }

        internal void Release(OwnerSlot slot, OwnerEntry? entry)
        {
            TaskCompletionSource<long>? changed = null;
            long version = 0;
            lock (_gate)
            {
                if (entry is null)
                {
                    if (_reservedOwnerSlots > 0)
                        _reservedOwnerSlots--;
                }
                else
                {
                    int index = -1;
                    for (int candidateIndex = 0; candidateIndex < _owners.Count; candidateIndex++)
                    {
                        if (ReferenceEquals(_owners[candidateIndex], entry))
                        {
                            index = candidateIndex;
                            break;
                        }
                    }
                    if (index >= 0)
                        _owners.RemoveAt(index);
                }

                _ownerVersion++;
                version = _ownerVersion;
                changed = _ownerVersionChanged;
                _ownerVersionChanged = null;
            }

            // All signal continuations are asynchronous and run after the
            // coordinator gate is released.
            changed?.TrySetResult(version);
        }

        internal long GetOwnerVersion() => OwnerVersion;

        internal Task WaitForOwnerVersionChangeAsync(long observedVersion, CancellationToken token)
        {
            lock (_gate)
            {
                if (_ownerVersion != observedVersion)
                    return Task.CompletedTask;
                return (_ownerVersionChanged ??= NewVersionSignal()).Task.WaitAsync(token);
            }
        }

        internal PendingWaiter? Defer(
            ImageBox owner,
            IMarkdownSvgAdmission admission,
            MarkdownSvgRenderDemand demand,
            int loadGeneration,
            long capacityVersion,
            long requiredSourceReservationBytes,
            bool requiresOwnerSlot,
            long ownerVersionAtDenial,
            long demandVersionAtDenial,
            long visualDemandEpochAtDenial,
            object registrationAttempt,
            CancellationToken cancellationToken)
        {
            PruneDeadPending();
            // Allocate a possible zero-to-one registration signal before
            // mutating a published row. When another registration is already
            // in flight this instance is simply unused.
            var proposedRegistrationsDrained = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            PendingEntry? entry;
            bool thresholdIncreased = false;
            PendingWaiter waiter;
            lock (_gate)
            {
                entry = FindPendingNoLock(owner, loadGeneration, demand);
                if (entry is not null)
                {
                    waiter = new PendingWaiter(this, entry);
                    BeginRegistrationNoLock(entry, proposedRegistrationsDrained);
                    entry.WaiterHandles++;
                }
                else
                {
                    if (_pending.Count >= MaximumPendingDemandsPerDomain)
                        return null;

                    entry = new PendingEntry(
                        owner,
                        admission,
                        demand,
                        loadGeneration,
                        capacityVersion,
                        requiredSourceReservationBytes,
                        requiresOwnerSlot,
                        ownerVersionAtDenial,
                        demandVersionAtDenial,
                        visualDemandEpochAtDenial,
                        cancellationToken);
                    try
                    {
                        waiter = new PendingWaiter(this, entry);
                    }
                    catch
                    {
                        entry.Cancellation.Dispose();
                        throw;
                    }
                    _pending.Add(entry);
                    BeginRegistrationNoLock(entry, proposedRegistrationsDrained);
                }
            }

            bool registered = false;
            try
            {
                registered = owner.OnSvgHostAdmissionWaiterRegistered(
                    demand,
                    loadGeneration,
                    registrationAttempt,
                    waiter);
            }
            catch (Exception exception)
            {
                MarkdownDiagnostics.WriteLine(
                    $"[SvgAdmission] waiter registration failed: {exception.GetType().Name}");
            }
            finally
            {
                try
                {
                    if (!registered)
                        waiter.Dispose();
                }
                finally
                {
                    TaskCompletionSource? registrationsDrained = EndRegistration(
                        entry,
                        registered,
                        capacityVersion,
                        requiredSourceReservationBytes,
                        requiresOwnerSlot,
                        admission,
                        ownerVersionAtDenial,
                        demandVersionAtDenial,
                        visualDemandEpochAtDenial,
                        out bool acceptedThresholdIncreased);
                    thresholdIncreased |= acceptedThresholdIncreased;
                    // RunContinuationsAsynchronously ensures the waiter cannot
                    // re-enter this host after completion while a domain or
                    // ImageBox lock remains held by the registration caller.
                    registrationsDrained?.TrySetResult();
                }
            }

            bool ownsWaiterStart = TryClaimPendingWaiterStart(entry);
            bool waiterStartSucceeded = !ownsWaiterStart || StartPendingWaiter(entry);
            if (!registered)
                return null;

            if (!waiterStartSucceeded)
            {
                waiter.Dispose();
                return null;
            }

            bool isPending = IsPendingEntry(entry);
            bool wasClaimed = Volatile.Read(ref entry.Claimed) != 0;
            if (!isPending && !wasClaimed)
            {
                // Cancellation/startup failure is rejection. A threshold or
                // promotion which already claimed the row is different: its
                // deferred retry may be queued before this accepted handle is
                // returned, so returning an already-finished handle preserves
                // the normal ImageBox terminal-state check without pretending
                // the registration failed.
                waiter.Dispose();
                return null;
            }

            if (isPending && thresholdIncreased)
                TryParkForPendingDemand(entry);

            return waiter;
        }

        private static void BeginRegistrationNoLock(
            PendingEntry entry,
            TaskCompletionSource proposedRegistrationsDrained)
        {
            if (entry.RegistrationsInFlight == 0)
                entry.RegistrationsDrained = proposedRegistrationsDrained;
            else if (entry.RegistrationsDrained is null)
                throw new InvalidOperationException("An in-flight SVG waiter registration has no drain signal.");

            entry.RegistrationsInFlight++;
        }

        private TaskCompletionSource? EndRegistration(
            PendingEntry entry,
            bool registered,
            long capacityVersion,
            long requiredSourceReservationBytes,
            bool requiresOwnerSlot,
            IMarkdownSvgAdmission admission,
            long ownerVersionAtDenial,
            long demandVersionAtDenial,
            long visualDemandEpochAtDenial,
            out bool thresholdIncreased)
        {
            thresholdIncreased = false;
            lock (_gate)
            {
                if (entry.RegistrationsInFlight <= 0)
                    throw new InvalidOperationException("An SVG waiter registration completed without ownership.");

                // A row's initial fields are provisional until the first
                // accepted ImageBox identity. Later accepted duplicates merge
                // their monotonic max threshold and earliest observation
                // epochs here. The in-flight count remains nonzero until after
                // this merge, so readiness cannot claim between registration
                // and identity publication.
                if (registered && _pending.Contains(entry) &&
                    Volatile.Read(ref entry.Removed) == 0)
                {
                    if (!entry.HasAcceptedRegistration)
                    {
                        entry.Admission.SetTarget(admission);
                        entry.CapacityVersion = capacityVersion;
                        entry.RequiredSourceReservationBytes = requiredSourceReservationBytes;
                        entry.RequiresOwnerSlot = requiresOwnerSlot;
                        entry.OwnerVersion = ownerVersionAtDenial;
                        entry.DemandVersionAtDenial = demandVersionAtDenial;
                        entry.VisualDemandEpochAtDenial = visualDemandEpochAtDenial;
                        entry.HasAcceptedRegistration = true;
                        thresholdIncreased = requiredSourceReservationBytes > 0;
                    }
                    else
                    {
                        entry.CapacityVersion = Math.Min(entry.CapacityVersion, capacityVersion);
                        thresholdIncreased = requiredSourceReservationBytes >
                            entry.RequiredSourceReservationBytes;
                        entry.RequiredSourceReservationBytes = Math.Max(
                            entry.RequiredSourceReservationBytes,
                            requiredSourceReservationBytes);
                        entry.OwnerVersion = Math.Min(entry.OwnerVersion, ownerVersionAtDenial);
                        entry.DemandVersionAtDenial = Math.Min(
                            entry.DemandVersionAtDenial,
                            demandVersionAtDenial);
                        entry.VisualDemandEpochAtDenial = Math.Min(
                            entry.VisualDemandEpochAtDenial,
                            visualDemandEpochAtDenial);
                    }
                }

                entry.RegistrationsInFlight--;
                if (entry.RegistrationsInFlight != 0)
                    return null;

                TaskCompletionSource? completed = entry.RegistrationsDrained;
                entry.RegistrationsDrained = null;
                return completed;
            }
        }

        private bool TryClaimPendingWaiterStart(PendingEntry entry)
        {
            lock (_gate)
            {
                if (!_pending.Contains(entry) || Volatile.Read(ref entry.Removed) != 0 ||
                    Volatile.Read(ref entry.Claimed) != 0 || !entry.HasAcceptedRegistration ||
                    entry.RegistrationsInFlight != 0 || entry.WaiterStartState != 0)
                {
                    return false;
                }

                entry.WaiterStartState = 1;
                return true;
            }
        }

        private bool IsPendingEntry(PendingEntry entry)
        {
            lock (_gate)
                return _pending.Contains(entry) && Volatile.Read(ref entry.Removed) == 0;
        }

        private bool StartPendingWaiter(PendingEntry entry)
        {
            try
            {
                long denialVersion;
                bool canStart;
                lock (_gate)
                {
                    canStart = _pending.Contains(entry) && Volatile.Read(ref entry.Removed) == 0 &&
                        entry.WaiterStartState == 1;
                    denialVersion = entry.DemandVersionAtDenial;
                }
                if (!canStart)
                {
                    CancelWithoutWaiter(entry);
                    return false;
                }

                if (entry.Demand.Version == denialVersion)
                {
                    IDisposable registration = entry.Demand.RegisterPromotion(
                        () => entry.Promotion.TrySetResult());
                    bool retained;
                    lock (_gate)
                    {
                        retained = _pending.Contains(entry) &&
                            Volatile.Read(ref entry.Removed) == 0 &&
                            entry.WaiterStartState == 1;
                        if (retained)
                            entry.PromotionRegistration = registration;
                    }
                    if (!retained)
                    {
                        try { registration.Dispose(); }
                        catch (Exception exception)
                        {
                            MarkdownDiagnostics.WriteLine(
                                $"[SvgAdmission] promotion retirement failed: {exception.GetType().Name}");
                        }
                        CancelWithoutWaiter(entry);
                        return false;
                    }
                }

                bool schedule;
                lock (_gate)
                {
                    schedule = _pending.Contains(entry) &&
                        Volatile.Read(ref entry.Removed) == 0 &&
                        entry.WaiterStartState == 1;
                    if (schedule)
                        entry.WaiterStartState = 2;
                }
                if (!schedule)
                {
                    CancelWithoutWaiter(entry);
                    return false;
                }

                // Register the source-free waiter before pressure relief. Any
                // capacity released by parking is therefore observable by the
                // exact threshold subscription.
                _ = Task.Run(() => WaitAndResumeAsync(entry));
                return true;
            }
            catch (Exception exception)
            {
                MarkdownDiagnostics.WriteLine(
                    $"[SvgAdmission] pending waiter startup failed: {exception.GetType().Name}");
                CancelWithoutWaiter(entry);
                return false;
            }
        }

        internal void ReleaseWaiterHandle(PendingEntry entry)
        {
            bool shouldCancel = false;
            bool unstarted = false;
            lock (_gate)
            {
                if (entry.WaiterHandles > 0)
                {
                    entry.WaiterHandles--;
                    shouldCancel = entry.WaiterHandles == 0 && _pending.Remove(entry);
                    unstarted = shouldCancel && entry.WaiterStartState != 2;
                }
            }
            if (shouldCancel)
            {
                if (unstarted)
                    CancelWithoutWaiter(entry);
                else
                    _ = RequestPendingCancellation(entry);
            }
        }

        internal void TouchOwner(ImageBox.SvgTileRenderPlan plan)
        {
            OwnerEntry? found = null;
            lock (_gate)
            {
                foreach (OwnerEntry candidate in _owners)
                {
                    if (candidate.Plan.TryGetTarget(out ImageBox.SvgTileRenderPlan? current) &&
                        ReferenceEquals(current, plan))
                    {
                        found = candidate;
                        break;
                    }
                }
                if (found is not null)
                    found.LastUse = ++_useClock;
            }
        }

        private void CancelWithoutWaiter(PendingEntry entry)
        {
            if (Interlocked.Exchange(ref entry.UnstartedCleanupStarted, 1) != 0 ||
                Interlocked.Exchange(ref entry.WaiterFinished, 1) != 0)
                return;

            lock (_gate)
            {
                _pending.Remove(entry);
                Interlocked.Exchange(ref entry.Removed, 1);
            }
            DisposePromotionRegistration(entry);
            _ = DrainUnstartedWaiterAsync(entry);
        }

        private void PruneDeadPending()
        {
            List<PendingEntry>? abandoned = null;
            lock (_gate)
            {
                for (int index = _pending.Count - 1; index >= 0; index--)
                {
                    PendingEntry entry = _pending[index];
                    if (entry.Owner.TryGetTarget(out _))
                        continue;
                    _pending.RemoveAt(index);
                    entry.WaiterHandles = 0;
                    (abandoned ??= []).Add(entry);
                }
            }

            if (abandoned is null)
                return;
            foreach (PendingEntry entry in abandoned)
            {
                Interlocked.Exchange(ref entry.Removed, 1);
                DisposePromotionRegistration(entry);
                if (entry.WaiterStartState == 2)
                {
                    // The running waiter observes this shared cancellation
                    // task in its terminal drain before it disposes the CTS.
                    _ = RequestPendingCancellation(entry);
                }
                else
                {
                    CancelWithoutWaiter(entry);
                }
            }
        }

        private async Task DrainUnstartedWaiterAsync(PendingEntry entry)
        {
            try { await RequestPendingCancellation(entry).ConfigureAwait(false); }
            catch (Exception exception)
            {
                MarkdownDiagnostics.WriteLine(
                    $"[SvgAdmission] unstarted waiter drain failed: {exception.GetType().Name}");
            }

            try { await WaitForRegistrationsToDrainAsync(entry).ConfigureAwait(false); }
            catch (Exception exception)
            {
                MarkdownDiagnostics.WriteLine(
                    $"[SvgAdmission] unstarted registration drain failed: {exception.GetType().Name}");
            }

            Volatile.Write(ref entry.WaiterCompletionStarted, 1);
            try
            {
                if (entry.Owner.TryGetTarget(out ImageBox? owner))
                {
                    owner.OnSvgHostAdmissionWaiterFinished(
                        entry.Demand,
                        entry.LoadGeneration,
                        entry.Identity);
                }
            }
            catch (Exception exception)
            {
                MarkdownDiagnostics.WriteLine(
                    $"[SvgAdmission] unstarted waiter completion callback failed: {exception.GetType().Name}");
            }
            finally
            {
                entry.Cancellation.Dispose();
            }
        }

        private async Task WaitAndResumeAsync(PendingEntry entry)
        {
            Task? capacityWait = null;
            try
            {
                while (true)
                {
                    if (!TryGetPendingWaitSnapshot(
                            entry,
                            out IMarkdownSvgAdmission? admission,
                            out long requiredSourceBytes,
                            out long ownerVersion,
                            out Task? registrationsDrained))
                    {
                        return;
                    }

                    if (registrationsDrained is not null)
                    {
                        await registrationsDrained.WaitAsync(entry.Cancellation.Token)
                            .ConfigureAwait(false);
                        continue;
                    }

                    if (entry.Demand.Version > entry.DemandVersionAtDenial &&
                        TryClaimPending(entry, sourceThresholdReached: false, awaitedSourceBytes: 0,
                            out bool promoted, out bool thresholdReady, out bool ownerReady))
                    {
                        SignalDeferredOwner(entry, admission, promoted, thresholdReady, ownerReady);
                        return;
                    }

                    if (admission is null && !entry.RequiresOwnerSlot)
                        return;

                    Task primaryChanged = entry.RequiresOwnerSlot
                        ? WaitForOwnerVersionChangeAsync(ownerVersion, entry.Cancellation.Token)
                        : admission!.WaitForCapacityAsync(
                            requiredSourceBytes,
                            entry.Demand.Priority,
                            entry.Cancellation.Token);
                    capacityWait = primaryChanged;

                    // The provider task is a source-free threshold signal. Do
                    // not keep a strong renderer/provider alias in this parked
                    // row while waiting; the registry row itself remains weak.
                    admission = null;
                    Task completed = await Task.WhenAny(primaryChanged, entry.Promotion.Task)
                        .WaitAsync(entry.Cancellation.Token)
                        .ConfigureAwait(false);
                    if (entry.Cancellation.IsCancellationRequested)
                        return;

                    if (ReferenceEquals(completed, entry.Promotion.Task))
                    {
                        // Promotion changes the provider tier. Claim it as a
                        // one-shot demand event after registration drains; if a
                        // duplicate registration is in flight, the next loop
                        // waits for that drain before claiming.
                        if (entry.Demand.Version > entry.DemandVersionAtDenial)
                        {
                            if (TryClaimPending(entry, sourceThresholdReached: false,
                                    awaitedSourceBytes: 0, out promoted, out thresholdReady, out ownerReady))
                            {
                                entry.Admission.TryGetTarget(out admission);
                                SignalDeferredOwner(entry, admission, promoted, thresholdReady, ownerReady);
                                return;
                            }
                            continue;
                        }

                        // A malformed promotion notification must not create a
                        // completed-task spin. Continue waiting for the primary
                        // source/owner condition instead.
                        await primaryChanged.WaitAsync(entry.Cancellation.Token).ConfigureAwait(false);
                    }
                    else
                    {
                        // Observe wait faults instead of leaving a detached
                        // faulted task behind after the primary signal wins.
                        await completed.ConfigureAwait(false);
                    }

                    capacityWait = null;

                    if (entry.RequiresOwnerSlot)
                    {
                        if (TryClaimPending(entry, sourceThresholdReached: false,
                                awaitedSourceBytes: 0, out promoted, out thresholdReady, out ownerReady))
                        {
                            entry.Admission.TryGetTarget(out admission);
                            SignalDeferredOwner(entry, admission, promoted, thresholdReady, ownerReady);
                            return;
                        }
                        continue;
                    }

                    // A small threshold may wake this task after a duplicate
                    // denial raised the entry's monotonic maximum. The claim
                    // rechecks that maximum under _gate; if it grew, the next
                    // iteration waits for the larger requirement (which is
                    // immediately observable if it already fits).
                    if (TryClaimPending(entry, sourceThresholdReached: true,
                            awaitedSourceBytes: requiredSourceBytes,
                            out promoted, out thresholdReady, out ownerReady))
                    {
                        entry.Admission.TryGetTarget(out admission);
                        SignalDeferredOwner(entry, admission, promoted, thresholdReady, ownerReady);
                        return;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Cancellation removes only this source-free waiter.
            }
            catch (Exception exception)
            {
                MarkdownDiagnostics.WriteLine(
                    $"[SvgAdmission] capacity wait failed: {exception.GetType().Name}");
            }
            finally
            {
                RemovePending(entry);
                Volatile.Write(ref entry.WaiterCompletionStarted, 1);
                try
                {
                    if (entry.Owner.TryGetTarget(out ImageBox? owner))
                    {
                        owner.OnSvgHostAdmissionWaiterFinished(
                            entry.Demand,
                            entry.LoadGeneration,
                            entry.Identity);
                    }
                }
                catch (Exception exception)
                {
                    MarkdownDiagnostics.WriteLine(
                        $"[SvgAdmission] waiter completion callback failed: {exception.GetType().Name}");
                }
                finally
                {
                    await FinishPendingWaiterAsync(entry, capacityWait).ConfigureAwait(false);
                }
            }
        }

        private bool TryGetPendingWaitSnapshot(
            PendingEntry entry,
            out IMarkdownSvgAdmission? admission,
            out long requiredSourceBytes,
            out long ownerVersion,
            out Task? registrationsDrained)
        {
            lock (_gate)
            {
                admission = null;
                requiredSourceBytes = 0;
                ownerVersion = 0;
                registrationsDrained = null;
                if (!_pending.Contains(entry) || Volatile.Read(ref entry.Removed) != 0 ||
                    Volatile.Read(ref entry.Claimed) != 0)
                {
                    return false;
                }

                if (entry.RegistrationsInFlight != 0)
                {
                    registrationsDrained = entry.RegistrationsDrained?.Task
                        ?? throw new InvalidOperationException(
                            "An in-flight SVG waiter registration has no drain signal.");
                    return true;
                }

                if (!entry.RequiresOwnerSlot &&
                    !entry.Admission.TryGetTarget(out admission))
                {
                    return false;
                }

                requiredSourceBytes = entry.RequiredSourceReservationBytes;
                ownerVersion = entry.OwnerVersion;
                return true;
            }
        }

        private bool TryClaimPending(
            PendingEntry entry,
            bool sourceThresholdReached,
            long awaitedSourceBytes,
            out bool promotionObserved,
            out bool thresholdReady,
            out bool ownerObserved)
        {
            lock (_gate)
            {
                promotionObserved = false;
                thresholdReady = false;
                ownerObserved = false;
                if (!_pending.Contains(entry) || Volatile.Read(ref entry.Removed) != 0 ||
                    Volatile.Read(ref entry.Claimed) != 0 || entry.RegistrationsInFlight != 0)
                {
                    return false;
                }

                promotionObserved = entry.Demand.Version > entry.DemandVersionAtDenial;
                thresholdReady = sourceThresholdReached && !entry.RequiresOwnerSlot &&
                    entry.RequiredSourceReservationBytes <= awaitedSourceBytes;
                ownerObserved = entry.RequiresOwnerSlot && _ownerVersion > entry.OwnerVersion;
                if (!promotionObserved && !thresholdReady && !ownerObserved)
                    return false;

                // Eligibility and removal are one transaction. A concurrent
                // duplicate threshold increase either precedes this lock and is
                // observed above, or sees the row already removed and creates
                // a new identity instead of updating a claimed entry.
                Volatile.Write(ref entry.Claimed, 1);
                _pending.Remove(entry);
                return true;
            }
        }

        private void SignalDeferredOwner(
            PendingEntry entry,
            IMarkdownSvgAdmission? admission,
            bool promotionObserved,
            bool capacityThresholdSatisfied,
            bool ownerObserved)
        {
            if (!entry.Owner.TryGetTarget(out ImageBox? owner))
                return;

            long observedCapacityVersion = 0;
            if (admission is not null)
            {
                try { observedCapacityVersion = admission.CapacityVersion; }
                catch { /* Threshold readiness is authoritative even if diagnostics are unavailable. */ }
            }

            owner.TryQueueDeferredSvgHostRetry(
                entry.Demand,
                entry.LoadGeneration,
                promotionObserved ? entry.Demand.Version : 0,
                observedCapacityVersion,
                ownerObserved ? OwnerVersion : 0,
                capacityThresholdSatisfied: capacityThresholdSatisfied,
                waiterIdentity: entry.Identity);
        }

        private void RemovePending(PendingEntry entry)
        {
            lock (_gate)
                _pending.Remove(entry);
        }

        private static void DisposePromotionRegistration(PendingEntry entry)
        {
            IDisposable? registration = Interlocked.Exchange(ref entry.PromotionRegistration, null);
            try { registration?.Dispose(); }
            catch (Exception exception)
            {
                MarkdownDiagnostics.WriteLine(
                    $"[SvgAdmission] promotion retirement failed: {exception.GetType().Name}");
            }
        }

        private static Task RequestPendingCancellation(PendingEntry entry)
        {
            Interlocked.Exchange(ref entry.Removed, 1);
            DisposePromotionRegistration(entry);
            lock (entry.CancellationSync)
            {
                if (entry.CancellationCallbacks is { } existing)
                    return existing;
                try
                {
                    entry.CancellationCallbacks = entry.Cancellation.CancelAsync();
                    return entry.CancellationCallbacks;
                }
                catch (Exception exception)
                {
                    MarkdownDiagnostics.WriteLine(
                        $"[SvgAdmission] waiter cancellation failed: {exception.GetType().Name}");
                    return Task.FromException(exception);
                }
            }
        }

        private async Task FinishPendingWaiterAsync(PendingEntry entry, Task? capacityWait)
        {
            if (Interlocked.Exchange(ref entry.WaiterFinished, 1) != 0)
                return;
            Interlocked.Exchange(ref entry.Removed, 1);
            DisposePromotionRegistration(entry);
            try
            {
                await RequestPendingCancellation(entry).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                MarkdownDiagnostics.WriteLine(
                    $"[SvgAdmission] waiter drain failed: {exception.GetType().Name}");
            }

            // The entry was removed before this final drain, so no later
            // Defer can add another registration. Keep the linked CTS alive
            // until every already-published ImageBox registration callback
            // has left its lock/callback path.
            try { await WaitForRegistrationsToDrainAsync(entry).ConfigureAwait(false); }
            catch (Exception exception)
            {
                MarkdownDiagnostics.WriteLine(
                    $"[SvgAdmission] registration drain failed: {exception.GetType().Name}");
            }

            finally
            {
                if (capacityWait is null || capacityWait.IsCompleted)
                {
                    ObserveFault(capacityWait);
                    entry.Cancellation.Dispose();
                }
                else
                {
                    // A provider may violate cancellation and leave its
                    // version wait incomplete. Do not block owner retirement;
                    // retain the CTS until that wait actually drains, while
                    // observing a later fault to avoid a detached exception.
                    _ = capacityWait.ContinueWith(
                        static (completed, state) =>
                        {
                            ObserveFault(completed);
                            ((CancellationTokenSource)state!).Dispose();
                        },
                        entry.Cancellation,
                        CancellationToken.None,
                        TaskContinuationOptions.ExecuteSynchronously,
                        TaskScheduler.Default);
                }
            }
        }

        private async Task WaitForRegistrationsToDrainAsync(PendingEntry entry)
        {
            while (true)
            {
                Task? drained;
                lock (_gate)
                {
                    drained = entry.RegistrationsInFlight == 0
                        ? null
                        : entry.RegistrationsDrained?.Task
                            ?? throw new InvalidOperationException(
                                "An in-flight SVG waiter registration has no drain signal.");
                }

                if (drained is null)
                    return;
                await drained.ConfigureAwait(false);
            }
        }

        private static void ObserveFault(Task? task)
        {
            if (task is { IsFaulted: true })
                _ = task.Exception;
        }

        private PendingEntry? FindPendingNoLock(
            ImageBox owner,
            int loadGeneration,
            MarkdownSvgRenderDemand demand)
        {
            foreach (PendingEntry pending in _pending)
            {
                if (Volatile.Read(ref pending.Removed) == 0 &&
                    Volatile.Read(ref pending.Claimed) == 0 &&
                    pending.LoadGeneration == loadGeneration &&
                    ReferenceEquals(pending.Demand, demand) &&
                    pending.Owner.TryGetTarget(out ImageBox? current) &&
                    ReferenceEquals(current, owner))
                {
                    return pending;
                }
            }
            return null;
        }

        internal void TryParkForAdmission(
            long requiredSourceReservationBytes,
            MarkdownSvgRenderPriority requesterPriority,
            bool requiresOwnerSlot)
        {
            TryParkOwners(
                requiredSourceReservationBytes,
                requesterPriority,
                requiresOwnerSlot,
                MaximumVictimsPerAdmission,
                pendingEntry: null);
        }

        private void TryParkForPendingDemand(PendingEntry entry)
        {
            while (true)
            {
                long requiredBytes;
                long requiredBytesAtStart;
                long alreadyScheduledBytes;
                int remainingVictims;
                lock (_gate)
                {
                    if (!_pending.Contains(entry) || Volatile.Read(ref entry.Removed) != 0 ||
                        entry.ParkingInProgress)
                    {
                        return;
                    }

                    requiredBytes = entry.RequiredSourceReservationBytes;
                    requiredBytesAtStart = requiredBytes;
                    alreadyScheduledBytes = entry.ParkedSourceBytes;
                    remainingVictims = entry.RequiresOwnerSlot
                        ? Math.Max(0, 1 - entry.ParkedVictims)
                        : MaximumVictimsPerAdmission - entry.ParkedVictims;
                    if (alreadyScheduledBytes >= requiredBytes || remainingVictims <= 0)
                        return;

                    entry.ParkingInProgress = true;
                }

                long scheduledBytes = 0;
                int scheduledVictims = 0;
                try
                {
                    long deficit = requiredBytes - alreadyScheduledBytes;
                    (scheduledBytes, scheduledVictims) = TryParkOwners(
                        deficit,
                        entry.Demand.Priority,
                        requiresOwnerSlot: entry.RequiresOwnerSlot,
                        maximumVictims: remainingVictims,
                        pendingEntry: entry);
                }
                catch (Exception exception)
                {
                    // Pressure parking only retires independently owned
                    // documents. Failure here must leave the transient denial
                    // source-free and eligible for the normal threshold wait.
                    MarkdownDiagnostics.WriteLine(
                        $"[SvgAdmission] pressure park failed safely: {exception.GetType().Name}");
                }

                bool tryAnotherPass;
                lock (_gate)
                {
                    entry.ParkedSourceBytes = SaturatingAdd(entry.ParkedSourceBytes, scheduledBytes);
                    entry.ParkedVictims = Math.Min(
                        MaximumVictimsPerAdmission,
                        entry.ParkedVictims + scheduledVictims);
                    entry.ParkingInProgress = false;

                    bool thresholdGrewDuringPass =
                        entry.RequiredSourceReservationBytes > requiredBytesAtStart;
                    tryAnotherPass = (entry.RequiresOwnerSlot
                        ? thresholdGrewDuringPass && entry.ParkedVictims < 1
                        : scheduledVictims > 0 || thresholdGrewDuringPass) &&
                        _pending.Contains(entry) && Volatile.Read(ref entry.Removed) == 0 &&
                        entry.ParkedVictims < (entry.RequiresOwnerSlot ? 1 : MaximumVictimsPerAdmission) &&
                        entry.ParkedSourceBytes < entry.RequiredSourceReservationBytes;
                }

                // A duplicate may raise the required max while this pass is
                // invoking owners. Recompute after progress, or once when the
                // accepted threshold grew during this pass. A no-progress pass
                // with unchanged demand cannot spin.
                if (!tryAnotherPass)
                    return;
            }
        }

        private (long ScheduledBytes, int ScheduledVictims) TryParkOwners(
            long requiredSourceReservationBytes,
            MarkdownSvgRenderPriority requesterPriority,
            bool requiresOwnerSlot,
            int maximumVictims,
            PendingEntry? pendingEntry)
        {
            if (maximumVictims <= 0)
                return (0, 0);

            PruneDeadOwners();
            (OwnerEntry Entry, long LastUse)[] owners;
            lock (_gate)
            {
                owners = new (OwnerEntry Entry, long LastUse)[_owners.Count];
                for (int index = 0; index < _owners.Count; index++)
                {
                    OwnerEntry entry = _owners[index];
                    owners[index] = (entry, entry.LastUse);
                }
            }

            var candidates = new List<(OwnerEntry Entry, ImageBox Owner,
                ImageBox.SvgTileRenderPlan Plan, long DemandVersion, int Tier, long LastUse)>();
            foreach ((OwnerEntry entry, long lastUse) in owners)
            {
                long demandVersion = entry.Demand.Version;
                if (!entry.Owner.TryGetTarget(out ImageBox? owner) ||
                    !entry.Plan.TryGetTarget(out ImageBox.SvgTileRenderPlan? plan) ||
                    !owner.TryGetSvgPressureParkTier(plan, demandVersion, out int tier))
                {
                    continue;
                }
                if (requesterPriority == MarkdownSvgRenderPriority.Overscan && tier != 0)
                    continue;
                candidates.Add((entry, owner, plan, demandVersion, tier, lastUse));
            }

            candidates.Sort(static (left, right) =>
                CompareParkCandidateOrder(left.Tier, left.LastUse, right.Tier, right.LastUse));

            long scheduledBytes = 0;
            int victims = 0;
            int maximumParkTier = requesterPriority == MarkdownSvgRenderPriority.Overscan ? 0 : 1;
            foreach ((OwnerEntry entry, ImageBox owner, ImageBox.SvgTileRenderPlan plan,
                long demandVersion, _, _) in candidates)
            {
                if (victims >= maximumVictims ||
                    (requiresOwnerSlot && victims >= 1) ||
                    (!requiresOwnerSlot && scheduledBytes >= requiredSourceReservationBytes))
                {
                    break;
                }

                if (pendingEntry is not null && !IsPendingForParking(pendingEntry))
                    break;

                bool parked;
                try { parked = owner.TryParkSvgDocument(plan, demandVersion, maximumParkTier); }
                catch (Exception exception)
                {
                    MarkdownDiagnostics.WriteLine(
                        $"[SvgAdmission] owner pressure park failed safely: {exception.GetType().Name}");
                    continue;
                }
                if (!parked)
                    continue;
                scheduledBytes = SaturatingAdd(scheduledBytes, entry.SourceReservationBytes);
                victims++;
            }

            return (scheduledBytes, victims);
        }

        private bool IsPendingForParking(PendingEntry entry)
        {
            lock (_gate)
            {
                return _pending.Contains(entry) && Volatile.Read(ref entry.Removed) == 0 &&
                    Volatile.Read(ref entry.Claimed) == 0;
            }
        }

        private void PruneDeadOwners()
        {
            TaskCompletionSource<long>? changed = null;
            long version = 0;
            lock (_gate)
            {
                bool removed = false;
                for (int index = _owners.Count - 1; index >= 0; index--)
                {
                    OwnerEntry entry = _owners[index];
                    if (entry.Owner.TryGetTarget(out _) && entry.Plan.TryGetTarget(out _))
                        continue;
                    _owners.RemoveAt(index);
                    removed = true;
                }

                if (removed)
                {
                    version = ++_ownerVersion;
                    changed = _ownerVersionChanged;
                    _ownerVersionChanged = null;
                }
            }

            // Slot-change waiters are always signaled after the registry lock.
            changed?.TrySetResult(version);
        }

        private static long SaturatingAdd(long value, long addend) =>
            value > long.MaxValue - addend ? long.MaxValue : value + addend;
    }

    internal static OwnerSlot? TryReserveOwnerSlot(
        object admissionDomain,
        MarkdownSvgRenderDemand demand,
        out long ownerVersionAtDenial)
    {
        ArgumentNullException.ThrowIfNull(admissionDomain);
        ArgumentNullException.ThrowIfNull(demand);
        DomainState domain = s_domains.GetValue(admissionDomain, static _ => new DomainState());
        OwnerSlot? slot = domain.TryReserveOwnerSlot(admissionDomain, out ownerVersionAtDenial);
        if (slot is not null)
            return slot;

        domain.TryParkForAdmission(1, demand.Priority, requiresOwnerSlot: true);
        return domain.TryReserveOwnerSlot(admissionDomain, out ownerVersionAtDenial);
    }

    internal static PendingWaiter? Defer(
        ImageBox owner,
        IMarkdownSvgAdmission admission,
        MarkdownSvgRenderDemand demand,
        int loadGeneration,
        long capacityVersion,
        long requiredSourceReservationBytes,
        bool requiresOwnerSlot,
        long ownerVersionAtDenial,
        long demandVersionAtDenial,
        long visualDemandEpochAtDenial,
        object registrationAttempt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(admission);
        ArgumentNullException.ThrowIfNull(demand);
        if (requiredSourceReservationBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(requiredSourceReservationBytes));
        object domainKey = admission.AdmissionDomain;
        DomainState domain = s_domains.GetValue(domainKey, static _ => new DomainState());
        return domain.Defer(
            owner,
            admission,
            demand,
            loadGeneration,
            capacityVersion,
            requiredSourceReservationBytes,
            requiresOwnerSlot,
            ownerVersionAtDenial,
            demandVersionAtDenial,
            visualDemandEpochAtDenial,
            registrationAttempt,
            cancellationToken);
    }

    internal static long GetOwnerVersion(object admissionDomain) =>
        s_domains.GetValue(admissionDomain, static _ => new DomainState()).GetOwnerVersion();

    internal static void Touch(ImageBox.SvgTileRenderPlan plan) => plan.OwnerSlot?.Touch();

    private static TaskCompletionSource<long> NewVersionSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}
