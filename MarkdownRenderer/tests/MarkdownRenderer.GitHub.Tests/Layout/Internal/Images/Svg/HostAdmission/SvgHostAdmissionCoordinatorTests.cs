using System.Runtime.CompilerServices;
using MarkdownRenderer.GitHub.Tests.TestSupport;
using MarkdownRenderer.Images;
using MarkdownRenderer.Layout.Internal.Images;
using MarkdownRenderer.Layout.Internal.Images.Svg.HostAdmission;
using MarkdownRenderer.Layout.Internal.Images.Svg.Tiles;
using MarkdownRenderer.Layout.Internal.RenderResources;
using Xunit;
using static MarkdownRenderer.GitHub.Tests.TestSupport.Fixtures.SvgHostAdmissionFixture;

namespace MarkdownRenderer.GitHub.Tests.Layout.Internal.Images.Svg.HostAdmission;

/// <summary>
/// Checks the SVG host owner slots: the bounded owner population, slot
/// reservation, commit and disposal, capacity wakeups, weak owner pruning
/// and retirement, and captured victim recency.
/// </summary>
// These tests exercise managed registry ownership only. Bare boxes are weak
// identity tokens; only managed deferred-ownership callbacks are called, never
// layout, paint, WinUI, provider rendering or device methods.
[Collection(AllocationSensitiveContractCollection.Name)]
public sealed class SvgHostAdmissionCoordinatorTests
{
    private readonly MarkdownRenderResources _renderResources = new();

    [Fact]
    public async Task ReservedOwnerPopulationIsHardBoundedAndReturnedSlotsAreReusable()
    {
        var domain = new SvgHostAdmissionDomain();
        var identity = new object();
        var slots = new List<SvgHostOwnerSlot>();
        try
        {
            for (int i = 0; i < 512; i++)
                slots.Add(Assert.IsType<SvgHostOwnerSlot>(
                    Reserve(domain, identity, out _)));

            Assert.Null(Reserve(domain, identity, out long deniedVersion));
            Assert.Equal(512, domain.Owners.ReservedOwnerSlotCount);
            Assert.Equal(0, domain.Owners.OwnerCount);
            Task changed = domain.Owners.WaitForOwnerVersionChangeAsync(deniedVersion, CancellationToken.None);
            Assert.False(changed.IsCompleted);

            slots[173].Dispose();
            slots[173].Dispose();
            await changed.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(511, domain.Owners.ReservedOwnerSlotCount);
            using SvgHostOwnerSlot replacement =
                Assert.IsType<SvgHostOwnerSlot>(
                    Reserve(domain, identity, out long admittedVersion));
            Assert.True(admittedVersion > deniedVersion);
            Assert.Null(Reserve(domain, identity, out _));
        }
        finally
        {
            foreach (SvgHostOwnerSlot slot in slots)
                slot.Dispose();
        }
        Assert.Equal(0, domain.Owners.ReservedOwnerSlotCount);
        Assert.Equal(0, domain.Owners.OwnerCount);
    }

    [Fact]
    public async Task ReleaseBetweenDenialAndSubscriptionCannotLoseTheCapacityWakeup()
    {
        var domain = new SvgHostAdmissionDomain();
        var identity = new object();
        var slots = new List<SvgHostOwnerSlot>();
        try
        {
            for (int i = 0; i < 512; i++)
                slots.Add(Assert.IsType<SvgHostOwnerSlot>(
                    Reserve(domain, identity, out _)));
            Assert.Null(Reserve(domain, identity, out long deniedVersion));

            slots[0].Dispose();
            Task changed = domain.Owners.WaitForOwnerVersionChangeAsync(deniedVersion, CancellationToken.None);
            Assert.True(changed.IsCompletedSuccessfully);
            await changed;
        }
        finally
        {
            foreach (SvgHostOwnerSlot slot in slots)
                slot.Dispose();
        }
    }

