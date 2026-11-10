using System.Collections.Concurrent;
using MarkdownRenderer.GitHub.Tests.TestSupport.Fakes;
using MarkdownRenderer.Images;
using MarkdownRenderer.Layout.Internal.Images.Svg.HostAdmission;
using MarkdownRenderer.Layout.Internal.RenderResources;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

// These managed-owner tests never construct a XAML object, load images,
// create a device or invoke a native queue. Each denial is recorded through
// the real deferral and the scope's managed admission coordinator; injected
// retry queues retain or reject callbacks.
public sealed class SvgHostRetryReadinessTests : IDisposable
{
    private const int Generation = 7;
    private const long DeniedCapacityVersion = 11;
    private const long RequiredSourceBytes = 73728;

    private readonly MarkdownRenderResources _renderResources = new();
    private readonly Dictionary<SvgHostLoadDeferral, TestSvgHostDeferralHost> _owners = new();

    public void Dispose()
    {
        // Release the coordinator waiters the denials registered.
        foreach (SvgHostLoadDeferral owner in _owners.Keys)
            owner.ClearSvgHostDeferral();
    }

    [Fact]
    public async Task ObservedSelfReleaseReregistersOnlyTheSourceFreeThresholdWait()
    {
        var queue = new RetryQueueProbe();
        var queued = new ConcurrentQueue<Action>();
        queue.Handler = work => { queued.Enqueue(work); return true; };
        using var lifetime = new CancellationTokenSource();
        var admission = new ThresholdProbe { CapacityVersion = DeniedCapacityVersion, FailNextDomainRead = true };
        SvgHostLoadDeferral owner = CreateOwner(queue);
        TestSvgHostDeferralHost host = HostOf(owner);
        host.Cancellation = lifetime.Token;
        MarkdownSvgRenderDemand demand = host.LoadDemand!;
        try
        {
            // The bounded waiter cannot be registered, so only the scalar
            // denial remains on the box.
            owner.DeferSvgHostLoad(Denial(admission, demand));
            SvgHostLoadDeferralState denied = owner.GetState();
            Assert.Equal(SvgHostDeferralPhase.Waiting, denied.Phase);
            Assert.False(denied.HasWaiter);
            Assert.Null(denied.WaiterIdentity);
            Assert.NotNull(denied.DenialIdentity);
            Assert.Equal(DeniedCapacityVersion, denied.CapacityVersion);
            Assert.Equal(0, admission.WaitCount);

            admission.CapacityVersion = 12; // A self-release pulse.
            owner.TryResumeDeferredSvgLoadForObservedDemandOrCapacity(demand);
            await admission.WaitStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Empty(queued);
            Assert.Equal(1, admission.WaitCount);
            Assert.Equal(RequiredSourceBytes, admission.RequiredBytes);
            Assert.Equal(MarkdownSvgRenderPriority.Overscan, admission.Priority);
            SvgHostLoadDeferralState reregistered = owner.GetState();
            Assert.Equal(12, reregistered.CapacityVersion);
            Assert.False(reregistered.CapacityReady);
            Assert.Equal(SvgHostDeferralPhase.Waiting, reregistered.Phase);

            admission.Ready.TrySetResult(); // No additional numeric version change.
            Assert.True(SpinWait.SpinUntil(() => queued.Count == 1, TimeSpan.FromSeconds(5)));
            Assert.Equal(1, admission.WaitCount);
            Assert.Equal(0, admission.OpenCount);
            SvgHostLoadDeferralState ready = owner.GetState();
            Assert.True(ready.CapacityReady);
            Assert.Equal(SvgHostDeferralPhase.RetryQueued, ready.Phase);
        }
        finally
        {
            owner.ClearSvgHostDeferral();
            await lifetime.CancelAsync();
        }
        GC.KeepAlive(admission);
        GC.KeepAlive(owner);
    }

    [Fact]
    public void NumericCapacityPulseAloneCannotRetryAnInsufficientSourceDenial()
    {
        var queue = new RetryQueueProbe();
        var (owner, demand, identity) = CreateWaiting(queue);
        owner.TryQueueDeferredSvgHostRetry(demand, Generation, 0, 12, 0);
        Assert.Equal(0, queue.Calls);
        SvgHostLoadDeferralState state = owner.GetState();
        Assert.Equal(SvgHostDeferralPhase.Waiting, state.Phase);
        Assert.False(state.CapacityReady);
        Assert.Same(identity, state.WaiterIdentity);
        Assert.True(state.HasWaiter);
    }

