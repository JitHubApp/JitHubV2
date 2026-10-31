using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using MarkdownRenderer.Images;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using Microsoft.UI.Xaml;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

// These tests exercise managed registry ownership only. Bare boxes are weak
// identity tokens; only managed deferred-ownership callbacks are called, never
// layout, paint, WinUI, provider rendering or device methods.
[Collection(AllocationSensitiveContractCollection.Name)]
public sealed class SvgHostAdmissionCoordinatorTests
{
    [Fact]
    public async Task ReservedOwnerPopulationIsHardBoundedAndReturnedSlotsAreReusable()
    {
        var domain = new SvgHostAdmissionCoordinator.DomainState();
        var identity = new object();
        var slots = new List<SvgHostAdmissionCoordinator.OwnerSlot>();
        try
        {
            for (int i = 0; i < 512; i++)
                slots.Add(Assert.IsType<SvgHostAdmissionCoordinator.OwnerSlot>(
                    Reserve(domain, identity, out _)));

            Assert.Null(Reserve(domain, identity, out long deniedVersion));
            Assert.Equal(512, ReservedCount(domain));
            Assert.Equal(0, OwnerCount(domain));
            Task changed = domain.WaitForOwnerVersionChangeAsync(deniedVersion, CancellationToken.None);
            Assert.False(changed.IsCompleted);

            slots[173].Dispose();
            slots[173].Dispose();
            await changed.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(511, ReservedCount(domain));
            using SvgHostAdmissionCoordinator.OwnerSlot replacement =
                Assert.IsType<SvgHostAdmissionCoordinator.OwnerSlot>(
                    Reserve(domain, identity, out long admittedVersion));
            Assert.True(admittedVersion > deniedVersion);
            Assert.Null(Reserve(domain, identity, out _));
        }
        finally
        {
            foreach (SvgHostAdmissionCoordinator.OwnerSlot slot in slots)
                slot.Dispose();
        }
        Assert.Equal(0, ReservedCount(domain));
        Assert.Equal(0, OwnerCount(domain));
    }

    [Fact]
    public async Task ReleaseBetweenDenialAndSubscriptionCannotLoseTheCapacityWakeup()
    {
        var domain = new SvgHostAdmissionCoordinator.DomainState();
        var identity = new object();
        var slots = new List<SvgHostAdmissionCoordinator.OwnerSlot>();
        try
        {
            for (int i = 0; i < 512; i++)
                slots.Add(Assert.IsType<SvgHostAdmissionCoordinator.OwnerSlot>(
                    Reserve(domain, identity, out _)));
            Assert.Null(Reserve(domain, identity, out long deniedVersion));

            slots[0].Dispose();
            Task changed = domain.WaitForOwnerVersionChangeAsync(deniedVersion, CancellationToken.None);
            Assert.True(changed.IsCompletedSuccessfully);
            await changed;
        }
        finally
        {
            foreach (SvgHostAdmissionCoordinator.OwnerSlot slot in slots)
                slot.Dispose();
        }
    }

