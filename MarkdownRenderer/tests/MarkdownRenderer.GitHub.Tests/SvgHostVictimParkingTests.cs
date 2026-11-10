using MarkdownRenderer.Images;
using MarkdownRenderer.Layout.Internal.Images;
using MarkdownRenderer.Layout.Internal.Images.Svg.HostAdmission;
using MarkdownRenderer.Layout.Internal.Images.Svg.Tiles;
using MarkdownRenderer.Layout.Internal.RenderResources;
using Xunit;
using static MarkdownRenderer.GitHub.Tests.SvgHostAdmissionFixture;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>
/// Checks how the SVG host parks victim owners: one parked victim per
/// reserved replacement under owner slot pressure, and one cumulative
/// parking budget for duplicate threshold growth.
/// </summary>
// These tests exercise managed registry ownership only. The requester is a
// real SvgHostLoadDeferral over a managed host, denied through
// DeferSvgHostLoad and registered by the scope's admission coordinator; only
// managed deferred-ownership callbacks are called, never layout, paint, WinUI,
// provider rendering or device methods.
[Collection(AllocationSensitiveContractCollection.Name)]
public sealed class SvgHostVictimParkingTests
{
    private readonly MarkdownRenderResources _renderResources = new();

    [Fact]
    public async Task DuplicateThresholdGrowthSharesOneCumulativeEightVictimParkingBudget()
    {
        const int ownerCount = 10;
        const long ownerWeight = 100;
        var admission = new ThresholdAdmission();
        SvgHostAdmissionDomain domain = DomainOf(_renderResources.Images, admission.DomainKey);
        var pressure = new SvgHostPressureOwnerSet(ownerCount);
        using var lifetime = new CancellationTokenSource();
        var requestDemand = new MarkdownSvgRenderDemand(MarkdownSvgRenderPriority.Overscan);
        SvgHostLoadDeferral requester = CreateLoadingOwner(_renderResources.Images, requestDemand, lifetime.Token);
        var waiters = new List<SvgHostPendingWaiter>(3);
        var knownEntries = new List<SvgHostPendingEntry>(1);
        bool cleanupDrained = true;
        try
        {
            pressure.Commit(domain, admission.DomainKey, ownerWeight, CreatePressureParkBox);

            Deny(requester, admission, requestDemand, requiredBytes: 100);
            CapturePendingEntries(domain, knownEntries);
            SvgHostLoadDeferralState denied = requester.GetState();
            Assert.True(denied.HasWaiter);
            SvgHostPendingEntry entry = PendingEntry(domain, denied.WaiterIdentity);
            object attempt = denied.RegistrationAttempt
                ?? throw new InvalidOperationException("The denial kept no live registration attempt.");
            Assert.Equal(1, entry.ParkedVictims);
            Assert.Equal(100, entry.ParkedSourceBytes);

            SvgHostPendingWaiter second = DeferDuplicateAndTrack(
                domain, requester, admission, requestDemand, attempt, 250, lifetime.Token, waiters, knownEntries);
            Assert.Same(entry.Identity, second.Identity);
            Assert.Equal(3, entry.ParkedVictims);
            Assert.Equal(300, entry.ParkedSourceBytes);

            SvgHostPendingWaiter third = DeferDuplicateAndTrack(
                domain, requester, admission, requestDemand, attempt, 800, lifetime.Token, waiters, knownEntries);
            Assert.Same(entry.Identity, third.Identity);
            Assert.Equal(8, entry.ParkedVictims);
            Assert.Equal(800, entry.ParkedSourceBytes);

            SvgHostPendingWaiter fourth = DeferDuplicateAndTrack(
                domain, requester, admission, requestDemand, attempt, 900, lifetime.Token, waiters, knownEntries);
            Assert.Same(entry.Identity, fourth.Identity);
            Assert.Equal(8, entry.ParkedVictims);
            Assert.Equal(800, entry.ParkedSourceBytes);
            Assert.Equal(2, domain.Owners.OwnerCount);

            requester.ClearSvgHostDeferral();
            second.Dispose();
            third.Dispose();
            fourth.Dispose();
            Assert.True(SpinWait.SpinUntil(() => IsFinished(entry), TimeSpan.FromSeconds(5)));
            // Only eight victims fit the per-entry bound. Retire the two owners
            // which correctly remained resident so every fixture document has an
            // explicit terminal owner before awaiting disposal.
            Assert.True(pressure.Owners[8].TryParkSvgDocument(
                pressure.Plans[8], pressure.Demands[8].Version, maximumTier: 0));
            Assert.True(pressure.Owners[9].TryParkSvgDocument(
                pressure.Plans[9], pressure.Demands[9].Version, maximumTier: 0));
            foreach (SvgHostPressureDocument document in pressure.Documents)
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
                requester.ClearSvgHostDeferral();
                cleanupDrained = DisposeWaitersAndWaitForEntryRetirement(domain, waiters, knownEntries);
                pressure.ParkOrDisposeUnfinishedDocuments();
            }
        }
        Assert.True(cleanupDrained, "The pressure test left a source-free admission waiter undrained.");
    }

    [Fact]
    public async Task OwnerSlotPressureParksOnlyOneVictimBeforeReservingReplacement()
    {
        const int existingReservationCount = 510;
        const int eligibleOwnerCount = 2;
        var domain = new SvgHostAdmissionDomain();
        object identity = new();
        var reservedSlots = new List<SvgHostOwnerSlot>(existingReservationCount + 1);
        var ownerSlots = new List<SvgHostOwnerSlot>(eligibleOwnerCount);
        var owners = new List<ImageBox>(eligibleOwnerCount);
        var plans = new List<SvgTileRenderPlan>(eligibleOwnerCount);
        var documents = new List<SvgHostPressureDocument>(eligibleOwnerCount);
        var demands = new List<MarkdownSvgRenderDemand>(eligibleOwnerCount);
        try
        {
            for (int i = 0; i < existingReservationCount; i++)
            {
                reservedSlots.Add(Assert.IsType<SvgHostOwnerSlot>(
                    domain.Owners.TryReserveOwnerSlot(identity, out _)));
            }

            for (int i = 0; i < eligibleOwnerCount; i++)
            {
                SvgHostOwnerSlot slot = Assert.IsType<
                    SvgHostOwnerSlot>(domain.Owners.TryReserveOwnerSlot(identity, out _));
                ownerSlots.Add(slot);
                var document = new SvgHostPressureDocument();
                var demand = new MarkdownSvgRenderDemand(MarkdownSvgRenderPriority.Overscan);
                var plan = new SvgTileRenderPlan(
                    document, $"single-owner-slot-victim-{i}", 2048, 2048, i + 1,
                    demand, sourceReservationBytes: 100, ownerSlot: slot);
                ImageBox owner = CreatePressureParkBox(plan);
                slot.Commit(owner, plan, demand, sourceReservationBytes: 100);
                documents.Add(document);
                demands.Add(demand);
                plans.Add(plan);
                owners.Add(owner);
            }

            Assert.Equal(eligibleOwnerCount, domain.Owners.OwnerCount);
            Assert.Equal(existingReservationCount, domain.Owners.ReservedOwnerSlotCount);
            Assert.Equal(512, domain.Owners.OwnerCount + domain.Owners.ReservedOwnerSlotCount);

            // Owner-slot pressure needs one released slot, not byte-weighted
            // source capacity. The second eligible document must remain owned.
            domain.TryParkForAdmission(
                requiredSourceReservationBytes: 1,
                requesterPriority: MarkdownSvgRenderPriority.Visible,
                requiresOwnerSlot: true);

            Assert.Equal(1, domain.Owners.OwnerCount);
            // Parking releases its slot synchronously, but document cleanup
            // drains on background work. Observe that drain before counting it.
            await Task.WhenAny(documents.Select(document => document.Disposed.Task))
                .WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, documents.Count(document => document.Disposed.Task.IsCompletedSuccessfully));
            Assert.Equal(existingReservationCount, domain.Owners.ReservedOwnerSlotCount);
            reservedSlots.Add(Assert.IsType<SvgHostOwnerSlot>(
                domain.Owners.TryReserveOwnerSlot(identity, out _)));
            Assert.Equal(1, domain.Owners.OwnerCount);
            Assert.Equal(512, domain.Owners.OwnerCount + domain.Owners.ReservedOwnerSlotCount);
        }
        finally
        {
            for (int i = 0; i < documents.Count; i++)
            {
                if (!documents[i].Disposed.Task.IsCompleted)
                    _ = owners[i].TryParkSvgDocument(plans[i], demands[i].Version, maximumTier: 0);
            }
            foreach (SvgHostOwnerSlot slot in ownerSlots)
                slot.Dispose();
            foreach (SvgHostOwnerSlot slot in reservedSlots)
                slot.Dispose();
            await Task.WhenAll(documents.Select(document => document.Disposed.Task))
                .WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    // Registers a duplicate of the requester's accepted denial: the same
    // demand and live registration attempt with a new source threshold.
    private static SvgHostPendingWaiter DeferDuplicateAndTrack(
        SvgHostAdmissionDomain domain,
        SvgHostLoadDeferral owner,
        IMarkdownSvgAdmission admission,
        MarkdownSvgRenderDemand demand,
        object attempt,
        long requiredBytes,
        CancellationToken lifetime,
        List<SvgHostPendingWaiter> waiters,
        List<SvgHostPendingEntry> knownEntries)
    {
        SvgHostPendingWaiter? candidate = domain.Defer(
            owner,
            admission,
            demand,
            1,
            admission.CapacityVersion,
            requiredBytes,
            requiresOwnerSlot: false,
            ownerVersionAtDenial: domain.Owners.OwnerVersion,
            demandVersionAtDenial: demand.Version,
            visualDemandEpochAtDenial: 0,
            registrationAttempt: attempt,
            cancellationToken: lifetime);
        CapturePendingEntries(domain, knownEntries);
        SvgHostPendingWaiter waiter = Assert.IsType<SvgHostPendingWaiter>(candidate);
        waiters.Add(waiter);
        return waiter;
    }

    // Disposes every waiter and reports whether each pending entry seen during
    // the test (or still reachable from a waiter) retired its cancellation.
    private static bool DisposeWaitersAndWaitForEntryRetirement(
        SvgHostAdmissionDomain domain,
        List<SvgHostPendingWaiter> waiters,
        List<SvgHostPendingEntry> knownEntries)
    {
        foreach (SvgHostPendingWaiter waiter in waiters)
        {
            if (waiter.Entry is { } waiterEntry)
                AddPendingEntry(waiterEntry, knownEntries);
        }
        foreach (SvgHostPendingWaiter waiter in waiters)
            waiter.Dispose();
        CapturePendingEntries(domain, knownEntries);
        return SpinWait.SpinUntil(
            () => knownEntries.All(entry => IsCancellationDisposed(entry.Cancellation)),
            TimeSpan.FromSeconds(5));
    }

    private static void CapturePendingEntries(
        SvgHostAdmissionDomain domain,
        List<SvgHostPendingEntry> entries)
    {
        foreach (SvgHostPendingEntry entry in domain.GetPendingEntries())
            AddPendingEntry(entry, entries);
    }

    private static void AddPendingEntry(
        SvgHostPendingEntry entry,
        List<SvgHostPendingEntry> entries)
    {
        if (!entries.Contains(entry))
            entries.Add(entry);
    }

    private ImageBox CreatePressureParkBox(SvgTileRenderPlan plan)
    {
        ImageBox box = NewBox(_renderResources, "https://example.test/pressure-park.svg", "pressure park svg");
        ImageBoxTestState.AcceptTiledPlan(box, plan);
        return box;
    }
}