    [Fact]
    public void AcceptedThresholdCanRetryWithoutAdvancingNumericCapacityVersion()
    {
        var queue = new RetryQueueProbe();
        var (owner, demand, identity) = CreateWaiting(queue);
        owner.TryQueueDeferredSvgHostRetry(demand, Generation, 0, 11, 0,
            capacityThresholdSatisfied: true, waiterIdentity: identity);
        Assert.Equal(1, queue.Calls);
        SvgHostLoadDeferralState state = owner.GetState();
        Assert.Equal(SvgHostDeferralPhase.RetryQueued, state.Phase);
        Assert.Equal(11, state.CapacityVersion);
        Assert.True(state.CapacityReady);
        Assert.False(state.HasWaiter);
        owner.OnSvgHostAdmissionWaiterFinished(demand, Generation, identity);
        Assert.Same(identity, owner.GetState().WaiterIdentity);
    }

    [Fact]
    public void OldWaiterThresholdCannotQueueAReplacementWithTheSameDemandAndGeneration()
    {
        var queue = new RetryQueueProbe();
        var (owner, demand, identity) = CreateWaiting(queue);
        // A replacement denial for the same demand and generation registers
        // a new waiter and retires the old one.
        owner.DeferSvgHostLoad(Denial(new ThresholdProbe { CapacityVersion = DeniedCapacityVersion }, demand));
        object replacement = Assert.IsType<object>(owner.GetState().WaiterIdentity);
        Assert.NotSame(identity, replacement);

        owner.TryQueueDeferredSvgHostRetry(demand, Generation, 0, 11, 0,
            capacityThresholdSatisfied: true, waiterIdentity: identity);
        Assert.Equal(0, queue.Calls);
        SvgHostLoadDeferralState state = owner.GetState();
        Assert.Equal(SvgHostDeferralPhase.Waiting, state.Phase);
        Assert.False(state.CapacityReady);
        Assert.Same(replacement, state.WaiterIdentity);
    }

    [Fact]
    public void SourceThresholdCannotSatisfyAnOwnerSlotDenial()
    {
        var queue = new RetryQueueProbe();
        var (owner, demand, identity) = CreateWaiting(queue, requiresOwnerSlot: true);
        Assert.True(owner.GetState().RequiresOwnerSlot);
        owner.TryQueueDeferredSvgHostRetry(demand, Generation, 0, 11, 0,
            capacityThresholdSatisfied: true, waiterIdentity: identity);
        Assert.Equal(0, queue.Calls);
        Assert.False(owner.GetState().CapacityReady);
        owner.TryQueueDeferredSvgHostRetry(demand, Generation, 0, 11, 1);
        Assert.Equal(1, queue.Calls);
    }

    [Fact]
    public void DualQueueRejectionRetainsThresholdForOneLaterObservation()
    {
        var queue = new RetryQueueProbe();
        queue.Handler = _ => queue.Calls > 2;
        var (owner, demand, identity) = CreateWaiting(queue);
        owner.TryQueueDeferredSvgHostRetry(demand, Generation, 0, 11, 0,
            capacityThresholdSatisfied: true, waiterIdentity: identity);
        Assert.Equal(2, queue.Calls);
        Assert.Equal(SvgHostDeferralPhase.Waiting, owner.GetState().Phase);
        owner.OnSvgHostAdmissionWaiterFinished(demand, Generation, identity);
        SvgHostLoadDeferralState retained = owner.GetState();
        Assert.True(retained.CapacityReady);
        Assert.Same(identity, retained.WaiterIdentity);
        owner.TryQueueDeferredSvgHostRetry(demand, Generation, 0, 11, 0);
        owner.TryQueueDeferredSvgHostRetry(demand, Generation, 0, 11, 0);
        Assert.Equal(3, queue.Calls);
        Assert.Equal(SvgHostDeferralPhase.RetryQueued, owner.GetState().Phase);
    }