    [Fact]
    public async Task CommittingAndDisposingTheSameSlotCannotLeaveAPublishedOwnerBehind()
    {
        var domain = new SvgHostAdmissionCoordinator.DomainState();
        using SvgHostAdmissionCoordinator.OwnerSlot slot =
            Assert.IsType<SvgHostAdmissionCoordinator.OwnerSlot>(
                Reserve(domain, new object(), out _));
        ImageBox owner = BareBox();
        var demand = new MarkdownSvgRenderDemand(MarkdownSvgRenderPriority.Overscan);
        var plan = new ImageBox.SvgTileRenderPlan(
            new RegistryDocument(), "registry-race", 2048, 2048, 1,
            demand, sourceReservationBytes: 128, ownerSlot: slot);
        object domainGate = Field<object>(domain, "_gate");
        object slotGate = Field<object>(slot, "_sync");
        Task commit;
        Task dispose;
        Monitor.Enter(domainGate);
        try
        {
            commit = Task.Run(() => slot.Commit(owner, plan, demand, 128));
            // Holding the domain gate pauses Commit after it has acquired the
            // slot gate but before it publishes the owner entry. A concurrent
            // Dispose must not observe a half-committed slot in this window.
            Assert.True(SpinWait.SpinUntil(() =>
            {
                if (!Monitor.TryEnter(slotGate))
                    return true;
                Monitor.Exit(slotGate);
                return false;
            }, TimeSpan.FromSeconds(5)));
            using var started = new ManualResetEventSlim();
            dispose = Task.Run(() => { started.Set(); slot.Dispose(); });
            Assert.True(started.Wait(TimeSpan.FromSeconds(5)));
            Assert.False(dispose.IsCompleted);
        }
        finally { Monitor.Exit(domainGate); }

        await Task.WhenAll(commit, dispose).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, OwnerCount(domain));
        Assert.Equal(0, ReservedCount(domain));
        slot.Dispose();
        Assert.Equal(0, OwnerCount(domain));
        GC.KeepAlive(owner);
        GC.KeepAlive(plan);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PruningADeadWeakOwnerOrPlanSignalsExistingCapacityWaiters(bool collectPlan)
    {
        var domain = new SvgHostAdmissionCoordinator.DomainState();
        using SvgHostAdmissionCoordinator.OwnerSlot slot =
            Assert.IsType<SvgHostAdmissionCoordinator.OwnerSlot>(
                Reserve(domain, new object(), out _));
        (WeakReference weak, object retained) = CommitWeakOwner(domain, slot, collectPlan);
        Assert.Equal(1, OwnerCount(domain));
        long observed = domain.OwnerVersion;
        Task changed = domain.WaitForOwnerVersionChangeAsync(observed, CancellationToken.None);
        Assert.False(changed.IsCompleted);

        for (int i = 0; i < 10 && weak.IsAlive; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
        Assert.False(weak.IsAlive);
        // Admission prunes dead entries before consulting its hard slot cap.
        using SvgHostAdmissionCoordinator.OwnerSlot next =
            Assert.IsType<SvgHostAdmissionCoordinator.OwnerSlot>(
                Reserve(domain, new object(), out _));
        await changed.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(domain.OwnerVersion > observed);
        Assert.Equal(0, OwnerCount(domain));
        Assert.Equal(1, ReservedCount(domain));
        GC.KeepAlive(retained);
    }

    [Fact]
    public void DisposedReservationCannotBeCommittedOrReturnAnotherOwnersCapacity()
    {
        var domain = new SvgHostAdmissionCoordinator.DomainState();
        using SvgHostAdmissionCoordinator.OwnerSlot retired =
            Assert.IsType<SvgHostAdmissionCoordinator.OwnerSlot>(
                Reserve(domain, new object(), out _));
        using SvgHostAdmissionCoordinator.OwnerSlot active =
            Assert.IsType<SvgHostAdmissionCoordinator.OwnerSlot>(
                Reserve(domain, new object(), out _));
        retired.Dispose();
        ImageBox owner = BareBox();
        var demand = new MarkdownSvgRenderDemand(MarkdownSvgRenderPriority.Visible);
        var plan = new ImageBox.SvgTileRenderPlan(new RegistryDocument(), "retired", 2048, 2048, 1);
        Assert.Throws<InvalidOperationException>(() => retired.Commit(owner, plan, demand, 128));
        retired.Dispose();
        Assert.Equal(1, ReservedCount(domain));
        Assert.Equal(0, OwnerCount(domain));
        GC.KeepAlive(owner);
    }

    [Fact]
    public void RetirementOfCommittedAndReservedOwnersDoesNotAllocateWithoutWaiters()
    {
        var domain = new SvgHostAdmissionCoordinator.DomainState();
        var identity = new object();
        var demand = new MarkdownSvgRenderDemand(MarkdownSvgRenderPriority.Overscan);
        using (SvgHostAdmissionCoordinator.OwnerSlot warm =
            Assert.IsType<SvgHostAdmissionCoordinator.OwnerSlot>(Reserve(domain, identity, out _)))
        {
            ImageBox owner = BareBox();
            var plan = new ImageBox.SvgTileRenderPlan(new RegistryDocument(), "warm-release", 2048, 2048, 1);
            warm.Commit(owner, plan, demand, 64);
            warm.Dispose();
            GC.KeepAlive(owner);
            GC.KeepAlive(plan);
        }

        var slots = new SvgHostAdmissionCoordinator.OwnerSlot[512];
        var owners = new ImageBox[256];
        var plans = new ImageBox.SvgTileRenderPlan[256];
        try
        {
            for (int i = 0; i < slots.Length; i++)
            {
                slots[i] = Assert.IsType<SvgHostAdmissionCoordinator.OwnerSlot>(Reserve(domain, identity, out _));
                if (i < owners.Length)
                {
                    owners[i] = BareBox();
                    plans[i] = new ImageBox.SvgTileRenderPlan(new RegistryDocument(), "release", 2048, 2048, 1);
                    slots[i].Commit(owners[i], plans[i], demand, 64);
                }
            }
            long before = GC.GetAllocatedBytesForCurrentThread();
            foreach (SvgHostAdmissionCoordinator.OwnerSlot slot in slots)
                slot.Dispose();
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.Equal(0, allocated);
            Assert.Equal(0, OwnerCount(domain));
            Assert.Equal(0, ReservedCount(domain));
        }
        finally
        {
            foreach (SvgHostAdmissionCoordinator.OwnerSlot? slot in slots)
                slot?.Dispose();
            GC.KeepAlive(owners);
            GC.KeepAlive(plans);
        }
    }

    [Fact]
    public void CapturedVictimRecencyRemainsStableAfterLiveOwnerTouches()
    {
        var domain = new SvgHostAdmissionCoordinator.DomainState();
        using SvgHostAdmissionCoordinator.OwnerSlot slot =
            Assert.IsType<SvgHostAdmissionCoordinator.OwnerSlot>(Reserve(domain, new object(), out _));
        ImageBox owner = BareBox();
        var demand = new MarkdownSvgRenderDemand(MarkdownSvgRenderPriority.Overscan);
        var plan = new ImageBox.SvgTileRenderPlan(new RegistryDocument(), "recency", 2048, 2048, 1);
        var older = new SvgHostAdmissionCoordinator.OwnerEntry(owner, plan, demand, 64, slot, 10);
        var newer = new SvgHostAdmissionCoordinator.OwnerEntry(owner, plan, demand, 64, slot, 20);
        long capturedOlder = older.LastUse;
        long capturedNewer = newer.LastUse;
        older.LastUse = 30;
        newer.LastUse = 0;

        Assert.True(SvgHostAdmissionCoordinator.DomainState.CompareParkCandidateOrder(
            0, capturedOlder, 0, capturedNewer) < 0);
        Assert.True(SvgHostAdmissionCoordinator.DomainState.CompareParkCandidateOrder(
            0, older.LastUse, 0, newer.LastUse) > 0);
        Assert.True(SvgHostAdmissionCoordinator.DomainState.CompareParkCandidateOrder(
            0, long.MaxValue, 1, long.MinValue) < 0);
        Assert.Equal(0, SvgHostAdmissionCoordinator.DomainState.CompareParkCandidateOrder(1, 20, 1, 20));

        GC.KeepAlive(owner);
        GC.KeepAlive(plan);
    }

    [Fact]
    public void LateFinishedWaiterCannotClearAReplacementForTheSameDemandAndGeneration()
    {
        ImageBox owner = BareBox();
        SetField(owner, "_svgHostDeferredSync", new object());
        var domain = new SvgHostAdmissionCoordinator.DomainState();
        var admission = new RegistryAdmission();
        var demand = new MarkdownSvgRenderDemand(MarkdownSvgRenderPriority.Overscan);
        const int generation = 41;
        SetField(owner, "_svgLoadDemandTicket", demand);
        SetField(owner, "_svgLoadDemandGeneration", generation);
        SetField(owner, "_svgHostDeferred", 1);
        using var lifetime = new CancellationTokenSource();
        var oldEntry = new SvgHostAdmissionCoordinator.PendingEntry(
            owner, admission, demand, generation, 0, 128, false, 0, 0, 0, lifetime.Token);
        var replacementEntry = new SvgHostAdmissionCoordinator.PendingEntry(
            owner, admission, demand, generation, 0, 128, false, 0, 0, 0, lifetime.Token);
        using var oldWaiter = new SvgHostAdmissionCoordinator.PendingWaiter(domain, oldEntry);
        using var replacement = new SvgHostAdmissionCoordinator.PendingWaiter(domain, replacementEntry);
        try
        {
            object oldAttempt = new();
            SetField(owner, "_svgHostDeferredRegistrationAttempt", oldAttempt);
            Assert.True(owner.OnSvgHostAdmissionWaiterRegistered(demand, generation, oldAttempt, oldWaiter));
            SetField(owner, "_svgHostDeferredWaiter", oldWaiter);
            owner.OnSvgHostAdmissionWaiterFinished(demand, generation, oldWaiter.Identity);
            Assert.Null(NullableField(owner, "_svgHostDeferredWaiter"));

            object newAttempt = new();
            SetField(owner, "_svgHostDeferredRegistrationAttempt", newAttempt);
            Assert.True(owner.OnSvgHostAdmissionWaiterRegistered(demand, generation, newAttempt, replacement));
            SetField(owner, "_svgHostDeferredWaiter", replacement);
            owner.OnSvgHostAdmissionWaiterFinished(demand, generation, oldWaiter.Identity);
            Assert.Same(replacement, NullableField(owner, "_svgHostDeferredWaiter"));
            Assert.Same(replacement.Identity, NullableField(owner, "_svgHostDeferredWaiterIdentity"));
            Assert.Same(newAttempt, NullableField(owner, "_svgHostDeferredRegistrationAttempt"));

            owner.OnSvgHostAdmissionWaiterFinished(demand, generation, replacement.Identity);
            Assert.Null(NullableField(owner, "_svgHostDeferredWaiter"));
            Assert.Null(NullableField(owner, "_svgHostDeferredWaiterIdentity"));
            Assert.Null(NullableField(owner, "_svgHostDeferredRegistrationAttempt"));

            SetField(owner, "_svgHostDeferredRegistrationAttempt", newAttempt);
            Volatile.Write(ref replacementEntry.WaiterCompletionStarted, 1);
            Assert.False(owner.OnSvgHostAdmissionWaiterRegistered(demand, generation, newAttempt, replacement));
        }
        finally
        {
            // These entries are never queued: retire their source-free linked
            // cancellation objects explicitly, without starting a waiter.
            oldEntry.Cancellation.Dispose();
            replacementEntry.Cancellation.Dispose();
        }
    }

    [Fact]
    public async Task DuplicateThresholdIncreaseWaitsForTheLatestRequirementBeforeClaim()
    {
        var domain = new SvgHostAdmissionCoordinator.DomainState();
        var admission = new ThresholdAdmission();
        var owner = BareBox();
        var demand = new MarkdownSvgRenderDemand(MarkdownSvgRenderPriority.Overscan);
        using var lifetime = new CancellationTokenSource();
        var firstAttempt = new object();
        InitializeSourceDeferredOwner(owner, demand, firstAttempt, admission, requiredBytes: 128);
        var queued = new ConcurrentQueue<Action>();
        SetField(owner, "_svgDeferredRetryEnqueueOverride", new Func<Action, bool>(callback =>
        {
            queued.Enqueue(callback);
            return true;
        }));

        SvgHostAdmissionCoordinator.PendingWaiter first = Assert.IsType<SvgHostAdmissionCoordinator.PendingWaiter>(
            domain.Defer(owner, admission, demand, 1, admission.CapacityVersion, 128, false,
                domain.OwnerVersion, demand.Version, 0, firstAttempt, lifetime.Token));
        SvgHostAdmissionCoordinator.PendingEntry entry = PendingEntryOf(first);
        Assert.True(SpinWait.SpinUntil(() => admission.WaitCalls.Length == 1, TimeSpan.FromSeconds(5)));
        Assert.Equal(128, admission.WaitCalls[0].RequiredBytes);
        Assert.Equal(0, admission.CapacityVersion);

        var secondAttempt = new object();
        lock (Field<object>(owner, "_svgHostDeferredSync"))
            SetField(owner, "_svgHostDeferredRegistrationAttempt", secondAttempt);
        SvgHostAdmissionCoordinator.PendingWaiter second = Assert.IsType<SvgHostAdmissionCoordinator.PendingWaiter>(
            domain.Defer(owner, admission, demand, 1, admission.CapacityVersion, 512, false,
                domain.OwnerVersion, demand.Version, 0, secondAttempt, lifetime.Token));
        Assert.Same(first.Identity, second.Identity);
        Assert.Equal(512, entry.RequiredSourceReservationBytes);

        // The smaller wait becomes ready without any version change. The
        // atomic claim must reconcile the duplicate's larger max and continue
        // waiting rather than retrying prematurely.
        admission.SetAvailableBytes(128);
        Assert.True(SpinWait.SpinUntil(() => admission.WaitCalls.Length == 2, TimeSpan.FromSeconds(5)));
        Assert.Equal(512, admission.WaitCalls[1].RequiredBytes);
        Assert.Equal(0, Volatile.Read(ref entry.Claimed));
        Assert.Contains(entry, Field<ICollection>(domain, "_pending").Cast<SvgHostAdmissionCoordinator.PendingEntry>());
        Assert.Empty(queued);

        admission.SetAvailableBytes(512);
        Assert.True(SpinWait.SpinUntil(() => first.IsFinished, TimeSpan.FromSeconds(5)));
        Assert.Equal(0, admission.CapacityVersion);
        Assert.Single(queued);
        Assert.Equal(2, Field<int>(owner, "_svgHostDeferred"));
        Assert.Same(first.Identity, NullableField(owner, "_svgHostDeferredWaiterIdentity"));

        first.Dispose();
        second.Dispose();
        GC.KeepAlive(owner);
    }

    [Fact]
    public async Task RejectedDuplicateRegistrationCannotRaiseAnAcceptedWaitersThreshold()
    {
        var domain = new SvgHostAdmissionCoordinator.DomainState();
        var admission = new ThresholdAdmission();
        var owner = BareBox();
        var demand = new MarkdownSvgRenderDemand(MarkdownSvgRenderPriority.Overscan);
        using var lifetime = new CancellationTokenSource();
        var firstAttempt = new object();
        InitializeSourceDeferredOwner(owner, demand, firstAttempt, admission, requiredBytes: 128);
        int queued = 0;
        SetField(owner, "_svgDeferredRetryEnqueueOverride", new Func<Action, bool>(_ =>
        {
            Interlocked.Increment(ref queued);
            return true;
        }));

        SvgHostAdmissionCoordinator.PendingWaiter first = Assert.IsType<
            SvgHostAdmissionCoordinator.PendingWaiter>(domain.Defer(
                owner, admission, demand, 1, admission.CapacityVersion, 128, false,
                domain.OwnerVersion, demand.Version, 0, firstAttempt, lifetime.Token));
        SvgHostAdmissionCoordinator.PendingEntry entry = PendingEntryOf(first);
        Assert.True(SpinWait.SpinUntil(() => admission.WaitCalls.Length == 1, TimeSpan.FromSeconds(5)));

        object hostGate = Field<object>(owner, "_svgHostDeferredSync");
        var rejectedAttempt = new object();
        var replacementAttempt = new object();
        Task<SvgHostAdmissionCoordinator.PendingWaiter?> rejectedRegistration;
        Monitor.Enter(hostGate);
        try
        {
            SetField(owner, "_svgHostDeferredRegistrationAttempt", rejectedAttempt);
            rejectedRegistration = Task.Run(() => domain.Defer(
                owner, admission, demand, 1, admission.CapacityVersion, 512, false,
                domain.OwnerVersion, demand.Version, 0, rejectedAttempt, lifetime.Token));
            Assert.True(SpinWait.SpinUntil(() =>
                Volatile.Read(ref entry.RegistrationsInFlight) == 1,
                TimeSpan.FromSeconds(5)));

            // The duplicate became stale while its ImageBox acceptance
            // callback was waiting outside the coordinator lock.
            SetField(owner, "_svgHostDeferredRegistrationAttempt", replacementAttempt);
        }
        finally
        {
            Monitor.Exit(hostGate);
        }

        Assert.Null(await rejectedRegistration.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(128, entry.RequiredSourceReservationBytes);
        Assert.Equal(0, Volatile.Read(ref entry.RegistrationsInFlight));

        admission.SetAvailableBytes(128);
        Assert.True(SpinWait.SpinUntil(() => first.IsFinished, TimeSpan.FromSeconds(5)));
        Assert.Single(admission.WaitCalls);
        Assert.Equal(1, Volatile.Read(ref queued));
        first.Dispose();
        GC.KeepAlive(owner);
    }

    [Fact]
    public async Task FirstRejectedRegistrationDoesNotSeedAcceptedDuplicateMetadataOrStrandWaiter()
    {
        var domain = new SvgHostAdmissionCoordinator.DomainState();
        var admission = new ThresholdAdmission();
        ImageBox owner = BareBox();
        var demand = new MarkdownSvgRenderDemand(MarkdownSvgRenderPriority.Overscan);
        using var lifetime = new CancellationTokenSource();
        var rejectedAttempt = new object();
        var acceptedAttempt = new object();
        InitializeSourceDeferredOwner(owner, demand, rejectedAttempt, admission, requiredBytes: 1024);
        int queued = 0;
        SetField(owner, "_svgDeferredRetryEnqueueOverride", new Func<Action, bool>(_ =>
        {
            Interlocked.Increment(ref queued);
            return true;
        }));

        object hostGate = Field<object>(owner, "_svgHostDeferredSync");
        Task<SvgHostAdmissionCoordinator.PendingWaiter?> rejectedTask;
        Task<SvgHostAdmissionCoordinator.PendingWaiter?> acceptedTask;
        SvgHostAdmissionCoordinator.PendingEntry entry;
        Monitor.Enter(hostGate);
        try
        {
            SetField(owner, "_svgHostDeferredRegistrationAttempt", rejectedAttempt);
            rejectedTask = Task.Run(() => domain.Defer(
                owner, admission, demand, 1, admission.CapacityVersion, 1024, false,
                domain.OwnerVersion, demand.Version, 0, rejectedAttempt, lifetime.Token));
            Assert.True(SpinWait.SpinUntil(() =>
                Field<ICollection>(domain, "_pending").Count == 1,
                TimeSpan.FromSeconds(5)));
            entry = Field<ICollection>(domain, "_pending")
                .Cast<SvgHostAdmissionCoordinator.PendingEntry>()
                .Single();
            Assert.True(SpinWait.SpinUntil(() =>
                Volatile.Read(ref entry.RegistrationsInFlight) == 1,
                TimeSpan.FromSeconds(5)));

            // The first row's constructor values are provisional. While its
            // identity callback is blocked, publish a lower-threshold attempt
            // that will be the only one accepted by the ImageBox.
            SetField(owner, "_svgHostDeferredRegistrationAttempt", acceptedAttempt);
            acceptedTask = Task.Run(() => domain.Defer(
                owner, admission, demand, 1, admission.CapacityVersion, 256, false,
                domain.OwnerVersion, demand.Version, 0, acceptedAttempt, lifetime.Token));
            Assert.True(SpinWait.SpinUntil(() =>
                Volatile.Read(ref entry.RegistrationsInFlight) == 2,
                TimeSpan.FromSeconds(5)));
        }
        finally
        {
            Monitor.Exit(hostGate);
        }

        Assert.Null(await rejectedTask.WaitAsync(TimeSpan.FromSeconds(5)));
        using SvgHostAdmissionCoordinator.PendingWaiter accepted = Assert.IsType<
            SvgHostAdmissionCoordinator.PendingWaiter>(
                await acceptedTask.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Same(entry.Identity, accepted.Identity);
        Assert.True(entry.HasAcceptedRegistration);
        Assert.Equal(256, entry.RequiredSourceReservationBytes);
        Assert.Equal(0, Volatile.Read(ref entry.RegistrationsInFlight));
        Assert.Equal(2, Volatile.Read(ref entry.WaiterStartState));
        Assert.True(SpinWait.SpinUntil(() => admission.WaitCalls.Length == 1, TimeSpan.FromSeconds(5)));
        Assert.Equal(256, admission.WaitCalls[0].RequiredBytes);

        admission.SetAvailableBytes(256);
        Assert.True(SpinWait.SpinUntil(() => accepted.IsFinished, TimeSpan.FromSeconds(5)));
        Assert.Equal(1, Volatile.Read(ref queued));
        GC.KeepAlive(owner);
    }

    [Fact]
    public async Task AlreadyReadyThresholdSignalsWithoutCapacityVersionAdvance()
    {
        var domain = new SvgHostAdmissionCoordinator.DomainState();
        var admission = new ThresholdAdmission(initialAvailableBytes: 1024);
        var owner = BareBox();
        var demand = new MarkdownSvgRenderDemand(MarkdownSvgRenderPriority.Overscan);
        using var lifetime = new CancellationTokenSource();
        var attempt = new object();
        InitializeSourceDeferredOwner(owner, demand, attempt, admission, requiredBytes: 768);
        int queued = 0;
        SetField(owner, "_svgDeferredRetryEnqueueOverride", new Func<Action, bool>(_ =>
        {
            Interlocked.Increment(ref queued);
            return true;
        }));

        using SvgHostAdmissionCoordinator.PendingWaiter waiter = Assert.IsType<
            SvgHostAdmissionCoordinator.PendingWaiter>(domain.Defer(
                owner, admission, demand, 1, admission.CapacityVersion, 768, false,
                domain.OwnerVersion, demand.Version, 0, attempt, lifetime.Token));
        Assert.True(SpinWait.SpinUntil(() => waiter.IsFinished, TimeSpan.FromSeconds(5)));

        Assert.Equal(0, admission.CapacityVersion);
        Assert.Single(admission.WaitCalls);
        Assert.Equal(768, admission.WaitCalls[0].RequiredBytes);
        Assert.Equal(1, Volatile.Read(ref queued));
        Assert.Equal(2, Field<int>(owner, "_svgHostDeferred"));
        GC.KeepAlive(owner);
    }

    [Fact]
    public async Task PromotionClaimsDeferredRetryWhenOverscanThresholdWaitRemainsPending()
    {
        var domain = new SvgHostAdmissionCoordinator.DomainState();
        var admission = new NeverReadyAdmission();
        var owner = BareBox();
        var demand = new MarkdownSvgRenderDemand(MarkdownSvgRenderPriority.Overscan);
        using var lifetime = new CancellationTokenSource();
        var attempt = new object();
        InitializeSourceDeferredOwner(owner, demand, attempt, admission, requiredBytes: 768);
        var queued = new ConcurrentQueue<Action>();
        SetField(owner, "_svgDeferredRetryEnqueueOverride", new Func<Action, bool>(callback =>
        {
            queued.Enqueue(callback);
            return true;
        }));

        SvgHostAdmissionCoordinator.PendingWaiter waiter = Assert.IsType<
            SvgHostAdmissionCoordinator.PendingWaiter>(domain.Defer(
                owner, admission, demand, 1, admission.CapacityVersion, 768, false,
                domain.OwnerVersion, demand.Version, 0, attempt, lifetime.Token));
        SetField(owner, "_svgHostDeferredWaiter", waiter);
        SvgHostAdmissionCoordinator.PendingEntry entry = PendingEntryOf(waiter);
        try
        {
            Task capacityWait = await admission.WaitStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(768, admission.RequiredBytes);
            Assert.Equal(MarkdownSvgRenderPriority.Overscan, admission.Priority);
            Assert.False(capacityWait.IsCompleted);
            Assert.Equal(0, Volatile.Read(ref entry.Claimed));

            Assert.True(demand.PromoteVisible());
            Assert.True(SpinWait.SpinUntil(
                () => waiter.IsFinished && queued.Count == 1,
                TimeSpan.FromSeconds(5)));

            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await capacityWait.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Single(queued);
            Assert.Equal(2, Field<int>(owner, "_svgHostDeferred"));
            Assert.False(Field<bool>(owner, "_svgHostDeferredCapacityReady"));
            Assert.Same(waiter.Identity, NullableField(owner, "_svgHostDeferredWaiterIdentity"));
            Assert.True(SpinWait.SpinUntil(
                () => IsCancellationDisposed(entry.Cancellation),
                TimeSpan.FromSeconds(5)));
        }
        finally
        {
            waiter.Dispose();
            typeof(ImageBox).GetMethod("ClearSvgHostDeferral",
                BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(owner, null);
            lifetime.Cancel();
        }
        GC.KeepAlive(owner);
        GC.KeepAlive(admission);
    }

    [Fact]
    public async Task DuplicateRegistrationBarrierPreventsClaimUntilIdentityIsAccepted()
    {
        var domain = new SvgHostAdmissionCoordinator.DomainState();
        var admission = new ThresholdAdmission();
        var owner = BareBox();
        var demand = new MarkdownSvgRenderDemand(MarkdownSvgRenderPriority.Overscan);
        using var lifetime = new CancellationTokenSource();
        var firstAttempt = new object();
        InitializeSourceDeferredOwner(owner, demand, firstAttempt, admission, requiredBytes: 128);
        var queued = new ConcurrentQueue<Action>();
        SetField(owner, "_svgDeferredRetryEnqueueOverride", new Func<Action, bool>(callback =>
        {
            queued.Enqueue(callback);
            return true;
        }));

        SvgHostAdmissionCoordinator.PendingWaiter first = Assert.IsType<SvgHostAdmissionCoordinator.PendingWaiter>(
            domain.Defer(owner, admission, demand, 1, admission.CapacityVersion, 128, false,
                domain.OwnerVersion, demand.Version, 0, firstAttempt, lifetime.Token));
        SvgHostAdmissionCoordinator.PendingEntry entry = PendingEntryOf(first);
        Assert.True(SpinWait.SpinUntil(() => admission.WaitCalls.Length == 1, TimeSpan.FromSeconds(5)));

        object hostGate = Field<object>(owner, "_svgHostDeferredSync");
        var secondAttempt = new object();
        Task<SvgHostAdmissionCoordinator.PendingWaiter?> secondTask;
        Monitor.Enter(hostGate);
        try
        {
            SetField(owner, "_svgHostDeferredRegistrationAttempt", secondAttempt);
            secondTask = Task.Run(() => domain.Defer(
                owner, admission, demand, 1, admission.CapacityVersion, 512, false,
                domain.OwnerVersion, demand.Version, 0, secondAttempt, lifetime.Token));
            Assert.True(SpinWait.SpinUntil(() =>
                Volatile.Read(ref entry.RegistrationsInFlight) == 1,
                TimeSpan.FromSeconds(5)));

            // Make only the first threshold ready while the accepted identity
            // callback is blocked outside the domain gate.
            admission.SetAvailableBytes(128);
            Thread.Sleep(100);
            Assert.Equal(0, Volatile.Read(ref entry.Claimed));
            Assert.False(first.IsFinished);
            Assert.Empty(queued);
        }
        finally
        {
            Monitor.Exit(hostGate);
        }

        SvgHostAdmissionCoordinator.PendingWaiter second = Assert.IsType<
            SvgHostAdmissionCoordinator.PendingWaiter>(
                await secondTask.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Same(first.Identity, second.Identity);
        Assert.True(SpinWait.SpinUntil(() => admission.WaitCalls.Length == 2, TimeSpan.FromSeconds(5)));
        Assert.Equal(512, admission.WaitCalls[1].RequiredBytes);
        Assert.Equal(0, Volatile.Read(ref entry.Claimed));
        Assert.Empty(queued);

        admission.SetAvailableBytes(512);
        Assert.True(SpinWait.SpinUntil(() => first.IsFinished, TimeSpan.FromSeconds(5)));
        Assert.Single(queued);
        Assert.Equal(0, Volatile.Read(ref entry.RegistrationsInFlight));
        first.Dispose();
        second.Dispose();
        GC.KeepAlive(owner);
    }

    [Fact]
    public async Task CancellationWaitsForInFlightRegistrationBeforeDisposingEntryTokenSource()
    {
        var domain = new SvgHostAdmissionCoordinator.DomainState();
        var admission = new ThresholdAdmission();
        var owner = BareBox();
        var demand = new MarkdownSvgRenderDemand(MarkdownSvgRenderPriority.Overscan);
        using var lifetime = new CancellationTokenSource();
        var firstAttempt = new object();
        InitializeSourceDeferredOwner(owner, demand, firstAttempt, admission, requiredBytes: 128);

        SvgHostAdmissionCoordinator.PendingWaiter first = Assert.IsType<SvgHostAdmissionCoordinator.PendingWaiter>(
            domain.Defer(owner, admission, demand, 1, admission.CapacityVersion, 128, false,
                domain.OwnerVersion, demand.Version, 0, firstAttempt, lifetime.Token));
        SvgHostAdmissionCoordinator.PendingEntry entry = PendingEntryOf(first);
        Assert.True(SpinWait.SpinUntil(() => admission.WaitCalls.Length == 1, TimeSpan.FromSeconds(5)));

        object hostGate = Field<object>(owner, "_svgHostDeferredSync");
        var secondAttempt = new object();
        Task<SvgHostAdmissionCoordinator.PendingWaiter?> secondTask;
        Monitor.Enter(hostGate);
        try
        {
            SetField(owner, "_svgHostDeferredRegistrationAttempt", secondAttempt);
            secondTask = Task.Run(() => domain.Defer(
                owner, admission, demand, 1, admission.CapacityVersion, 256, false,
                domain.OwnerVersion, demand.Version, 0, secondAttempt, lifetime.Token));
            Assert.True(SpinWait.SpinUntil(() =>
                Volatile.Read(ref entry.RegistrationsInFlight) == 1,
                TimeSpan.FromSeconds(5)));

            lifetime.Cancel();
            Assert.True(SpinWait.SpinUntil(() =>
                Volatile.Read(ref entry.WaiterCompletionStarted) != 0,
                TimeSpan.FromSeconds(5)));
            Assert.True(entry.Cancellation.IsCancellationRequested);
            _ = entry.Cancellation.Token; // Must remain usable until registration drain.
            Assert.Equal(1, Volatile.Read(ref entry.RegistrationsInFlight));
        }
        finally
        {
            Monitor.Exit(hostGate);
        }

        Assert.Null(await secondTask.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(SpinWait.SpinUntil(() => first.IsFinished, TimeSpan.FromSeconds(5)));
        Assert.Equal(0, Volatile.Read(ref entry.RegistrationsInFlight));
        Assert.True(SpinWait.SpinUntil(
            () => IsCancellationDisposed(entry.Cancellation),
            TimeSpan.FromSeconds(5)));
        first.Dispose();
        GC.KeepAlive(owner);
    }

    [Fact]
    public async Task UnstartedWaiterRetirementWaitsForRegistrationDrainBeforeDisposingTokenSource()
    {
        var domain = new SvgHostAdmissionCoordinator.DomainState();
        var admission = new RegistryAdmission();
        ImageBox owner = BareBox();
        var demand = new MarkdownSvgRenderDemand(MarkdownSvgRenderPriority.Overscan);
        using var lifetime = new CancellationTokenSource();
        var attempt = new object();
        InitializeDeferredOwner(owner, demand, attempt);
        var entry = new SvgHostAdmissionCoordinator.PendingEntry(
            owner, admission, demand, 1, 0, 256, false, 0, demand.Version, 0, lifetime.Token);
        var waiter = new SvgHostAdmissionCoordinator.PendingWaiter(domain, entry);
        Assert.True(owner.OnSvgHostAdmissionWaiterRegistered(demand, 1, attempt, waiter));
        SetField(owner, "_svgHostDeferredWaiter", waiter);

        // Model the registration callback still owning the published row,
        // while retirement begins before any waiter task was started.
        ((IList)Field<ICollection>(domain, "_pending")).Add(entry);
        var registrationsDrained = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        entry.RegistrationsDrained = registrationsDrained;
        entry.RegistrationsInFlight = 1;

        waiter.Dispose();

        Assert.True(entry.Cancellation.IsCancellationRequested);
        Assert.False(IsCancellationDisposed(entry.Cancellation));
        Assert.Equal(1, Volatile.Read(ref entry.RegistrationsInFlight));
        Assert.Same(waiter, NullableField(owner, "_svgHostDeferredWaiter"));

        entry.RegistrationsInFlight = 0;
        entry.RegistrationsDrained = null;
        registrationsDrained.TrySetResult();

        Assert.True(SpinWait.SpinUntil(
            () => IsCancellationDisposed(entry.Cancellation),
            TimeSpan.FromSeconds(5)));
        Assert.Equal(1, Volatile.Read(ref entry.WaiterCompletionStarted));
        Assert.Null(NullableField(owner, "_svgHostDeferredWaiter"));
        Assert.Null(NullableField(owner, "_svgHostDeferredWaiterIdentity"));
        Assert.Empty(Field<ICollection>(domain, "_pending"));
        GC.KeepAlive(owner);
    }

    [Fact]
    public async Task DuplicateThresholdGrowthSharesOneCumulativeEightVictimParkingBudget()
    {
        const int ownerCount = 10;
        const long ownerWeight = 100;
        var domain = new SvgHostAdmissionCoordinator.DomainState();
        var admission = new ThresholdAdmission();
        var owners = new ImageBox[ownerCount];
        var plans = new ImageBox.SvgTileRenderPlan[ownerCount];
        var documents = new PressureDocument[ownerCount];
        var ownerSlots = new SvgHostAdmissionCoordinator.OwnerSlot[ownerCount];
        var ownerDemand = new MarkdownSvgRenderDemand[ownerCount];
        using var lifetime = new CancellationTokenSource();
        var waiters = new List<SvgHostAdmissionCoordinator.PendingWaiter>(4);
        var knownEntries = new List<SvgHostAdmissionCoordinator.PendingEntry>(1);
        bool cleanupDrained = true;
        try
        {
            for (int i = 0; i < ownerCount; i++)
            {
                documents[i] = new PressureDocument();
                ownerDemand[i] = new MarkdownSvgRenderDemand(MarkdownSvgRenderPriority.Overscan);
                ownerSlots[i] = Assert.IsType<SvgHostAdmissionCoordinator.OwnerSlot>(
                    domain.TryReserveOwnerSlot(admission.AdmissionDomain, out _));
                plans[i] = new ImageBox.SvgTileRenderPlan(
                    documents[i], $"pressure-owner-{i}", 2048, 2048, i + 1,
                    ownerDemand[i], sourceReservationBytes: ownerWeight, ownerSlot: ownerSlots[i]);
                owners[i] = CreatePressureParkBox(plans[i]);
                ownerSlots[i].Commit(owners[i], plans[i], ownerDemand[i], ownerWeight);
            }

            var requester = BareBox();
            var requestDemand = new MarkdownSvgRenderDemand(MarkdownSvgRenderPriority.Overscan);
            var attempts = new object[] { new(), new(), new(), new() };
            InitializeSourceDeferredOwner(requester, requestDemand, attempts[0], admission, requiredBytes: 100);
            SvgHostAdmissionCoordinator.PendingWaiter? firstCandidate =
                domain.Defer(requester, admission, requestDemand, 1, admission.CapacityVersion, 100, false,
                    domain.OwnerVersion, requestDemand.Version, 0, attempts[0], lifetime.Token);
            CapturePendingEntries(domain, knownEntries);
            SvgHostAdmissionCoordinator.PendingWaiter first = Assert.IsType<
                SvgHostAdmissionCoordinator.PendingWaiter>(firstCandidate);
            waiters.Add(first);
            SvgHostAdmissionCoordinator.PendingEntry entry = PendingEntryOf(first);
            AddPendingEntry(entry, knownEntries);
            Assert.Equal(1, entry.ParkedVictims);
            Assert.Equal(100, entry.ParkedSourceBytes);

            SvgHostAdmissionCoordinator.PendingWaiter? secondCandidate =
                DeferDuplicate(domain, requester, admission, requestDemand, attempts[1], 250, lifetime.Token);
            CapturePendingEntries(domain, knownEntries);
            SvgHostAdmissionCoordinator.PendingWaiter second = Assert.IsType<
                SvgHostAdmissionCoordinator.PendingWaiter>(secondCandidate);
            waiters.Add(second);
            Assert.Same(first.Identity, second.Identity);
            Assert.Equal(3, entry.ParkedVictims);
            Assert.Equal(300, entry.ParkedSourceBytes);

            SvgHostAdmissionCoordinator.PendingWaiter? thirdCandidate =
                DeferDuplicate(domain, requester, admission, requestDemand, attempts[2], 800, lifetime.Token);
            CapturePendingEntries(domain, knownEntries);
            SvgHostAdmissionCoordinator.PendingWaiter third = Assert.IsType<
                SvgHostAdmissionCoordinator.PendingWaiter>(thirdCandidate);
            waiters.Add(third);
            Assert.Same(first.Identity, third.Identity);
            Assert.Equal(8, entry.ParkedVictims);
            Assert.Equal(800, entry.ParkedSourceBytes);

            SvgHostAdmissionCoordinator.PendingWaiter? fourthCandidate =
                DeferDuplicate(domain, requester, admission, requestDemand, attempts[3], 900, lifetime.Token);
            CapturePendingEntries(domain, knownEntries);
            SvgHostAdmissionCoordinator.PendingWaiter fourth = Assert.IsType<
                SvgHostAdmissionCoordinator.PendingWaiter>(fourthCandidate);
            waiters.Add(fourth);
            Assert.Same(first.Identity, fourth.Identity);
            Assert.Equal(8, entry.ParkedVictims);
            Assert.Equal(800, entry.ParkedSourceBytes);
            Assert.Equal(2, OwnerCount(domain));

            first.Dispose();
            second.Dispose();
            third.Dispose();
            fourth.Dispose();
            Assert.True(SpinWait.SpinUntil(() => first.IsFinished, TimeSpan.FromSeconds(5)));
            // Only eight victims fit the per-entry bound. Retire the two owners
            // which correctly remained resident so every fixture document has an
            // explicit terminal owner before awaiting disposal.
            Assert.True(owners[8].TryParkSvgDocument(plans[8], ownerDemand[8].Version, maximumTier: 0));
            Assert.True(owners[9].TryParkSvgDocument(plans[9], ownerDemand[9].Version, maximumTier: 0));
            foreach (PressureDocument document in documents)
                await document.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            try
            {
                lifetime.Cancel();
            }
            finally
            {
                foreach (SvgHostAdmissionCoordinator.PendingWaiter waiter in waiters)
                {
                    SvgHostAdmissionCoordinator.PendingEntry? waiterEntry = TryPendingEntryOf(waiter);
                    if (waiterEntry is not null && !knownEntries.Contains(waiterEntry))
                        knownEntries.Add(waiterEntry);
                }
                foreach (SvgHostAdmissionCoordinator.PendingWaiter waiter in waiters)
                    waiter.Dispose();
                CapturePendingEntries(domain, knownEntries);
                cleanupDrained = SpinWait.SpinUntil(
                    () => knownEntries.All(entry => IsCancellationDisposed(entry.Cancellation)),
                    TimeSpan.FromSeconds(5));
                for (int i = 0; i < documents.Length; i++)
                {
                    if (documents[i] is not { } document || document.Disposed.Task.IsCompleted)
                        continue;
                    if (owners[i] is { } owner && plans[i] is { } plan && ownerDemand[i] is { } demand)
                        _ = owner.TryParkSvgDocument(plan, demand.Version, maximumTier: 0);
                    if (!document.Disposed.Task.IsCompleted)
                        document.Dispose();
                }
                foreach (SvgHostAdmissionCoordinator.OwnerSlot? slot in ownerSlots)
                    slot?.Dispose();
                GC.KeepAlive(owners);
                GC.KeepAlive(plans);
            }
        }
        Assert.True(cleanupDrained, "The pressure test left a source-free admission waiter undrained.");
    }

    [Fact]
    public async Task OwnerSlotPressureParksOnlyOneVictimBeforeReservingReplacement()
    {
        const int existingReservationCount = 510;
        const int eligibleOwnerCount = 2;
        var domain = new SvgHostAdmissionCoordinator.DomainState();
        object identity = new();
        var reservedSlots = new List<SvgHostAdmissionCoordinator.OwnerSlot>(existingReservationCount + 1);
        var ownerSlots = new List<SvgHostAdmissionCoordinator.OwnerSlot>(eligibleOwnerCount);
        var owners = new List<ImageBox>(eligibleOwnerCount);
        var plans = new List<ImageBox.SvgTileRenderPlan>(eligibleOwnerCount);
        var documents = new List<PressureDocument>(eligibleOwnerCount);
        var demands = new List<MarkdownSvgRenderDemand>(eligibleOwnerCount);
        try
        {
            for (int i = 0; i < existingReservationCount; i++)
            {
                reservedSlots.Add(Assert.IsType<SvgHostAdmissionCoordinator.OwnerSlot>(
                    domain.TryReserveOwnerSlot(identity, out _)));
            }

            for (int i = 0; i < eligibleOwnerCount; i++)
            {
                SvgHostAdmissionCoordinator.OwnerSlot slot = Assert.IsType<
                    SvgHostAdmissionCoordinator.OwnerSlot>(domain.TryReserveOwnerSlot(identity, out _));
                ownerSlots.Add(slot);
                var document = new PressureDocument();
                var demand = new MarkdownSvgRenderDemand(MarkdownSvgRenderPriority.Overscan);
                var plan = new ImageBox.SvgTileRenderPlan(
                    document, $"single-owner-slot-victim-{i}", 2048, 2048, i + 1,
                    demand, sourceReservationBytes: 100, ownerSlot: slot);
                ImageBox owner = CreatePressureParkBox(plan);
                slot.Commit(owner, plan, demand, sourceReservationBytes: 100);
                documents.Add(document);
                demands.Add(demand);
                plans.Add(plan);
                owners.Add(owner);
            }

            Assert.Equal(eligibleOwnerCount, OwnerCount(domain));
            Assert.Equal(existingReservationCount, ReservedCount(domain));
            Assert.Equal(512, OwnerCount(domain) + ReservedCount(domain));

            // Owner-slot pressure needs one released slot, not byte-weighted
            // source capacity. The second eligible document must remain owned.
            domain.TryParkForAdmission(
                requiredSourceReservationBytes: 1,
                requesterPriority: MarkdownSvgRenderPriority.Visible,
                requiresOwnerSlot: true);

            Assert.Equal(1, OwnerCount(domain));
            // Parking releases its slot synchronously, but document cleanup
            // drains on background work. Observe that drain before counting it.
            await Task.WhenAny(documents.Select(document => document.Disposed.Task))
                .WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, documents.Count(document => document.Disposed.Task.IsCompletedSuccessfully));
            Assert.Equal(existingReservationCount, ReservedCount(domain));
            reservedSlots.Add(Assert.IsType<SvgHostAdmissionCoordinator.OwnerSlot>(
                domain.TryReserveOwnerSlot(identity, out _)));
            Assert.Equal(1, OwnerCount(domain));
            Assert.Equal(512, OwnerCount(domain) + ReservedCount(domain));
        }
        finally
        {
            for (int i = 0; i < documents.Count; i++)
            {
                if (!documents[i].Disposed.Task.IsCompleted)
                    _ = owners[i].TryParkSvgDocument(plans[i], demands[i].Version, maximumTier: 0);
            }
            foreach (SvgHostAdmissionCoordinator.OwnerSlot slot in ownerSlots)
                slot.Dispose();
            foreach (SvgHostAdmissionCoordinator.OwnerSlot slot in reservedSlots)
                slot.Dispose();
            await Task.WhenAll(documents.Select(document => document.Disposed.Task))
                .WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task SourceFreePendingPopulationIsBoundedAndCancellationDrainsEveryRow()
    {
        var domain = new SvgHostAdmissionCoordinator.DomainState();
        var admission = new WaitingAdmission();
        var boxes = new ImageBox[129];
        var demands = new MarkdownSvgRenderDemand[129];
        var attempts = new object[129];
        var handles = new SvgHostAdmissionCoordinator.PendingWaiter?[129];
        using var lifetime = new CancellationTokenSource();
        try
        {
            for (int i = 0; i < boxes.Length; i++)
            {
                boxes[i] = BareBox();
                demands[i] = new MarkdownSvgRenderDemand(MarkdownSvgRenderPriority.Overscan);
                attempts[i] = new object();
                SetField(boxes[i], "_svgHostDeferredSync", new object());
                SetField(boxes[i], "_svgLoadDemandTicket", demands[i]);
                SetField(boxes[i], "_svgLoadDemandGeneration", 1);
                SetField(boxes[i], "_svgHostDeferred", 1);
                SetField(boxes[i], "_svgHostDeferredRegistrationAttempt", attempts[i]);
                handles[i] = domain.Defer(boxes[i], admission, demands[i], 1,
                    0, 128, false, 0, 0, 0, attempts[i], lifetime.Token);
                if (i < 128)
                    Assert.NotNull(handles[i]);
                else
                    Assert.Null(handles[i]);
            }
            Assert.Equal(128, Field<ICollection>(domain, "_pending").Count);
            foreach (SvgHostAdmissionCoordinator.PendingWaiter? handle in handles)
                handle?.Dispose();
            Assert.Empty(Field<ICollection>(domain, "_pending"));
            Assert.True(await Task.Run(() => SpinWait.SpinUntil(() =>
                handles.Take(128).All(handle => handle!.IsFinished), TimeSpan.FromSeconds(5))));
            Assert.All(demands.Take(128), demand => Assert.Equal(0, demand.PendingPromotionCount));
        }
        finally
        {
            foreach (SvgHostAdmissionCoordinator.PendingWaiter? handle in handles)
                handle?.Dispose();
            await lifetime.CancelAsync();
            GC.KeepAlive(boxes);
            GC.KeepAlive(admission);
        }
    }

    [Fact]
    public async Task DeadWeakPendingOwnersCannotPermanentlyExhaustTheDeferredRegistry()
    {
        var domain = new SvgHostAdmissionCoordinator.DomainState();
        var admission = new WaitingAdmission();
        using var lifetime = new CancellationTokenSource();
        (WeakReference[] weakOwners, SvgHostAdmissionCoordinator.PendingWaiter[] handles) =
            CreateWeakPendingOwners(domain, admission, lifetime.Token);
        try
        {
            for (int i = 0; i < 10 && weakOwners.Any(weak => weak.IsAlive); i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
            }
            Assert.All(weakOwners, weak => Assert.False(weak.IsAlive));
            ImageBox replacementOwner = BareBox();
            var replacementDemand = new MarkdownSvgRenderDemand(MarkdownSvgRenderPriority.Overscan);
            object attempt = new();
            InitializeDeferredOwner(replacementOwner, replacementDemand, attempt);
            using SvgHostAdmissionCoordinator.PendingWaiter replacement =
                Assert.IsType<SvgHostAdmissionCoordinator.PendingWaiter>(domain.Defer(
                    replacementOwner, admission, replacementDemand, 1, 0, 128,
                    false, 0, 0, 0, attempt, lifetime.Token));
            Assert.Single(Field<ICollection>(domain, "_pending"));
            Assert.True(await Task.Run(() => SpinWait.SpinUntil(() =>
                handles.All(handle => handle.IsFinished), TimeSpan.FromSeconds(5))));
            GC.KeepAlive(replacementOwner);
        }
        finally
        {
            foreach (SvgHostAdmissionCoordinator.PendingWaiter handle in handles)
                handle.Dispose();
            await lifetime.CancelAsync();
            GC.KeepAlive(admission);
        }
    }

    [Fact]
    public void FailedPromotionSubscriptionCannotLeaveAPendingRowOrRetainItsWeakOwner()
    {
        var domain = new SvgHostAdmissionCoordinator.DomainState();
        var admission = new WaitingAdmission();
        using var lifetime = new CancellationTokenSource();
        var demand = new MarkdownSvgRenderDemand(MarkdownSvgRenderPriority.Visible,
            () => throw new InvalidOperationException("Reject injected promotion scheduling."));
        WeakReference weak = CreateRejectedPendingOwner(domain, admission, demand, lifetime.Token);
        Assert.Empty(Field<ICollection>(domain, "_pending"));
        Assert.Equal(0, demand.PendingPromotionCount);
        Assert.Equal(0, Field<int>(demand, "_activeRegistrations"));
        for (int i = 0; i < 10 && weak.IsAlive; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
        Assert.False(weak.IsAlive);
        GC.KeepAlive(demand);
        GC.KeepAlive(admission);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference CreateRejectedPendingOwner(
        SvgHostAdmissionCoordinator.DomainState domain, IMarkdownSvgAdmission admission,
        MarkdownSvgRenderDemand demand, CancellationToken lifetime)
    {
        ImageBox owner = BareBox();
        object attempt = new();
        InitializeDeferredOwner(owner, demand, attempt);
        Assert.Null(domain.Defer(owner, admission, demand, 1, 0, 128,
            false, 0, demand.Version, 0, attempt, lifetime));
        return new WeakReference(owner);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (WeakReference[], SvgHostAdmissionCoordinator.PendingWaiter[]) CreateWeakPendingOwners(
        SvgHostAdmissionCoordinator.DomainState domain,
        IMarkdownSvgAdmission admission,
        CancellationToken lifetime)
    {
        var weakOwners = new WeakReference[128];
        var handles = new SvgHostAdmissionCoordinator.PendingWaiter[128];
        var owners = new ImageBox[128];
        for (int i = 0; i < weakOwners.Length; i++)
        {
            ImageBox owner = BareBox();
            owners[i] = owner;
            var demand = new MarkdownSvgRenderDemand(MarkdownSvgRenderPriority.Overscan);
            object attempt = new();
            InitializeDeferredOwner(owner, demand, attempt);
            handles[i] = Assert.IsType<SvgHostAdmissionCoordinator.PendingWaiter>(
                domain.Defer(owner, admission, demand, 1, 0, 128, false, 0, 0, 0, attempt, lifetime));
            weakOwners[i] = new WeakReference(owner);
        }
        // Hold the entire setup population until its full-capacity precondition
        // is asserted. Concurrent GC tests must not make earlier weak owners
        // eligible for pruning while this loop is still admitting later rows.
        Assert.Equal(128, Field<ICollection>(domain, "_pending").Count);
        GC.KeepAlive(owners);
        return (weakOwners, handles);
    }

    private static void InitializeDeferredOwner(ImageBox owner, MarkdownSvgRenderDemand demand, object attempt)
    {
        SetField(owner, "_svgHostDeferredSync", new object());
        SetField(owner, "_svgLoadDemandTicket", demand);
        SetField(owner, "_svgLoadDemandGeneration", 1);
        SetField(owner, "_svgHostDeferred", 1);
        SetField(owner, "_svgHostDeferredRegistrationAttempt", attempt);
    }

    private static void InitializeSourceDeferredOwner(
        ImageBox owner,
        MarkdownSvgRenderDemand demand,
        object attempt,
        IMarkdownSvgAdmission admission,
        long requiredBytes)
    {
        InitializeDeferredOwner(owner, demand, attempt);
        SetField(owner, "_svgHostDeferredDenialIdentity", attempt);
        SetField(owner, "_svgHostDeferredAdmission", new WeakReference<IMarkdownSvgAdmission>(admission));
        SetField(owner, "_svgHostDeferredDemandVersion", demand.Version);
        SetField(owner, "_svgHostDeferredVisualDemandEpoch", 0L);
        SetField(owner, "_svgHostDeferredCapacityVersion", admission.CapacityVersion);
        SetField(owner, "_svgHostDeferredOwnerVersion", 0L);
        SetField(owner, "_svgHostDeferredRequiredSourceBytes", requiredBytes);
        SetField(owner, "_svgHostDeferredRequiresOwnerSlot", false);
        SetField(owner, "_svgHostDeferredCapacityReady", false);
    }

    private static SvgHostAdmissionCoordinator.PendingWaiter? DeferDuplicate(
        SvgHostAdmissionCoordinator.DomainState domain,
        ImageBox owner,
        IMarkdownSvgAdmission admission,
        MarkdownSvgRenderDemand demand,
        object attempt,
        long requiredBytes,
        CancellationToken lifetime)
    {
        lock (Field<object>(owner, "_svgHostDeferredSync"))
            SetField(owner, "_svgHostDeferredRegistrationAttempt", attempt);
        return domain.Defer(
            owner,
            admission,
            demand,
            1,
            admission.CapacityVersion,
            requiredBytes,
            requiresOwnerSlot: false,
            ownerVersionAtDenial: domain.OwnerVersion,
            demandVersionAtDenial: demand.Version,
            visualDemandEpochAtDenial: 0,
            registrationAttempt: attempt,
            cancellationToken: lifetime);
    }

    private static SvgHostAdmissionCoordinator.PendingEntry PendingEntryOf(
        SvgHostAdmissionCoordinator.PendingWaiter waiter) =>
        Field<SvgHostAdmissionCoordinator.PendingEntry>(waiter, "_entry");

    private static SvgHostAdmissionCoordinator.PendingEntry? TryPendingEntryOf(
        SvgHostAdmissionCoordinator.PendingWaiter waiter) =>
        waiter.GetType().GetField("_entry", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(waiter)
            as SvgHostAdmissionCoordinator.PendingEntry;

    private static void CapturePendingEntries(
        SvgHostAdmissionCoordinator.DomainState domain,
        List<SvgHostAdmissionCoordinator.PendingEntry> entries)
    {
        foreach (SvgHostAdmissionCoordinator.PendingEntry entry in
            Field<ICollection>(domain, "_pending").Cast<SvgHostAdmissionCoordinator.PendingEntry>())
        {
            AddPendingEntry(entry, entries);
        }
    }

    private static void AddPendingEntry(
        SvgHostAdmissionCoordinator.PendingEntry entry,
        List<SvgHostAdmissionCoordinator.PendingEntry> entries)
    {
        if (!entries.Contains(entry))
            entries.Add(entry);
    }

    private static bool IsCancellationDisposed(CancellationTokenSource cancellation)
    {
        try
        {
            _ = cancellation.Token;
            return false;
        }
        catch (ObjectDisposedException)
        {
            return true;
        }
    }

    private static ImageBox CreatePressureParkBox(ImageBox.SvgTileRenderPlan plan)
    {
        ImageBox box = BareBox();
        SetField(box, "_svgWorkSync", new object());
        SetField(box, "_svgPublicationSync", new object());
        SetField(box, "_svgTileLock", new object());
        SetField(box, "_svgTiles", new Dictionary<(int X, int Y, int Width, int Height), SharedCanvasBitmapCache.Lease>());
        SetField(box, "_svgTilesInFlight", new Dictionary<(int X, int Y, int Width, int Height), CancellationTokenSource>());
        SetField(box, "_svgTileWorkOwners", CreatePressureWorkOwnerMap());
        SetField(box, "_svgTileCacheTransitionToken", null);
        SetField(box, "_svgFallbackTiles", null);
        SetField(box, "_svgTileCacheIdentityPrefix", null);
        SetField(box, "_svgTileCacheWidthPixels", 0);
        SetField(box, "_svgTileCacheHeightPixels", 0);
        SetField(box, "_svgVisibleTileRangeKnown", false);
        SetField(box, "_svgCurrentViewportVisible", -1);
        SetField(box, "_svgTileRenderPlan", plan);
        SetField(box, "_svgDocument", plan.Document);
        SetField(box, "_svgLoadDemandTicket", plan.Demand);
        SetField(box, "_svgRenderGeneration", plan.RenderGeneration);
        SetField(box, "_svgUsesTiles", true);
        SetField(box, "_resourcesSuspended", false);
        SetField(box, "_disposed", false);
        SetField(box, "Context", new MarkdownLayoutContext(
            resourceCreator: null!,
            themeSnapshot: null!,
            sourceMap: null!,
            registry: null!,
            flowDirection: FlowDirection.LeftToRight));
        return box;
    }

    private static IDictionary CreatePressureWorkOwnerMap() =>
        (IDictionary)(Activator.CreateInstance(
            typeof(Dictionary<,>).MakeGenericType(
                typeof(ValueTuple<int, int, int, int>),
                typeof(ImageBox).GetNestedType("SvgTileWorkOwner", BindingFlags.NonPublic)!))
            ?? throw new Xunit.Sdk.XunitException("Could not create ImageBox SVG work-owner map."));

    private sealed class ThresholdAdmission(long initialAvailableBytes = 0) : IMarkdownSvgAdmission
    {
        private readonly object _sync = new();
        private readonly List<(long RequiredBytes, MarkdownSvgRenderPriority Priority,
            TaskCompletionSource Signal)> _waits = [];
        private long _availableBytes = initialAvailableBytes;

        public object AdmissionDomain { get; } = new();
        public long CapacityVersion => 0;

        internal (long RequiredBytes, MarkdownSvgRenderPriority Priority)[] WaitCalls
        {
            get
            {
                lock (_sync)
                    return _waits.Select(wait => (wait.RequiredBytes, wait.Priority)).ToArray();
            }
        }

        public Task WaitForCapacityAsync(
            long requiredFreeBytes,
            MarkdownSvgRenderPriority priority,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_sync)
            {
                var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _waits.Add((requiredFreeBytes, priority, signal));
                if (_availableBytes >= requiredFreeBytes)
                    return Task.CompletedTask;
                return signal.Task.WaitAsync(cancellationToken);
            }
        }

        internal void SetAvailableBytes(long bytes)
        {
            TaskCompletionSource[] ready;
            lock (_sync)
            {
                _availableBytes = bytes;
                ready = _waits
                    .Where(wait => wait.RequiredBytes <= bytes)
                    .Select(wait => wait.Signal)
                    .ToArray();
            }

            foreach (TaskCompletionSource signal in ready)
                signal.TrySetResult();
        }

        public ValueTask<MarkdownSvgAdmissionResult> TryOpenAsync(
            MarkdownSvgOpenRequest request,
            MarkdownSvgRenderDemand demand,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Pure admission tests must not open a document.");
    }

    private sealed class NeverReadyAdmission : IMarkdownSvgAdmission
    {
        private readonly TaskCompletionSource<Task> _waitStarted = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource<Task> WaitStarted => _waitStarted;
        internal long RequiredBytes { get; private set; }
        internal MarkdownSvgRenderPriority Priority { get; private set; }

        public object AdmissionDomain { get; } = new();
        public long CapacityVersion => 0;

        public Task WaitForCapacityAsync(
            long requiredFreeBytes,
            MarkdownSvgRenderPriority priority,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequiredBytes = requiredFreeBytes;
            Priority = priority;
            Task wait = Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            _waitStarted.TrySetResult(wait);
            return wait;
        }

        public ValueTask<MarkdownSvgAdmissionResult> TryOpenAsync(
            MarkdownSvgOpenRequest request,
            MarkdownSvgRenderDemand demand,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The promotion test must not open source bytes.");
    }

    private sealed class PressureDocument : IMarkdownSvgDocument
    {
        private readonly TaskCompletionSource _disposed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public MarkdownSvgDocumentInfo Info { get; } = new(2048, 2048, 1);
        internal TaskCompletionSource Disposed => _disposed;

        public ValueTask<MarkdownSvgRaster> RenderAsync(
            MarkdownSvgRenderRequest request,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Pressure-park tests must not render.");

        public void Dispose() => _disposed.TrySetResult();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (WeakReference, object) CommitWeakOwner(
        SvgHostAdmissionCoordinator.DomainState domain,
        SvgHostAdmissionCoordinator.OwnerSlot slot,
        bool collectPlan)
    {
        ImageBox owner = BareBox();
        var demand = new MarkdownSvgRenderDemand(MarkdownSvgRenderPriority.Overscan);
        var plan = new ImageBox.SvgTileRenderPlan(new RegistryDocument(), "weak", 2048, 2048, 1);
        slot.Commit(owner, plan, demand, 64);
        return collectPlan ? (new WeakReference(plan), owner) : (new WeakReference(owner), plan);
    }

    private static SvgHostAdmissionCoordinator.OwnerSlot? Reserve(
        SvgHostAdmissionCoordinator.DomainState domain, object identity, out long version) =>
        domain.TryReserveOwnerSlot(identity, out version);

    private static ImageBox BareBox() =>
        (ImageBox)RuntimeHelpers.GetUninitializedObject(typeof(ImageBox));
    private static int OwnerCount(SvgHostAdmissionCoordinator.DomainState domain) =>
        Field<ICollection>(domain, "_owners").Count;
    private static int ReservedCount(SvgHostAdmissionCoordinator.DomainState domain) =>
        Field<int>(domain, "_reservedOwnerSlots");
    private static T Field<T>(object owner, string name) =>
        (T)(owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(owner)
            ?? throw new InvalidOperationException($"Missing registry field {name}."));
    private static object? NullableField(object owner, string name) =>
        owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner);
    private static void SetField(object owner, string name, object? value) =>
        owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(owner, value);

    private sealed class RegistryAdmission : IMarkdownSvgAdmission
    {
        public object AdmissionDomain { get; } = new();
        public long CapacityVersion => 0;
        public Task WaitForCapacityAsync(
            long requiredFreeBytes,
            MarkdownSvgRenderPriority priority,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Pure identity tests must not start admission waiters.");
        public ValueTask<MarkdownSvgAdmissionResult> TryOpenAsync(
            MarkdownSvgOpenRequest request, MarkdownSvgRenderDemand demand, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Pure identity tests must not open a document.");
    }

    private sealed class WaitingAdmission : IMarkdownSvgAdmission
    {
        private readonly TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public object AdmissionDomain { get; } = new();
        public long CapacityVersion => 0;
        public Task WaitForCapacityAsync(
            long requiredFreeBytes,
            MarkdownSvgRenderPriority priority,
            CancellationToken cancellationToken) =>
            _changed.Task.WaitAsync(cancellationToken);
        public ValueTask<MarkdownSvgAdmissionResult> TryOpenAsync(
            MarkdownSvgOpenRequest request, MarkdownSvgRenderDemand demand, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Pure registry tests must not open a document.");
    }

    private sealed class RegistryDocument : IMarkdownSvgDocument
    {
        public MarkdownSvgDocumentInfo Info { get; } = new(2048, 2048, 1);
        public ValueTask<MarkdownSvgRaster> RenderAsync(
            MarkdownSvgRenderRequest request, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Pure registry tests must not render.");
        public void Dispose() { }
    }
}
