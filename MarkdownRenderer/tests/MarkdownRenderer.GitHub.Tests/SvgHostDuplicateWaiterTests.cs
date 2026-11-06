using System.Collections;
using System.Collections.Concurrent;
using MarkdownRenderer.Images;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using Xunit;
using static MarkdownRenderer.GitHub.Tests.SvgHostAdmissionFixture;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>
/// Checks SVG host waiters for duplicate demands: replacement waiters,
/// threshold increases, already-ready thresholds, and rejected and
/// barriered duplicate registrations.
/// </summary>
// These tests exercise managed registry ownership only. Bare boxes are weak
// identity tokens; only managed deferred-ownership callbacks are called, never
// layout, paint, WinUI, provider rendering or device methods.
[Collection(AllocationSensitiveContractCollection.Name)]
public sealed class SvgHostDuplicateWaiterTests
{
    private readonly MarkdownRenderResources _renderResources = new();

    [Fact]
    public void LateFinishedWaiterCannotClearAReplacementForTheSameDemandAndGeneration()
    {
        SvgHostLoadDeferral owner = TestSvgHostDeferralHost.CreateDeferral(_renderResources.Images);
        var domain = new SvgHostAdmissionDomain();
        var admission = new RegistryAdmission();
        var demand = new MarkdownSvgRenderDemand(MarkdownSvgRenderPriority.Overscan);
        const int generation = 41;
        TestSvgHostDeferralHost.Of(owner).LoadDemand = demand;
        TestSvgHostDeferralHost.Of(owner).LoadDemandGeneration = generation;
        SetField(owner, "_svgHostDeferred", 1);
        using var lifetime = new CancellationTokenSource();
        var oldEntry = new SvgHostPendingEntry(
            owner, admission, demand, generation, 0, 128, false, 0, 0, 0, lifetime.Token);
        var replacementEntry = new SvgHostPendingEntry(
            owner, admission, demand, generation, 0, 128, false, 0, 0, 0, lifetime.Token);
        using var oldWaiter = new SvgHostPendingWaiter(domain, oldEntry);
        using var replacement = new SvgHostPendingWaiter(domain, replacementEntry);
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
        var domain = new SvgHostAdmissionDomain();
        var admission = new ThresholdAdmission();
        var owner = TestSvgHostDeferralHost.CreateDeferral(_renderResources.Images);
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

        SvgHostPendingWaiter first = Assert.IsType<SvgHostPendingWaiter>(
            domain.Defer(owner, admission, demand, 1, admission.CapacityVersion, 128, false,
                domain.Owners.OwnerVersion, demand.Version, 0, firstAttempt, lifetime.Token));
        SvgHostPendingEntry entry = PendingEntryOf(first);
        Assert.True(SpinWait.SpinUntil(() => admission.WaitCalls.Length == 1, TimeSpan.FromSeconds(5)));
        Assert.Equal(128, admission.WaitCalls[0].RequiredBytes);
        Assert.Equal(0, admission.CapacityVersion);

        var secondAttempt = new object();
        lock (Field<object>(owner, "_svgHostDeferredSync"))
            SetField(owner, "_svgHostDeferredRegistrationAttempt", secondAttempt);
        SvgHostPendingWaiter second = Assert.IsType<SvgHostPendingWaiter>(
            domain.Defer(owner, admission, demand, 1, admission.CapacityVersion, 512, false,
                domain.Owners.OwnerVersion, demand.Version, 0, secondAttempt, lifetime.Token));
        Assert.Same(first.Identity, second.Identity);
        Assert.Equal(512, entry.RequiredSourceReservationBytes);

        // The smaller wait becomes ready without any version change. The
        // atomic claim must reconcile the duplicate's larger max and continue
        // waiting rather than retrying prematurely.
        admission.SetAvailableBytes(128);
        Assert.True(SpinWait.SpinUntil(() => admission.WaitCalls.Length == 2, TimeSpan.FromSeconds(5)));
        Assert.Equal(512, admission.WaitCalls[1].RequiredBytes);
        Assert.Equal(0, Volatile.Read(ref entry.Claimed));
        Assert.Contains(entry, Field<ICollection>(domain, "_pending").Cast<SvgHostPendingEntry>());
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
        var domain = new SvgHostAdmissionDomain();
        var admission = new ThresholdAdmission();
        var owner = TestSvgHostDeferralHost.CreateDeferral(_renderResources.Images);
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

        SvgHostPendingWaiter first = Assert.IsType<
            SvgHostPendingWaiter>(domain.Defer(
                owner, admission, demand, 1, admission.CapacityVersion, 128, false,
                domain.Owners.OwnerVersion, demand.Version, 0, firstAttempt, lifetime.Token));
        SvgHostPendingEntry entry = PendingEntryOf(first);
        Assert.True(SpinWait.SpinUntil(() => admission.WaitCalls.Length == 1, TimeSpan.FromSeconds(5)));

        object hostGate = Field<object>(owner, "_svgHostDeferredSync");
        var rejectedAttempt = new object();
        var replacementAttempt = new object();
        Task<SvgHostPendingWaiter?> rejectedRegistration;
        Monitor.Enter(hostGate);
        try
        {
            SetField(owner, "_svgHostDeferredRegistrationAttempt", rejectedAttempt);
            rejectedRegistration = Task.Run(() => domain.Defer(
                owner, admission, demand, 1, admission.CapacityVersion, 512, false,
                domain.Owners.OwnerVersion, demand.Version, 0, rejectedAttempt, lifetime.Token));
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
        var domain = new SvgHostAdmissionDomain();
        var admission = new ThresholdAdmission();
        SvgHostLoadDeferral owner = TestSvgHostDeferralHost.CreateDeferral(_renderResources.Images);
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
        Task<SvgHostPendingWaiter?> rejectedTask;
        Task<SvgHostPendingWaiter?> acceptedTask;
        SvgHostPendingEntry entry;
        Monitor.Enter(hostGate);
        try
        {
            SetField(owner, "_svgHostDeferredRegistrationAttempt", rejectedAttempt);
            rejectedTask = Task.Run(() => domain.Defer(
                owner, admission, demand, 1, admission.CapacityVersion, 1024, false,
                domain.Owners.OwnerVersion, demand.Version, 0, rejectedAttempt, lifetime.Token));
            Assert.True(SpinWait.SpinUntil(() =>
                Field<ICollection>(domain, "_pending").Count == 1,
                TimeSpan.FromSeconds(5)));
            entry = Field<ICollection>(domain, "_pending")
                .Cast<SvgHostPendingEntry>()
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
                domain.Owners.OwnerVersion, demand.Version, 0, acceptedAttempt, lifetime.Token));
            Assert.True(SpinWait.SpinUntil(() =>
                Volatile.Read(ref entry.RegistrationsInFlight) == 2,
                TimeSpan.FromSeconds(5)));
        }
        finally
        {
            Monitor.Exit(hostGate);
        }