    [Fact]
    public void LateQueuedWorkCannotClearANewerDenialForTheSameDemandAndGeneration()
    {
        var queue = new RetryQueueProbe();
        var queued = new List<Action>();
        queue.Handler = work => { queued.Add(work); return true; };
        var (owner, demand, identity) = CreateWaiting(queue);
        TestSvgHostDeferralHost host = HostOf(owner);
        owner.TryQueueDeferredSvgHostRetry(demand, Generation, 0, 11, 0,
            capacityThresholdSatisfied: true, waiterIdentity: identity);
        Action oldWork = Assert.Single(queued);

        // A newer denial of the same demand and generation reaches its own
        // queued retry before the old callback runs.
        owner.DeferSvgHostLoad(Denial(new ThresholdProbe { CapacityVersion = DeniedCapacityVersion }, demand));
        SvgHostLoadDeferralState newer = owner.GetState();
        object newerDenial = Assert.IsType<object>(newer.DenialIdentity);
        owner.TryQueueDeferredSvgHostRetry(demand, Generation, 0, 11, 0,
            capacityThresholdSatisfied: true, waiterIdentity: newer.WaiterIdentity);
        Assert.Equal(2, queued.Count);

        oldWork();
        SvgHostLoadDeferralState state = owner.GetState();
        Assert.Equal(SvgHostDeferralPhase.RetryQueued, state.Phase);
        Assert.Same(newerDenial, state.DenialIdentity);
        Assert.True(state.CapacityReady);
        Assert.Equal(0, host.StartLoadCount);

        // The newer denial's own retry still starts the load.
        queued[1]();
        Assert.Equal(1, host.StartLoadCount);
        Assert.Equal(SvgHostDeferralPhase.None, owner.GetState().Phase);
    }

    [Fact]
    public void DelayedRetryCannotStartAReplacementLoadGenerationWithTheSameDemandAndDenialIdentity()
    {
        var queue = new RetryQueueProbe();
        Action? queued = null;
        queue.Handler = work => { queued = work; return true; };
        var (owner, demand, identity) = CreateWaiting(queue);
        TestSvgHostDeferralHost host = HostOf(owner);
        owner.TryQueueDeferredSvgHostRetry(demand, Generation, 0, 11, 0,
            capacityThresholdSatisfied: true, waiterIdentity: identity);
        Assert.NotNull(queued);
        Assert.Equal(SvgHostDeferralPhase.RetryQueued, owner.GetState().Phase);

        // Keep demand and denial identity equal deliberately: the generation
        // fence itself must reject an old queued callback before StartLoad.
        host.LoadDemandGeneration = Generation + 1;
        queued!();

        Assert.Equal(Generation + 1, host.LoadDemandGeneration);
        Assert.Equal(0, host.StartLoadCount);
        SvgHostLoadDeferralState state = owner.GetState();
        Assert.Equal(SvgHostDeferralPhase.RetryQueued, state.Phase);
        Assert.True(state.CapacityReady);
        Assert.Same(identity, state.WaiterIdentity);
    }

    [Fact]
    public void LateThresholdCannotQueueANewerGenerationEvenWhenDemandAndWaiterIdentityMatch()
    {
        var queue = new RetryQueueProbe();
        var (owner, demand, identity) = CreateWaiting(queue);
        HostOf(owner).LoadDemandGeneration = Generation + 1;

        owner.TryQueueDeferredSvgHostRetry(demand, Generation, 0, 11, 0,
            capacityThresholdSatisfied: true, waiterIdentity: identity);

        Assert.Equal(0, queue.Calls);
        SvgHostLoadDeferralState state = owner.GetState();
        Assert.Equal(SvgHostDeferralPhase.Waiting, state.Phase);
        Assert.False(state.CapacityReady);
        Assert.Same(identity, state.WaiterIdentity);
    }

    [Fact]
    public void ReentrantQueueFailureCannotRollBackANewerDenial()
    {
        var queue = new RetryQueueProbe();
        SvgHostLoadDeferral? reentrantOwner = null;
        MarkdownSvgRenderDemand? reentrantDemand = null;
        object? newerDenial = null;
        queue.Handler = _ =>
        {
            if (queue.Calls == 1)
            {
                // While the primary queue attempt runs outside the lock, a
                // newer denial of the same demand and generation is recorded
                // and its own retry is accepted (the second queue call).
                reentrantOwner!.DeferSvgHostLoad(Denial(
                    new ThresholdProbe { CapacityVersion = DeniedCapacityVersion }, reentrantDemand!));
                SvgHostLoadDeferralState newer = reentrantOwner.GetState();
                newerDenial = newer.DenialIdentity;
                reentrantOwner.TryQueueDeferredSvgHostRetry(reentrantDemand!, Generation, 0, 11, 0,
                    capacityThresholdSatisfied: true, waiterIdentity: newer.WaiterIdentity);
                return false;
            }
            return queue.Calls == 2;
        };
        var (owner, demand, identity) = CreateWaiting(queue);
        reentrantOwner = owner;
        reentrantDemand = demand;
        object originalDenial = Assert.IsType<object>(owner.GetState().DenialIdentity);

        owner.TryQueueDeferredSvgHostRetry(demand, Generation, 0, 11, 0,
            capacityThresholdSatisfied: true, waiterIdentity: identity);

        // The old denial's primary and fallback attempts, with the newer
        // denial's accepted retry between them.
        Assert.Equal(3, queue.Calls);
        SvgHostLoadDeferralState state = owner.GetState();
        Assert.Equal(SvgHostDeferralPhase.RetryQueued, state.Phase);
        Assert.NotNull(newerDenial);
        Assert.NotSame(originalDenial, newerDenial);
        Assert.Same(newerDenial, state.DenialIdentity);
    }

