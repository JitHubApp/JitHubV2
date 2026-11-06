using System.Collections;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using MarkdownRenderer.Images;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using Xunit;
using static MarkdownRenderer.GitHub.Tests.SvgHostAdmissionFixture;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>
/// Checks the SVG host deferred registry: promotion of deferred retries,
/// cancellation and retirement draining before token sources are disposed,
/// and the bound on source-free and dead weak pending owners.
/// </summary>
// These tests exercise managed registry ownership only. Bare boxes are weak
// identity tokens; only managed deferred-ownership callbacks are called, never
// layout, paint, WinUI, provider rendering or device methods.
[Collection(AllocationSensitiveContractCollection.Name)]
public sealed class SvgHostDeferredRegistryTests
{
    private readonly MarkdownRenderResources _renderResources = new();

    [Fact]
    public async Task PromotionClaimsDeferredRetryWhenOverscanThresholdWaitRemainsPending()
    {
        var domain = new SvgHostAdmissionDomain();
        var admission = new NeverReadyAdmission();
        var owner = TestSvgHostDeferralHost.CreateDeferral(_renderResources.Images);
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

        SvgHostPendingWaiter waiter = Assert.IsType<
            SvgHostPendingWaiter>(domain.Defer(
                owner, admission, demand, 1, admission.CapacityVersion, 768, false,
                domain.Owners.OwnerVersion, demand.Version, 0, attempt, lifetime.Token));
        SetField(owner, "_svgHostDeferredWaiter", waiter);
        SvgHostPendingEntry entry = PendingEntryOf(waiter);
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
            owner.ClearSvgHostDeferral();
            lifetime.Cancel();
        }
        GC.KeepAlive(owner);
        GC.KeepAlive(admission);
    }

    [Fact]
    public async Task CancellationWaitsForInFlightRegistrationBeforeDisposingEntryTokenSource()
    {
        var domain = new SvgHostAdmissionDomain();
        var admission = new ThresholdAdmission();
        var owner = TestSvgHostDeferralHost.CreateDeferral(_renderResources.Images);
        var demand = new MarkdownSvgRenderDemand(MarkdownSvgRenderPriority.Overscan);
        using var lifetime = new CancellationTokenSource();
        var firstAttempt = new object();
        InitializeSourceDeferredOwner(owner, demand, firstAttempt, admission, requiredBytes: 128);

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
                owner, admission, demand, 1, admission.CapacityVersion, 256, false,
                domain.Owners.OwnerVersion, demand.Version, 0, secondAttempt, lifetime.Token));
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
        var domain = new SvgHostAdmissionDomain();
        var admission = new RegistryAdmission();
        SvgHostLoadDeferral owner = TestSvgHostDeferralHost.CreateDeferral(_renderResources.Images);
        var demand = new MarkdownSvgRenderDemand(MarkdownSvgRenderPriority.Overscan);
        using var lifetime = new CancellationTokenSource();
        var attempt = new object();
        InitializeDeferredOwner(owner, demand, attempt);
        var entry = new SvgHostPendingEntry(
            owner, admission, demand, 1, 0, 256, false, 0, demand.Version, 0, lifetime.Token);
        var waiter = new SvgHostPendingWaiter(domain, entry);
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
    public async Task SourceFreePendingPopulationIsBoundedAndCancellationDrainsEveryRow()
    {
        var domain = new SvgHostAdmissionDomain();
        var admission = new WaitingAdmission();
        var boxes = new SvgHostLoadDeferral[129];
        var demands = new MarkdownSvgRenderDemand[129];
        var attempts = new object[129];
        var handles = new SvgHostPendingWaiter?[129];
        using var lifetime = new CancellationTokenSource();
        try
        {
            for (int i = 0; i < boxes.Length; i++)
            {
                boxes[i] = TestSvgHostDeferralHost.CreateDeferral(_renderResources.Images);
                demands[i] = new MarkdownSvgRenderDemand(MarkdownSvgRenderPriority.Overscan);
                attempts[i] = new object();
                TestSvgHostDeferralHost.Of(boxes[i]).LoadDemand = demands[i];
                TestSvgHostDeferralHost.Of(boxes[i]).LoadDemandGeneration = 1;
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
            foreach (SvgHostPendingWaiter? handle in handles)
                handle?.Dispose();
            Assert.Empty(Field<ICollection>(domain, "_pending"));
            Assert.True(await Task.Run(() => SpinWait.SpinUntil(() =>
                handles.Take(128).All(handle => handle!.IsFinished), TimeSpan.FromSeconds(5))));
            Assert.All(demands.Take(128), demand => Assert.Equal(0, demand.PendingPromotionCount));
        }
        finally
        {
            foreach (SvgHostPendingWaiter? handle in handles)
                handle?.Dispose();
            await lifetime.CancelAsync();
            GC.KeepAlive(boxes);
            GC.KeepAlive(admission);
        }
    }

    [Fact]
    public async Task DeadWeakPendingOwnersCannotPermanentlyExhaustTheDeferredRegistry()
    {
        var domain = new SvgHostAdmissionDomain();
        var admission = new WaitingAdmission();
        using var lifetime = new CancellationTokenSource();
        (WeakReference[] weakOwners, SvgHostPendingWaiter[] handles) =
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
            SvgHostLoadDeferral replacementOwner = TestSvgHostDeferralHost.CreateDeferral(_renderResources.Images);
            var replacementDemand = new MarkdownSvgRenderDemand(MarkdownSvgRenderPriority.Overscan);
            object attempt = new();
            InitializeDeferredOwner(replacementOwner, replacementDemand, attempt);
            using SvgHostPendingWaiter replacement =
                Assert.IsType<SvgHostPendingWaiter>(domain.Defer(
                    replacementOwner, admission, replacementDemand, 1, 0, 128,
                    false, 0, 0, 0, attempt, lifetime.Token));
            Assert.Single(Field<ICollection>(domain, "_pending"));
            Assert.True(await Task.Run(() => SpinWait.SpinUntil(() =>
                handles.All(handle => handle.IsFinished), TimeSpan.FromSeconds(5))));
            GC.KeepAlive(replacementOwner);
        }
        finally
        {
            foreach (SvgHostPendingWaiter handle in handles)
                handle.Dispose();
            await lifetime.CancelAsync();
            GC.KeepAlive(admission);
        }
    }

    [Fact]
    public void FailedPromotionSubscriptionCannotLeaveAPendingRowOrRetainItsWeakOwner()
    {
        var domain = new SvgHostAdmissionDomain();
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
    private WeakReference CreateRejectedPendingOwner(
        SvgHostAdmissionDomain domain, IMarkdownSvgAdmission admission,
        MarkdownSvgRenderDemand demand, CancellationToken lifetime)
    {
        SvgHostLoadDeferral owner = TestSvgHostDeferralHost.CreateDeferral(_renderResources.Images);
        object attempt = new();
        InitializeDeferredOwner(owner, demand, attempt);
        Assert.Null(domain.Defer(owner, admission, demand, 1, 0, 128,
            false, 0, demand.Version, 0, attempt, lifetime));
        return new WeakReference(owner);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private (WeakReference[], SvgHostPendingWaiter[]) CreateWeakPendingOwners(
        SvgHostAdmissionDomain domain,
        IMarkdownSvgAdmission admission,
        CancellationToken lifetime)
    {
        var weakOwners = new WeakReference[128];
        var handles = new SvgHostPendingWaiter[128];
        var owners = new SvgHostLoadDeferral[128];
        for (int i = 0; i < weakOwners.Length; i++)
        {
            SvgHostLoadDeferral owner = TestSvgHostDeferralHost.CreateDeferral(_renderResources.Images);
            owners[i] = owner;
            var demand = new MarkdownSvgRenderDemand(MarkdownSvgRenderPriority.Overscan);
            object attempt = new();
            InitializeDeferredOwner(owner, demand, attempt);
            handles[i] = Assert.IsType<SvgHostPendingWaiter>(
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