    [Fact]
    public async Task CommittingAndDisposingTheSameSlotCannotLeaveAPublishedOwnerBehind()
    {
        var domain = new SvgHostAdmissionDomain();
        using SvgHostOwnerSlot slot =
            Assert.IsType<SvgHostOwnerSlot>(
                Reserve(domain, new object(), out _));
        ImageBox owner = BareBox();
        var demand = new MarkdownSvgRenderDemand(MarkdownSvgRenderPriority.Overscan);
        var plan = new SvgTileRenderPlan(
            new RegistryDocument(), "registry-race", 2048, 2048, 1,
            demand, sourceReservationBytes: 128, ownerSlot: slot);
        object domainGate = domain.Owners.Sync;
        object slotGate = slot.Sync;
        Task commit;
        Task dispose;
        Monitor.Enter(domainGate);
        try
        {
            commit = Task.Run(() => slot.Commit(owner, plan, demand, 128));
            // Holding the owner registry gate pauses Commit after it has acquired the
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
        Assert.Equal(0, domain.Owners.OwnerCount);
        Assert.Equal(0, domain.Owners.ReservedOwnerSlotCount);
        slot.Dispose();
        Assert.Equal(0, domain.Owners.OwnerCount);
        GC.KeepAlive(owner);
        GC.KeepAlive(plan);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PruningADeadWeakOwnerOrPlanSignalsExistingCapacityWaiters(bool collectPlan)
    {
        var domain = new SvgHostAdmissionDomain();
        using SvgHostOwnerSlot slot =
            Assert.IsType<SvgHostOwnerSlot>(
                Reserve(domain, new object(), out _));
        (WeakReference weak, object retained) = CommitWeakOwner(domain, slot, collectPlan);
        Assert.Equal(1, domain.Owners.OwnerCount);
        long observed = domain.Owners.OwnerVersion;
        Task changed = domain.Owners.WaitForOwnerVersionChangeAsync(observed, CancellationToken.None);
        Assert.False(changed.IsCompleted);

        for (int i = 0; i < 10 && weak.IsAlive; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
        Assert.False(weak.IsAlive);
        // Admission prunes dead entries before consulting its hard slot cap.
        using SvgHostOwnerSlot next =
            Assert.IsType<SvgHostOwnerSlot>(
                Reserve(domain, new object(), out _));
        await changed.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(domain.Owners.OwnerVersion > observed);
        Assert.Equal(0, domain.Owners.OwnerCount);
        Assert.Equal(1, domain.Owners.ReservedOwnerSlotCount);
        GC.KeepAlive(retained);
    }

    [Fact]
    public void DisposedReservationCannotBeCommittedOrReturnAnotherOwnersCapacity()
    {
        var domain = new SvgHostAdmissionDomain();
        using SvgHostOwnerSlot retired =
            Assert.IsType<SvgHostOwnerSlot>(
                Reserve(domain, new object(), out _));
        using SvgHostOwnerSlot active =
            Assert.IsType<SvgHostOwnerSlot>(
                Reserve(domain, new object(), out _));
        retired.Dispose();
        ImageBox owner = BareBox();
        var demand = new MarkdownSvgRenderDemand(MarkdownSvgRenderPriority.Visible);
        var plan = new SvgTileRenderPlan(new RegistryDocument(), "retired", 2048, 2048, 1);
        Assert.Throws<InvalidOperationException>(() => retired.Commit(owner, plan, demand, 128));
        retired.Dispose();
        Assert.Equal(1, domain.Owners.ReservedOwnerSlotCount);
        Assert.Equal(0, domain.Owners.OwnerCount);
        GC.KeepAlive(owner);
    }

    [Fact]
    public void RetirementOfCommittedAndReservedOwnersDoesNotAllocateWithoutWaiters()
    {
        var domain = new SvgHostAdmissionDomain();
        var identity = new object();
        var demand = new MarkdownSvgRenderDemand(MarkdownSvgRenderPriority.Overscan);
        using (SvgHostOwnerSlot warm =
            Assert.IsType<SvgHostOwnerSlot>(Reserve(domain, identity, out _)))
        {
            ImageBox owner = BareBox();
            var plan = new SvgTileRenderPlan(new RegistryDocument(), "warm-release", 2048, 2048, 1);
            warm.Commit(owner, plan, demand, 64);
            warm.Dispose();
            GC.KeepAlive(owner);
            GC.KeepAlive(plan);
        }

        var slots = new SvgHostOwnerSlot[512];
        var owners = new ImageBox[256];
        var plans = new SvgTileRenderPlan[256];
        try
        {
            for (int i = 0; i < slots.Length; i++)
            {
                slots[i] = Assert.IsType<SvgHostOwnerSlot>(Reserve(domain, identity, out _));
                if (i < owners.Length)
                {
                    owners[i] = BareBox();
                    plans[i] = new SvgTileRenderPlan(new RegistryDocument(), "release", 2048, 2048, 1);
                    slots[i].Commit(owners[i], plans[i], demand, 64);
                }
            }
            long before = GC.GetAllocatedBytesForCurrentThread();
            foreach (SvgHostOwnerSlot slot in slots)
                slot.Dispose();
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.Equal(0, allocated);
            Assert.Equal(0, domain.Owners.OwnerCount);
            Assert.Equal(0, domain.Owners.ReservedOwnerSlotCount);
        }
        finally
        {
            foreach (SvgHostOwnerSlot? slot in slots)
                slot?.Dispose();
            GC.KeepAlive(owners);
            GC.KeepAlive(plans);
        }
    }

    [Fact]
    public void CapturedVictimRecencyRemainsStableAfterLiveOwnerTouches()
    {
        var domain = new SvgHostAdmissionDomain();
        using SvgHostOwnerSlot slot =
            Assert.IsType<SvgHostOwnerSlot>(Reserve(domain, new object(), out _));
        ImageBox owner = BareBox();
        var demand = new MarkdownSvgRenderDemand(MarkdownSvgRenderPriority.Overscan);
        var plan = new SvgTileRenderPlan(new RegistryDocument(), "recency", 2048, 2048, 1);
        var older = new SvgHostOwnerEntry(owner, plan, demand, 64, slot, 10);
        var newer = new SvgHostOwnerEntry(owner, plan, demand, 64, slot, 20);
        long capturedOlder = older.LastUse;
        long capturedNewer = newer.LastUse;
        older.LastUse = 30;
        newer.LastUse = 0;

        Assert.True(SvgHostOwnerRegistry.CompareParkCandidateOrder(
            0, capturedOlder, 0, capturedNewer) < 0);
        Assert.True(SvgHostOwnerRegistry.CompareParkCandidateOrder(
            0, older.LastUse, 0, newer.LastUse) > 0);
        Assert.True(SvgHostOwnerRegistry.CompareParkCandidateOrder(
            0, long.MaxValue, 1, long.MinValue) < 0);
        Assert.Equal(0, SvgHostOwnerRegistry.CompareParkCandidateOrder(1, 20, 1, 20));

        GC.KeepAlive(owner);
        GC.KeepAlive(plan);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private (WeakReference, object) CommitWeakOwner(
        SvgHostAdmissionDomain domain,
        SvgHostOwnerSlot slot,
        bool collectPlan)
    {
        ImageBox owner = BareBox();
        var demand = new MarkdownSvgRenderDemand(MarkdownSvgRenderPriority.Overscan);
        var plan = new SvgTileRenderPlan(new RegistryDocument(), "weak", 2048, 2048, 1);
        slot.Commit(owner, plan, demand, 64);
        return collectPlan ? (new WeakReference(plan), owner) : (new WeakReference(owner), plan);
    }

    private static SvgHostOwnerSlot? Reserve(
        SvgHostAdmissionDomain domain, object identity, out long version) =>
        domain.Owners.TryReserveOwnerSlot(identity, out version);

    private ImageBox BareBox() => NewBox(_renderResources, "https://example.test/owner.svg", "owner svg");
}
