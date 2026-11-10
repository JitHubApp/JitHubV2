using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using MarkdownRenderer.Images;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Layout.Internal.Images.Svg.HostAdmission;
using Xunit;
using static MarkdownRenderer.GitHub.Tests.SvgHostAdmissionFixture;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>
/// Checks the SVG host deferred registry: promotion of deferred retries,
/// cancellation and retirement draining before token sources are disposed,
/// and the bound on source-free and dead weak pending owners.
/// </summary>
// These tests exercise managed registry ownership only. Every owner is a real
// SvgHostLoadDeferral over a managed host, denied through DeferSvgHostLoad and
// registered by the scope's admission coordinator; no layout, paint, WinUI,
// provider rendering or device methods are called.
[Collection(AllocationSensitiveContractCollection.Name)]
public sealed class SvgHostDeferredRegistryTests
{
    private readonly MarkdownRenderResources _renderResources = new();

    [Fact]
    public async Task PromotionClaimsDeferredRetryWhenOverscanThresholdWaitRemainsPending()
    {
        var admission = new NeverReadyAdmission();
        var demand = new MarkdownSvgRenderDemand(MarkdownSvgRenderPriority.Overscan);
        using var lifetime = new CancellationTokenSource();
        var queued = new ConcurrentQueue<Action>();
        SvgHostLoadDeferral owner = CreateLoadingOwner(_renderResources.Images, demand, lifetime.Token, callback =>
        {
            queued.Enqueue(callback);
            return true;
        });
        SvgHostAdmissionDomain domain = DomainOf(_renderResources.Images, admission.AdmissionDomain);
        try
        {
            Deny(owner, admission, demand, requiredBytes: 768);
            SvgHostLoadDeferralState denied = owner.GetState();
            Assert.Equal(SvgHostDeferralPhase.Waiting, denied.Phase);
            Assert.True(denied.HasWaiter);
            SvgHostPendingEntry entry = PendingEntry(domain, denied.WaiterIdentity);

            Task capacityWait = await admission.WaitStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(768, admission.RequiredBytes);
            Assert.Equal(MarkdownSvgRenderPriority.Overscan, admission.Priority);
            Assert.False(capacityWait.IsCompleted);
            Assert.Equal(0, Volatile.Read(ref entry.Claimed));

            Assert.True(demand.PromoteVisible());
            Assert.True(SpinWait.SpinUntil(
                () => IsFinished(entry) && queued.Count == 1,
                TimeSpan.FromSeconds(5)));

            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await capacityWait.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Single(queued);
            SvgHostLoadDeferralState retried = owner.GetState();
            Assert.Equal(SvgHostDeferralPhase.RetryQueued, retried.Phase);
            Assert.False(retried.CapacityReady);
            Assert.Same(entry.Identity, retried.WaiterIdentity);
            Assert.False(retried.HasWaiter);
            Assert.True(SpinWait.SpinUntil(
                () => IsCancellationDisposed(entry.Cancellation),
                TimeSpan.FromSeconds(5)));
        }
        finally
        {
            owner.ClearSvgHostDeferral();
            lifetime.Cancel();
        }
        GC.KeepAlive(owner);
        GC.KeepAlive(admission);
    }

