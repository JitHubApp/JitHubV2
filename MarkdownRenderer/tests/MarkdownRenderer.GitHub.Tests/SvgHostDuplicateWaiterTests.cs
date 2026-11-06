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
// These tests exercise managed registry ownership only. Every owner is a real
// SvgHostLoadDeferral over a managed host, denied through DeferSvgHostLoad and
// registered by the scope's admission coordinator. A stale attempt comes from
// a second real denial. The deferral lock is reentrant, so a denial made while
// the test holds it completes before a blocked registration callback resumes.
// No layout, paint, WinUI, provider rendering or device methods are called.
[Collection(AllocationSensitiveContractCollection.Name)]
public sealed class SvgHostDuplicateWaiterTests
{
    private readonly MarkdownRenderResources _renderResources = new();

    [Fact]
    public void LateFinishedWaiterCannotClearAReplacementForTheSameDemandAndGeneration()
    {
        var admission = new ThresholdAdmission();
        var demand = new MarkdownSvgRenderDemand(MarkdownSvgRenderPriority.Overscan);
        const int generation = 41;
        using var lifetime = new CancellationTokenSource();
        SvgHostLoadDeferral owner = CreateLoadingOwner(
            _renderResources.Images, demand, lifetime.Token, generation: generation);
        // The test's waiters belong to their own domain and are never queued.
        var domain = new SvgHostAdmissionDomain();
        var oldEntry = new SvgHostPendingEntry(
            owner, admission, demand, generation, 0, 128, false, 0, 0, 0, lifetime.Token);
        var replacementEntry = new SvgHostPendingEntry(
            owner, admission, demand, generation, 0, 128, false, 0, 0, 0, lifetime.Token);
        using var oldWaiter = new SvgHostPendingWaiter(domain, oldEntry);
        using var replacement = new SvgHostPendingWaiter(domain, replacementEntry);
        try
        {
            Deny(owner, admission, demand, requiredBytes: 128, generation);
            object oldAttempt = AttemptOf(owner);
            Assert.True(owner.OnSvgHostAdmissionWaiterRegistered(demand, generation, oldAttempt, oldWaiter));
            Assert.True(owner.GetState().HasWaiter);
            owner.OnSvgHostAdmissionWaiterFinished(demand, generation, oldWaiter.Identity);
            Assert.False(owner.GetState().HasWaiter);

            Deny(owner, admission, demand, requiredBytes: 128, generation);
            object newAttempt = AttemptOf(owner);
            Assert.NotSame(oldAttempt, newAttempt);
            Assert.True(owner.OnSvgHostAdmissionWaiterRegistered(demand, generation, newAttempt, replacement));
            owner.OnSvgHostAdmissionWaiterFinished(demand, generation, oldWaiter.Identity);
            SvgHostLoadDeferralState kept = owner.GetState();
            Assert.True(kept.HasWaiter);
            Assert.Same(replacement.Identity, kept.WaiterIdentity);
            Assert.Same(newAttempt, kept.RegistrationAttempt);

            owner.OnSvgHostAdmissionWaiterFinished(demand, generation, replacement.Identity);
            SvgHostLoadDeferralState finished = owner.GetState();
            Assert.False(finished.HasWaiter);
            Assert.Null(finished.WaiterIdentity);
            Assert.Null(finished.RegistrationAttempt);

            Deny(owner, admission, demand, requiredBytes: 128, generation);
            object thirdAttempt = AttemptOf(owner);
            Volatile.Write(ref replacementEntry.WaiterCompletionStarted, 1);
            Assert.False(owner.OnSvgHostAdmissionWaiterRegistered(demand, generation, thirdAttempt, replacement));
        }
        finally
        {
            owner.ClearSvgHostDeferral();
            // These entries are never queued: retire their source-free linked
            // cancellation objects explicitly, without starting a waiter.
            oldEntry.Cancellation.Dispose();
            replacementEntry.Cancellation.Dispose();
        }
    }