    [Fact]
    public void ThrowingPrimaryQueueUsesTheFallbackOnce()
    {
        var queue = new RetryQueueProbe();
        queue.Handler = _ => queue.Calls == 1 ? throw new InvalidOperationException("primary queue") : true;
        var (owner, demand, identity) = CreateWaiting(queue);
        owner.TryQueueDeferredSvgHostRetry(demand, Generation, 0, 11, 0,
            capacityThresholdSatisfied: true, waiterIdentity: identity);
        Assert.Equal(2, queue.Calls);
        SvgHostLoadDeferralState state = owner.GetState();
        Assert.Equal(SvgHostDeferralPhase.RetryQueued, state.Phase);
        Assert.True(state.CapacityReady);
    }

    [Fact]
    public void ThrowingFallbackRestoresWaitingAndExplicitDeferralClearRetiresReadiness()
    {
        var queue = new RetryQueueProbe();
        queue.Handler = _ => throw new InvalidOperationException("queue");
        var (owner, demand, identity) = CreateWaiting(queue);
        owner.TryQueueDeferredSvgHostRetry(demand, Generation, 0, 11, 0,
            capacityThresholdSatisfied: true, waiterIdentity: identity);
        Assert.Equal(2, queue.Calls);
        SvgHostLoadDeferralState restored = owner.GetState();
        Assert.Equal(SvgHostDeferralPhase.Waiting, restored.Phase);
        Assert.True(restored.CapacityReady);
        owner.ClearSvgHostDeferral();
        owner.TryQueueDeferredSvgHostRetry(demand, Generation, 0, 11, 0,
            capacityThresholdSatisfied: true, waiterIdentity: identity);
        Assert.Equal(2, queue.Calls);
        SvgHostLoadDeferralState cleared = owner.GetState();
        Assert.Equal(SvgHostDeferralPhase.None, cleared.Phase);
        Assert.False(cleared.CapacityReady);
    }

    [Fact]
    public void ThresholdWithoutAnAcceptedWaiterIdentityCannotQueue()
    {
        var queue = new RetryQueueProbe();
        var (owner, demand, _) = CreateWaiting(queue);
        owner.TryQueueDeferredSvgHostRetry(demand, Generation, 0, 12, 0,
            capacityThresholdSatisfied: true);
        Assert.Equal(0, queue.Calls);
        SvgHostLoadDeferralState state = owner.GetState();
        Assert.Equal(SvgHostDeferralPhase.Waiting, state.Phase);
        Assert.False(state.CapacityReady);
    }

    [Fact]
    public void FinishedUnreadyWaiterRetiresIdentityWithoutClearingItsScalarDenial()
    {
        var (owner, demand, identity) = CreateWaiting(new RetryQueueProbe());
        object denial = Assert.IsType<object>(owner.GetState().DenialIdentity);
        owner.OnSvgHostAdmissionWaiterFinished(demand, Generation, identity);
        SvgHostLoadDeferralState state = owner.GetState();
        Assert.Equal(SvgHostDeferralPhase.Waiting, state.Phase);
        Assert.False(state.CapacityReady);
        Assert.False(state.HasWaiter);
        Assert.Null(state.WaiterIdentity);
        Assert.Null(state.RegistrationAttempt);
        Assert.Same(denial, state.DenialIdentity);
    }

    [Fact]
    public void ClearingDeferralRetiresEveryScalarReadinessIdentity()
    {
        var (owner, demand, identity) = CreateWaiting(new RetryQueueProbe());
        owner.TryQueueDeferredSvgHostRetry(demand, Generation, 0, 11, 0,
            capacityThresholdSatisfied: true, waiterIdentity: identity);
        owner.ClearSvgHostDeferral();
        SvgHostLoadDeferralState state = owner.GetState();
        Assert.Equal(SvgHostDeferralPhase.None, state.Phase);
        Assert.False(state.CapacityReady);
        Assert.Null(state.DenialIdentity);
        Assert.Null(state.WaiterIdentity);
        Assert.Null(state.RegistrationAttempt);
        Assert.True(owner.IsClear());
    }