    [Fact]
    public async Task CancellationWaitsForInFlightRegistrationBeforeDisposingEntryTokenSource()
    {
        var admission = new ThresholdAdmission();
        var demand = new MarkdownSvgRenderDemand(MarkdownSvgRenderPriority.Overscan);
        using var lifetime = new CancellationTokenSource();
        SvgHostLoadDeferral owner = CreateLoadingOwner(_renderResources.Images, demand, lifetime.Token);
        SvgHostAdmissionDomain domain = DomainOf(_renderResources.Images, admission.DomainKey);

        Deny(owner, admission, demand, requiredBytes: 128);
        SvgHostLoadDeferralState denied = owner.GetState();
        SvgHostPendingEntry entry = PendingEntry(domain, denied.WaiterIdentity);
        object attempt = denied.RegistrationAttempt ?? throw new InvalidOperationException("The denial kept no attempt.");
        Assert.True(SpinWait.SpinUntil(() => admission.WaitCalls.Length == 1, TimeSpan.FromSeconds(5)));

        Task<SvgHostPendingWaiter?> secondTask;
        Monitor.Enter(owner.Sync);
        try
        {
            // A duplicate registration of the denial's live attempt publishes
            // its identity, then waits for the deferral lock in its callback.
            secondTask = Task.Run(() => domain.Defer(
                owner, admission, demand, 1, admission.CapacityVersion, 256, false,
                domain.Owners.OwnerVersion, demand.Version, 0, attempt, lifetime.Token));
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
            Monitor.Exit(owner.Sync);
        }

        Assert.Null(await secondTask.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(SpinWait.SpinUntil(() => IsFinished(entry), TimeSpan.FromSeconds(5)));
        Assert.Equal(0, Volatile.Read(ref entry.RegistrationsInFlight));
        Assert.True(SpinWait.SpinUntil(
            () => IsCancellationDisposed(entry.Cancellation),
            TimeSpan.FromSeconds(5)));
        owner.ClearSvgHostDeferral();
        GC.KeepAlive(owner);
    }

    [Fact]
    public async Task UnstartedWaiterRetirementWaitsForRegistrationDrainBeforeDisposingTokenSource()
    {
        var admission = new ThresholdAdmission();
        var demand = new MarkdownSvgRenderDemand(MarkdownSvgRenderPriority.Overscan);
        using var lifetime = new CancellationTokenSource();
        SvgHostLoadDeferral owner = CreateLoadingOwner(_renderResources.Images, demand, lifetime.Token);
        SvgHostAdmissionDomain domain = DomainOf(_renderResources.Images, admission.DomainKey);

        // Pause the denial's registration after the denial is recorded, so the
        // test can take the deferral lock before the row is published.
        Task domainReadHeld = admission.HoldNextDomainRead();
        Task denial = Task.Run(() => Deny(owner, admission, demand, requiredBytes: 256));
        await domainReadHeld.WaitAsync(TimeSpan.FromSeconds(5));

        SvgHostPendingEntry entry;
        Monitor.Enter(owner.Sync);
        try
        {
            // The registration publishes a fresh row, then waits for the
            // deferral lock in its owner callback, before any waiter starts.
            admission.ReleaseDomainRead();
            Assert.True(SpinWait.SpinUntil(
                () => domain.GetPendingEntries() is [var row] &&
                    Volatile.Read(ref row.RegistrationsInFlight) == 1,
                TimeSpan.FromSeconds(5)));
            entry = Assert.Single(domain.GetPendingEntries());
            Assert.Equal(0, Volatile.Read(ref entry.WaiterStartState));

            // Retire the row while that registration still owns it. This handle
            // stands in for the registration's own, which a rejected
            // registration releases before the registration ends.
            new SvgHostPendingWaiter(domain, entry).Dispose();

            Assert.Empty(domain.GetPendingEntries());
            Assert.True(entry.Cancellation.IsCancellationRequested);
            Assert.False(IsCancellationDisposed(entry.Cancellation));
            Assert.Equal(1, Volatile.Read(ref entry.RegistrationsInFlight));
            Assert.False(IsFinished(entry));
        }
        finally
        {
            Monitor.Exit(owner.Sync);
        }

        await denial.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(SpinWait.SpinUntil(
            () => IsCancellationDisposed(entry.Cancellation),
            TimeSpan.FromSeconds(5)));
        Assert.Equal(1, Volatile.Read(ref entry.WaiterCompletionStarted));
        Assert.Equal(0, Volatile.Read(ref entry.RegistrationsInFlight));
        SvgHostLoadDeferralState settled = owner.GetState();
        Assert.Equal(SvgHostDeferralPhase.Waiting, settled.Phase);
        Assert.False(settled.HasWaiter);
        Assert.Null(settled.WaiterIdentity);
        Assert.Null(settled.RegistrationAttempt);
        Assert.Empty(domain.GetPendingEntries());
        Assert.Empty(admission.WaitCalls);
        owner.ClearSvgHostDeferral();
        GC.KeepAlive(owner);
    }

    [Fact]
    public async Task SourceFreePendingPopulationIsBoundedAndCancellationDrainsEveryRow()
    {
        var admission = new WaitingAdmission();
        SvgHostAdmissionDomain domain = DomainOf(_renderResources.Images, admission.AdmissionDomain);
        var owners = new SvgHostLoadDeferral?[129];
        var demands = new MarkdownSvgRenderDemand[129];
        using var lifetime = new CancellationTokenSource();
        try
        {
            for (int i = 0; i < owners.Length; i++)
            {
                demands[i] = new MarkdownSvgRenderDemand(MarkdownSvgRenderPriority.Overscan);
                SvgHostLoadDeferral owner = CreateLoadingOwner(_renderResources.Images, demands[i], lifetime.Token);
                owners[i] = owner;
                Deny(owner, admission, demands[i], requiredBytes: 128);
                SvgHostLoadDeferralState state = owner.GetState();
                Assert.Equal(SvgHostDeferralPhase.Waiting, state.Phase);
                // The 129th denial finds the table full and keeps only scalar state.
                Assert.Equal(i < 128, state.HasWaiter);
            }
            SvgHostPendingEntry[] entries = domain.GetPendingEntries();
            Assert.Equal(128, entries.Length);
            foreach (SvgHostLoadDeferral? owner in owners)
                owner?.ClearSvgHostDeferral();
            Assert.Empty(domain.GetPendingEntries());
            Assert.True(await Task.Run(() => SpinWait.SpinUntil(() =>
                entries.All(IsFinished), TimeSpan.FromSeconds(5))));
            Assert.All(demands.Take(128), demand => Assert.Equal(0, demand.PendingPromotionCount));
        }
        finally
        {
            foreach (SvgHostLoadDeferral? owner in owners)
                owner?.ClearSvgHostDeferral();
            await lifetime.CancelAsync();
            GC.KeepAlive(owners);
            GC.KeepAlive(admission);
        }
    }

    [Fact]
    public async Task DeadWeakPendingOwnersCannotPermanentlyExhaustTheDeferredRegistry()
    {
        var admission = new WaitingAdmission();
        SvgHostAdmissionDomain domain = DomainOf(_renderResources.Images, admission.AdmissionDomain);
        using var lifetime = new CancellationTokenSource();
        (WeakReference[] weakOwners, SvgHostPendingEntry[] entries) =
            CreateWeakPendingOwners(domain, admission, lifetime.Token);
        SvgHostLoadDeferral? replacementOwner = null;
        try
        {
            for (int i = 0; i < 10 && weakOwners.Any(weak => weak.IsAlive); i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
            }
            Assert.All(weakOwners, weak => Assert.False(weak.IsAlive));
            var replacementDemand = new MarkdownSvgRenderDemand(MarkdownSvgRenderPriority.Overscan);
            replacementOwner = CreateLoadingOwner(_renderResources.Images, replacementDemand, lifetime.Token);
            Deny(replacementOwner, admission, replacementDemand, requiredBytes: 128);
            Assert.True(replacementOwner.GetState().HasWaiter);
            Assert.Single(domain.GetPendingEntries());
            Assert.True(await Task.Run(() => SpinWait.SpinUntil(() =>
                entries.All(IsFinished), TimeSpan.FromSeconds(5))));
            GC.KeepAlive(replacementOwner);
        }
        finally
        {
            replacementOwner?.ClearSvgHostDeferral();
            await lifetime.CancelAsync();
            GC.KeepAlive(admission);
        }
    }

    [Fact]
    public void FailedPromotionSubscriptionCannotLeaveAPendingRowOrRetainItsWeakOwner()
    {
        var admission = new WaitingAdmission();
        SvgHostAdmissionDomain domain = DomainOf(_renderResources.Images, admission.AdmissionDomain);
        using var lifetime = new CancellationTokenSource();
        var demand = new MarkdownSvgRenderDemand(MarkdownSvgRenderPriority.Visible,
            () => throw new InvalidOperationException("Reject injected promotion scheduling."));
        WeakReference weak = CreateRejectedPendingOwner(admission, demand, lifetime.Token);
        Assert.Empty(domain.GetPendingEntries());
        Assert.Equal(0, demand.PendingPromotionCount);
        Assert.Equal(0, demand.ActiveRegistrationCount);
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
    private WeakReference CreateRejectedPendingOwner(
        IMarkdownSvgAdmission admission,
        MarkdownSvgRenderDemand demand,
        CancellationToken lifetime)
    {
        SvgHostLoadDeferral owner = CreateLoadingOwner(_renderResources.Images, demand, lifetime);
        Deny(owner, admission, demand, requiredBytes: 128);
        // The waiter failed to start, so the coordinator returned no handle
        // and the owner keeps only its scalar denial.
        SvgHostLoadDeferralState state = owner.GetState();
        Assert.Equal(SvgHostDeferralPhase.Waiting, state.Phase);
        Assert.False(state.HasWaiter);
        Assert.Null(state.WaiterIdentity);
        return new WeakReference(owner);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private (WeakReference[], SvgHostPendingEntry[]) CreateWeakPendingOwners(
        SvgHostAdmissionDomain domain,
        IMarkdownSvgAdmission admission,
        CancellationToken lifetime)
    {
        var weakOwners = new WeakReference[128];
        var owners = new SvgHostLoadDeferral[128];
        for (int i = 0; i < weakOwners.Length; i++)
        {
            var demand = new MarkdownSvgRenderDemand(MarkdownSvgRenderPriority.Overscan);
            SvgHostLoadDeferral owner = CreateLoadingOwner(_renderResources.Images, demand, lifetime);
            owners[i] = owner;
            Deny(owner, admission, demand, requiredBytes: 128);
            Assert.True(owner.GetState().HasWaiter);
            weakOwners[i] = new WeakReference(owner);
        }
        // Hold the entire setup population until its full-capacity precondition
        // is asserted. Concurrent GC tests must not make earlier weak owners
        // eligible for pruning while this loop is still admitting later rows.
        SvgHostPendingEntry[] entries = domain.GetPendingEntries();
        Assert.Equal(128, entries.Length);
        GC.KeepAlive(owners);
        return (weakOwners, entries);
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
}