    [Fact]
    public async Task DuplicateThresholdIncreaseWaitsForTheLatestRequirementBeforeClaim()
    {
        var admission = new ThresholdAdmission();
        var demand = new MarkdownSvgRenderDemand(MarkdownSvgRenderPriority.Overscan);
        using var lifetime = new CancellationTokenSource();
        var queued = new ConcurrentQueue<Action>();
        SvgHostLoadDeferral owner = CreateLoadingOwner(_renderResources.Images, demand, lifetime.Token, callback =>
        {
            queued.Enqueue(callback);
            return true;
        });
        SvgHostAdmissionDomain domain = DomainOf(_renderResources.Images, admission.DomainKey);

        Deny(owner, admission, demand, requiredBytes: 128);
        SvgHostLoadDeferralState denied = owner.GetState();
        SvgHostPendingEntry entry = PendingEntry(domain, denied.WaiterIdentity);
        Assert.True(SpinWait.SpinUntil(() => admission.WaitCalls.Length == 1, TimeSpan.FromSeconds(5)));
        Assert.Equal(128, admission.WaitCalls[0].RequiredBytes);
        Assert.Equal(0, admission.CapacityVersion);

        // A duplicate registration of the same demand raises the row's threshold.
        SvgHostPendingWaiter second = Assert.IsType<SvgHostPendingWaiter>(
            domain.Defer(owner, admission, demand, 1, admission.CapacityVersion, 512, false,
                domain.Owners.OwnerVersion, demand.Version, 0, AttemptOf(owner), lifetime.Token));
        Assert.Same(entry.Identity, second.Identity);
        Assert.Equal(512, entry.RequiredSourceReservationBytes);

        // The smaller wait becomes ready without any version change. The
        // atomic claim must reconcile the duplicate's larger max and continue
        // waiting rather than retrying prematurely.
        admission.SetAvailableBytes(128);
        Assert.True(SpinWait.SpinUntil(() => admission.WaitCalls.Length == 2, TimeSpan.FromSeconds(5)));
        Assert.Equal(512, admission.WaitCalls[1].RequiredBytes);
        Assert.Equal(0, Volatile.Read(ref entry.Claimed));
        Assert.Contains(entry, domain.GetPendingEntries());
        Assert.Empty(queued);

        admission.SetAvailableBytes(512);
        Assert.True(SpinWait.SpinUntil(() => IsFinished(entry), TimeSpan.FromSeconds(5)));
        Assert.Equal(0, admission.CapacityVersion);
        Assert.Single(queued);
        SvgHostLoadDeferralState retried = owner.GetState();
        Assert.Equal(SvgHostDeferralPhase.RetryQueued, retried.Phase);
        Assert.Same(entry.Identity, retried.WaiterIdentity);

        second.Dispose();
        owner.ClearSvgHostDeferral();
        GC.KeepAlive(owner);
    }