    // Records a real source-threshold (or owner-slot) denial whose bounded
    // waiter is registered with the scope's coordinator and never signals on
    // its own. The registration attempt is the denial identity; the waiter
    // identity is distinct from it.
    private (SvgHostLoadDeferral, MarkdownSvgRenderDemand, object) CreateWaiting(
        RetryQueueProbe queue,
        bool requiresOwnerSlot = false)
    {
        SvgHostLoadDeferral owner = CreateOwner(queue);
        MarkdownSvgRenderDemand demand = HostOf(owner).LoadDemand!;
        owner.DeferSvgHostLoad(Denial(
            new ThresholdProbe { CapacityVersion = DeniedCapacityVersion }, demand, requiresOwnerSlot));
        SvgHostLoadDeferralState state = owner.GetState();
        Assert.Equal(SvgHostDeferralPhase.Waiting, state.Phase);
        Assert.True(state.HasWaiter);
        Assert.Equal(DeniedCapacityVersion, state.CapacityVersion);
        Assert.Equal(RequiredSourceBytes, state.RequiredSourceBytes);
        Assert.False(state.CapacityReady);
        object waiterIdentity = Assert.IsType<object>(state.WaiterIdentity);
        object denialIdentity = Assert.IsType<object>(state.DenialIdentity);
        Assert.Same(denialIdentity, state.RegistrationAttempt);
        Assert.NotSame(denialIdentity, waiterIdentity);
        Assert.Equal(0, queue.Calls);
        return (owner, demand, waiterIdentity);
    }

    private SvgHostLoadDeferral CreateOwner(RetryQueueProbe queue)
    {
        var host = new TestSvgHostDeferralHost
        {
            LoadDemand = new MarkdownSvgRenderDemand(MarkdownSvgRenderPriority.Overscan),
            LoadDemandGeneration = Generation,
        };
        var owner = new SvgHostLoadDeferral(host, _renderResources.Images, queue.TryEnqueue);
        _owners.Add(owner, host);
        return owner;
    }

    private TestSvgHostDeferralHost HostOf(SvgHostLoadDeferral owner) => _owners[owner];

    private static SvgHostCapacityDeferral Denial(
        ThresholdProbe admission,
        MarkdownSvgRenderDemand demand,
        bool requiresOwnerSlot = false) =>
        new(admission, demand, Generation, admission.CapacityVersion, RequiredSourceBytes,
            requiresOwnerSlot, OwnerVersionAtDenial: 0,
            DemandVersionAtDenial: demand.Version, VisualDemandEpochAtDenial: 0);

    /// <summary>An injected retry queue that counts attempts and delegates to <see cref="Handler"/>.</summary>
    private sealed class RetryQueueProbe
    {
        internal Func<Action, bool> Handler { get; set; } = static _ => true;
        internal int Calls { get; private set; }

        internal bool TryEnqueue(Action work)
        {
            Calls++;
            return Handler(work);
        }
    }

    private sealed class ThresholdProbe : IMarkdownSvgAdmission
    {
        private readonly object _admissionDomain = new();
        private long _capacityVersion;
        private int _failNextDomainRead;

        // A failed domain read is how a bounded waiter registration fails.
        public object AdmissionDomain => Interlocked.Exchange(ref _failNextDomainRead, 0) != 0
            ? throw new InvalidOperationException("The bounded waiter could not be registered.")
            : _admissionDomain;

        public long CapacityVersion
        {
            get => Volatile.Read(ref _capacityVersion);
            set => Volatile.Write(ref _capacityVersion, value);
        }

        internal bool FailNextDomainRead
        {
            init => _failNextDomainRead = value ? 1 : 0;
        }

        internal readonly TaskCompletionSource WaitStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int WaitCount;
        internal int OpenCount;
        internal long RequiredBytes;
        internal MarkdownSvgRenderPriority Priority;

        public Task WaitForCapacityAsync(long requiredFreeBytes, MarkdownSvgRenderPriority priority,
            CancellationToken cancellationToken)
        {
            RequiredBytes = requiredFreeBytes;
            Priority = priority;
            Interlocked.Increment(ref WaitCount);
            WaitStarted.TrySetResult();
            return Ready.Task.WaitAsync(cancellationToken);
        }

        public ValueTask<MarkdownSvgAdmissionResult> TryOpenAsync(MarkdownSvgOpenRequest request,
            MarkdownSvgRenderDemand demand, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref OpenCount);
            throw new InvalidOperationException("A scalar retry observation must not open source bytes.");
        }
    }
}