        Assert.Null(await rejectedTask.WaitAsync(TimeSpan.FromSeconds(5)));
        using SvgHostPendingWaiter accepted = Assert.IsType<
            SvgHostPendingWaiter>(
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
        var domain = new SvgHostAdmissionDomain();
        var admission = new ThresholdAdmission(initialAvailableBytes: 1024);
        var owner = TestSvgHostDeferralHost.CreateDeferral(_renderResources.Images);
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

        using SvgHostPendingWaiter waiter = Assert.IsType<
            SvgHostPendingWaiter>(domain.Defer(
                owner, admission, demand, 1, admission.CapacityVersion, 768, false,
                domain.Owners.OwnerVersion, demand.Version, 0, attempt, lifetime.Token));
        Assert.True(SpinWait.SpinUntil(() => waiter.IsFinished, TimeSpan.FromSeconds(5)));

        Assert.Equal(0, admission.CapacityVersion);
        Assert.Single(admission.WaitCalls);
        Assert.Equal(768, admission.WaitCalls[0].RequiredBytes);
        Assert.Equal(1, Volatile.Read(ref queued));
        Assert.Equal(2, Field<int>(owner, "_svgHostDeferred"));
        GC.KeepAlive(owner);
    }

    [Fact]
    public async Task DuplicateRegistrationBarrierPreventsClaimUntilIdentityIsAccepted()
    {
        var domain = new SvgHostAdmissionDomain();
        var admission = new ThresholdAdmission();
        var owner = TestSvgHostDeferralHost.CreateDeferral(_renderResources.Images);
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

        SvgHostPendingWaiter first = Assert.IsType<SvgHostPendingWaiter>(
            domain.Defer(owner, admission, demand, 1, admission.CapacityVersion, 128, false,
                domain.Owners.OwnerVersion, demand.Version, 0, firstAttempt, lifetime.Token));
        SvgHostPendingEntry entry = PendingEntryOf(first);
        Assert.True(SpinWait.SpinUntil(() => admission.WaitCalls.Length == 1, TimeSpan.FromSeconds(5)));

        object hostGate = Field<object>(owner, "_svgHostDeferredSync");
        var secondAttempt = new object();
        Task<SvgHostPendingWaiter?> secondTask;
        Monitor.Enter(hostGate);
        try
        {
            SetField(owner, "_svgHostDeferredRegistrationAttempt", secondAttempt);
            secondTask = Task.Run(() => domain.Defer(
                owner, admission, demand, 1, admission.CapacityVersion, 512, false,
                domain.Owners.OwnerVersion, demand.Version, 0, secondAttempt, lifetime.Token));
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

        SvgHostPendingWaiter second = Assert.IsType<
            SvgHostPendingWaiter>(
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
}