    [Fact]
    public async Task RejectedDuplicateRegistrationCannotRaiseAnAcceptedWaitersThreshold()
    {
        var admission = new ThresholdAdmission();
        var demand = new MarkdownSvgRenderDemand(MarkdownSvgRenderPriority.Overscan);
        using var lifetime = new CancellationTokenSource();
        int queued = 0;
        SvgHostLoadDeferral owner = CreateLoadingOwner(_renderResources.Images, demand, lifetime.Token, _ =>
        {
            Interlocked.Increment(ref queued);
            return true;
        });
        SvgHostAdmissionDomain domain = DomainOf(_renderResources.Images, admission.DomainKey);

        Deny(owner, admission, demand, requiredBytes: 128);
        SvgHostLoadDeferralState denied = owner.GetState();
        SvgHostPendingEntry entry = PendingEntry(domain, denied.WaiterIdentity);
        object staleAttempt = AttemptOf(owner);
        Assert.True(SpinWait.SpinUntil(() => admission.WaitCalls.Length == 1, TimeSpan.FromSeconds(5)));

        Task<SvgHostPendingWaiter?> rejectedRegistration;
        Monitor.Enter(owner.Sync);
        try
        {
            rejectedRegistration = Task.Run(() => domain.Defer(
                owner, admission, demand, 1, admission.CapacityVersion, 512, false,
                domain.Owners.OwnerVersion, demand.Version, 0, staleAttempt, lifetime.Token));
            Assert.True(SpinWait.SpinUntil(() =>
                Volatile.Read(ref entry.RegistrationsInFlight) == 1,
                TimeSpan.FromSeconds(5)));

            // The duplicate becomes stale while its owner callback waits
            // outside the coordinator lock: a second real denial of the same
            // demand replaces its attempt and joins the accepted row.
            Deny(owner, admission, demand, requiredBytes: 128);
            SvgHostLoadDeferralState replaced = owner.GetState();
            Assert.NotSame(staleAttempt, replaced.RegistrationAttempt);
            Assert.True(replaced.HasWaiter);
            Assert.Same(entry.Identity, replaced.WaiterIdentity);
        }
        finally
        {
            Monitor.Exit(owner.Sync);
        }

        Assert.Null(await rejectedRegistration.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(128, entry.RequiredSourceReservationBytes);
        Assert.Equal(0, Volatile.Read(ref entry.RegistrationsInFlight));

        admission.SetAvailableBytes(128);
        Assert.True(SpinWait.SpinUntil(() => IsFinished(entry), TimeSpan.FromSeconds(5)));
        Assert.Single(admission.WaitCalls);
        Assert.Equal(1, Volatile.Read(ref queued));
        owner.ClearSvgHostDeferral();
        GC.KeepAlive(owner);
    }

    [Fact]
    public async Task FirstRejectedRegistrationDoesNotSeedAcceptedDuplicateMetadataOrStrandWaiter()
    {
        var admission = new ThresholdAdmission();
        var demand = new MarkdownSvgRenderDemand(MarkdownSvgRenderPriority.Overscan);
        using var lifetime = new CancellationTokenSource();
        int queued = 0;
        SvgHostLoadDeferral owner = CreateLoadingOwner(_renderResources.Images, demand, lifetime.Token, _ =>
        {
            Interlocked.Increment(ref queued);
            return true;
        });
        SvgHostAdmissionDomain domain = DomainOf(_renderResources.Images, admission.DomainKey);

        // Pause the first denial's registration after the denial is recorded,
        // so the test can take the deferral lock before the row is published.
        Task domainReadHeld = admission.HoldNextDomainRead();
        Task rejectedDenial = Task.Run(() => Deny(owner, admission, demand, requiredBytes: 1024));
        await domainReadHeld.WaitAsync(TimeSpan.FromSeconds(5));

        SvgHostPendingEntry entry;
        Monitor.Enter(owner.Sync);
        try
        {
            admission.ReleaseDomainRead();
            Assert.True(SpinWait.SpinUntil(
                () => domain.GetPendingEntries() is [var row] &&
                    Volatile.Read(ref row.RegistrationsInFlight) == 1,
                TimeSpan.FromSeconds(5)));
            entry = Assert.Single(domain.GetPendingEntries());
            Assert.False(entry.HasAcceptedRegistration);

            // The first row's constructor values are provisional. While its
            // owner callback is blocked, a second real denial with a lower
            // threshold replaces the attempt and is the only registration the
            // owner accepts.
            Deny(owner, admission, demand, requiredBytes: 256);
            Assert.True(entry.HasAcceptedRegistration);
            Assert.Equal(256, entry.RequiredSourceReservationBytes);
            Assert.Equal(1, Volatile.Read(ref entry.RegistrationsInFlight));
            Assert.Equal(0, Volatile.Read(ref entry.WaiterStartState));
            SvgHostLoadDeferralState accepted = owner.GetState();
            Assert.True(accepted.HasWaiter);
            Assert.Same(entry.Identity, accepted.WaiterIdentity);
        }
        finally
        {
            Monitor.Exit(owner.Sync);
        }

        await rejectedDenial.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(entry.HasAcceptedRegistration);
        Assert.Equal(256, entry.RequiredSourceReservationBytes);
        Assert.Equal(0, Volatile.Read(ref entry.RegistrationsInFlight));
        Assert.Equal(2, Volatile.Read(ref entry.WaiterStartState));
        SvgHostLoadDeferralState settled = owner.GetState();
        Assert.True(settled.HasWaiter);
        Assert.Same(entry.Identity, settled.WaiterIdentity);
        Assert.True(SpinWait.SpinUntil(() => admission.WaitCalls.Length == 1, TimeSpan.FromSeconds(5)));
        Assert.Equal(256, admission.WaitCalls[0].RequiredBytes);

        admission.SetAvailableBytes(256);
        Assert.True(SpinWait.SpinUntil(() => IsFinished(entry), TimeSpan.FromSeconds(5)));
        Assert.Equal(1, Volatile.Read(ref queued));
        owner.ClearSvgHostDeferral();
        GC.KeepAlive(owner);
    }

    [Fact]
    public async Task AlreadyReadyThresholdSignalsWithoutCapacityVersionAdvance()
    {
        var admission = new ThresholdAdmission(initialAvailableBytes: 1024);
        var demand = new MarkdownSvgRenderDemand(MarkdownSvgRenderPriority.Overscan);
        using var lifetime = new CancellationTokenSource();
        int queued = 0;
        SvgHostLoadDeferral owner = CreateLoadingOwner(_renderResources.Images, demand, lifetime.Token, _ =>
        {
            Interlocked.Increment(ref queued);
            return true;
        });
        SvgHostAdmissionDomain domain = DomainOf(_renderResources.Images, admission.DomainKey);

        // Hold the registration's owner callback just long enough to observe
        // the row before its already-ready waiter claims and removes it.
        Task domainReadHeld = admission.HoldNextDomainRead();
        Task denial = Task.Run(() => Deny(owner, admission, demand, requiredBytes: 768));
        await domainReadHeld.WaitAsync(TimeSpan.FromSeconds(5));
        SvgHostPendingEntry entry;
        Monitor.Enter(owner.Sync);
        try
        {
            admission.ReleaseDomainRead();
            Assert.True(SpinWait.SpinUntil(
                () => domain.GetPendingEntries() is [var row] &&
                    Volatile.Read(ref row.RegistrationsInFlight) == 1,
                TimeSpan.FromSeconds(5)));
            entry = Assert.Single(domain.GetPendingEntries());
        }
        finally
        {
            Monitor.Exit(owner.Sync);
        }

        await denial.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(SpinWait.SpinUntil(() => IsFinished(entry), TimeSpan.FromSeconds(5)));

        Assert.Equal(0, admission.CapacityVersion);
        Assert.Single(admission.WaitCalls);
        Assert.Equal(768, admission.WaitCalls[0].RequiredBytes);
        Assert.Equal(1, Volatile.Read(ref queued));
        Assert.Equal(SvgHostDeferralPhase.RetryQueued, owner.GetState().Phase);
        owner.ClearSvgHostDeferral();
        GC.KeepAlive(owner);
    }

    [Fact]
    public async Task DuplicateRegistrationBarrierPreventsClaimUntilIdentityIsAccepted()
    {
        var admission = new ThresholdAdmission();
        var demand = new MarkdownSvgRenderDemand(MarkdownSvgRenderPriority.Overscan);
        using var lifetime = new CancellationTokenSource();
        var queued = new ConcurrentQueue<Action>();
        SvgHostLoadDeferral owner = CreateLoadingOwner(_renderResources.Images, demand, lifetime.Token, callback =>
        {
            queued.Enqueue(callback);
            return true;
        });
        SvgHostAdmissionDomain domain = DomainOf(_renderResources.Images, admission.DomainKey);

        Deny(owner, admission, demand, requiredBytes: 128);
        SvgHostLoadDeferralState denied = owner.GetState();
        SvgHostPendingEntry entry = PendingEntry(domain, denied.WaiterIdentity);
        object attempt = AttemptOf(owner);
        Assert.True(SpinWait.SpinUntil(() => admission.WaitCalls.Length == 1, TimeSpan.FromSeconds(5)));

        Task<SvgHostPendingWaiter?> secondTask;
        Monitor.Enter(owner.Sync);
        try
        {
            secondTask = Task.Run(() => domain.Defer(
                owner, admission, demand, 1, admission.CapacityVersion, 512, false,
                domain.Owners.OwnerVersion, demand.Version, 0, attempt, lifetime.Token));
            Assert.True(SpinWait.SpinUntil(() =>
                Volatile.Read(ref entry.RegistrationsInFlight) == 1,
                TimeSpan.FromSeconds(5)));

            // Make only the first threshold ready while the accepted identity
            // callback is blocked outside the domain gate.
            admission.SetAvailableBytes(128);
            Thread.Sleep(100);
            Assert.Equal(0, Volatile.Read(ref entry.Claimed));
            Assert.False(IsFinished(entry));
            Assert.Empty(queued);
        }
        finally
        {
            Monitor.Exit(owner.Sync);
        }

        SvgHostPendingWaiter second = Assert.IsType<
            SvgHostPendingWaiter>(
                await secondTask.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Same(entry.Identity, second.Identity);
        Assert.True(SpinWait.SpinUntil(() => admission.WaitCalls.Length == 2, TimeSpan.FromSeconds(5)));
        Assert.Equal(512, admission.WaitCalls[1].RequiredBytes);
        Assert.Equal(0, Volatile.Read(ref entry.Claimed));
        Assert.Empty(queued);

        admission.SetAvailableBytes(512);
        Assert.True(SpinWait.SpinUntil(() => IsFinished(entry), TimeSpan.FromSeconds(5)));
        Assert.Single(queued);
        Assert.Equal(0, Volatile.Read(ref entry.RegistrationsInFlight));
        second.Dispose();
        owner.ClearSvgHostDeferral();
        GC.KeepAlive(owner);
    }

    private static object AttemptOf(SvgHostLoadDeferral owner) =>
        owner.GetState().RegistrationAttempt
            ?? throw new InvalidOperationException("The denial kept no live registration attempt.");
}
