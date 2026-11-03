using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MarkdownRenderer.Diagnostics;
using MarkdownRenderer.Images;

namespace MarkdownRenderer.Layout.Boxes;

/// <summary>
/// Host-side admission state of one provider admission domain: its pending
/// deferred demands and their waiters, and the accepted owners it may park
/// to make room for them.
/// </summary>
internal sealed class SvgHostAdmissionDomain
{
    private const int MaximumPendingDemandsPerDomain = 128;
    private const int MaximumVictimsPerAdmission = 8;

    private readonly object _gate = new();
    private readonly List<SvgHostPendingEntry> _pending = new(MaximumPendingDemandsPerDomain);

    /// <summary>The accepted owners of this domain.</summary>
    internal SvgHostOwnerRegistry Owners { get; } = new();

    internal SvgHostPendingWaiter? Defer(
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
        SvgHostPendingEntry? entry;
        bool thresholdIncreased = false;
        SvgHostPendingWaiter waiter;
        lock (_gate)
        {
            entry = FindPendingNoLock(owner, loadGeneration, demand);
            if (entry is not null)
            {
                waiter = new SvgHostPendingWaiter(this, entry);
                BeginRegistrationNoLock(entry, proposedRegistrationsDrained);
                entry.WaiterHandles++;
            }
            else
            {
                if (_pending.Count >= MaximumPendingDemandsPerDomain)
                    return null;

                entry = new SvgHostPendingEntry(
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
                    waiter = new SvgHostPendingWaiter(this, entry);
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
        SvgHostPendingEntry entry,
        TaskCompletionSource proposedRegistrationsDrained)
    {
        if (entry.RegistrationsInFlight == 0)
            entry.RegistrationsDrained = proposedRegistrationsDrained;
        else if (entry.RegistrationsDrained is null)
            throw new InvalidOperationException("An in-flight SVG waiter registration has no drain signal.");

        entry.RegistrationsInFlight++;
    }

    private TaskCompletionSource? EndRegistration(
        SvgHostPendingEntry entry,
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

    private bool TryClaimPendingWaiterStart(SvgHostPendingEntry entry)
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

    private bool IsPendingEntry(SvgHostPendingEntry entry)
    {
        lock (_gate)
            return _pending.Contains(entry) && Volatile.Read(ref entry.Removed) == 0;
    }

    private bool StartPendingWaiter(SvgHostPendingEntry entry)
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

    internal void ReleaseWaiterHandle(SvgHostPendingEntry entry)
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

    private void CancelWithoutWaiter(SvgHostPendingEntry entry)
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
        List<SvgHostPendingEntry>? abandoned = null;
        lock (_gate)
        {
            for (int index = _pending.Count - 1; index >= 0; index--)
            {
                SvgHostPendingEntry entry = _pending[index];
                if (entry.Owner.TryGetTarget(out _))
                    continue;
                _pending.RemoveAt(index);
                entry.WaiterHandles = 0;
                (abandoned ??= []).Add(entry);
            }
        }

        if (abandoned is null)
            return;
        foreach (SvgHostPendingEntry entry in abandoned)
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

    private async Task DrainUnstartedWaiterAsync(SvgHostPendingEntry entry)
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

    private async Task WaitAndResumeAsync(SvgHostPendingEntry entry)
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
                    ? Owners.WaitForOwnerVersionChangeAsync(ownerVersion, entry.Cancellation.Token)
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
        SvgHostPendingEntry entry,
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
        SvgHostPendingEntry entry,
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
            ownerObserved = entry.RequiresOwnerSlot && Owners.OwnerVersion > entry.OwnerVersion;
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
        SvgHostPendingEntry entry,
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
            ownerObserved ? Owners.OwnerVersion : 0,
            capacityThresholdSatisfied: capacityThresholdSatisfied,
            waiterIdentity: entry.Identity);
    }

    private void RemovePending(SvgHostPendingEntry entry)
    {
        lock (_gate)
            _pending.Remove(entry);
    }

    private static void DisposePromotionRegistration(SvgHostPendingEntry entry)
    {
        IDisposable? registration = Interlocked.Exchange(ref entry.PromotionRegistration, null);
        try { registration?.Dispose(); }
        catch (Exception exception)
        {
            MarkdownDiagnostics.WriteLine(
                $"[SvgAdmission] promotion retirement failed: {exception.GetType().Name}");
        }
    }

    private static Task RequestPendingCancellation(SvgHostPendingEntry entry)
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

    private async Task FinishPendingWaiterAsync(SvgHostPendingEntry entry, Task? capacityWait)
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

    private async Task WaitForRegistrationsToDrainAsync(SvgHostPendingEntry entry)
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

    private SvgHostPendingEntry? FindPendingNoLock(
        ImageBox owner,
        int loadGeneration,
        MarkdownSvgRenderDemand demand)
    {
        foreach (SvgHostPendingEntry pending in _pending)
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
        Owners.TryParkOwners(
            requiredSourceReservationBytes,
            requesterPriority,
            requiresOwnerSlot,
            MaximumVictimsPerAdmission,
            isStillPending: null);
    }

    private void TryParkForPendingDemand(SvgHostPendingEntry entry)
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
                (scheduledBytes, scheduledVictims) = Owners.TryParkOwners(
                    deficit,
                    entry.Demand.Priority,
                    requiresOwnerSlot: entry.RequiresOwnerSlot,
                    maximumVictims: remainingVictims,
                    isStillPending: () => IsPendingForParking(entry));
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

    private bool IsPendingForParking(SvgHostPendingEntry entry)
    {
        lock (_gate)
        {
            return _pending.Contains(entry) && Volatile.Read(ref entry.Removed) == 0 &&
                Volatile.Read(ref entry.Claimed) == 0;
        }
    }

    private static long SaturatingAdd(long value, long addend) =>
        value > long.MaxValue - addend ? long.MaxValue : value + addend;
}
