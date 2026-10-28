using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Reflection;
using MarkdownRenderer.Images;
using MarkdownRenderer.Performance;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

public sealed class BoundedMarkdownImageActivationSchedulerDemandPromotionTests
{
    [Fact]
    public async Task DelayedPromotionPreservesArrivalOrderAndEvictsOnlyStillOverscan()
    {
        using var scheduler = new BoundedMarkdownImageActivationScheduler(capacity: 4, workerCount: 1);
        using var activeEntered = new ManualResetEventSlim();
        using var releaseActive = new ManualResetEventSlim();
        var started = new ConcurrentQueue<string>();
        int evicted = 0;

        Assert.True(scheduler.TrySchedule(VisibleDemand(), () =>
        {
            activeEntered.Set();
            releaseActive.Wait(TimeSpan.FromSeconds(5));
            started.Enqueue("active");
        }, static () => { }, CancellationToken.None));
        Assert.True(activeEntered.Wait(TimeSpan.FromSeconds(5)));

        Assert.True(scheduler.TrySchedule(VisibleDemand(), () => started.Enqueue("visible-old"),
            static () => { }, CancellationToken.None));
        var promotedOlder = new FakeDemand(isVisible: false);
        Assert.True(scheduler.TrySchedule(promotedOlder, () => started.Enqueue("promoted-older"),
            static () => { }, CancellationToken.None));
        Assert.True(scheduler.TrySchedule(VisibleDemand(), () => started.Enqueue("visible-newer"),
            static () => { }, CancellationToken.None));
        Assert.True(scheduler.TrySchedule(OverscanDemand(), () => started.Enqueue("evict-me"),
            () => Interlocked.Increment(ref evicted), CancellationToken.None));

        // The async notification is deliberately held. The monotonic demand bit
        // itself must be reconciled before an incoming visible item picks a victim.
        promotedOlder.Promote(deliver: false);
        Assert.True(scheduler.TrySchedule(VisibleDemand(), () => started.Enqueue("visible-last"),
            static () => { }, CancellationToken.None));
        Assert.Equal(4, scheduler.QueuedCount);
        Assert.Equal(0, scheduler.VisibleWaitingCount);
        Assert.Equal(1, Volatile.Read(ref evicted));

        promotedOlder.DeliverClaimedPromotion(); // stale after synchronous queue reconciliation
        try
        {
            releaseActive.Set();
            Assert.True(SpinWait.SpinUntil(() => started.Count == 5, TimeSpan.FromSeconds(5)));
            Assert.Equal(new[]
            {
                "active", "visible-old", "promoted-older", "visible-newer", "visible-last",
            }, started.ToArray());
        }
        finally
        {
            releaseActive.Set();
            scheduler.Dispose();
        }
        await scheduler.Completion.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task PromotionCallbackCanStartBeforeRegistrationReturnsWithoutSeeingPartialQueueState()
    {
        using var scheduler = new BoundedMarkdownImageActivationScheduler(capacity: 2, workerCount: 1);
        using var activeEntered = new ManualResetEventSlim();
        using var releaseActive = new ManualResetEventSlim();
        int starts = 0;
        int notStarted = 0;

        Assert.True(scheduler.TrySchedule(VisibleDemand(), () =>
        {
            activeEntered.Set();
            releaseActive.Wait(TimeSpan.FromSeconds(5));
        }, static () => { }, CancellationToken.None));
        Assert.True(activeEntered.Wait(TimeSpan.FromSeconds(5)));

        var demand = new FakeDemand(isVisible: false)
        {
            PromoteDuringNextRegistration = true,
            StartCallbackBeforeRegistrationReturns = true,
        };
        Assert.True(scheduler.TrySchedule(demand,
            () =>
            {
                Assert.True(demand.IsVisible);
                Interlocked.Increment(ref starts);
            },
            () => Interlocked.Increment(ref notStarted),
            CancellationToken.None));

        Assert.True(demand.CallbackBeganBeforeRegistrationReturned);
        Assert.Equal(1, scheduler.QueuedCount);
        Assert.InRange(demand.PendingRegistrationCount, 0, 1);
        try
        {
            releaseActive.Set();
            Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref starts) == 1, TimeSpan.FromSeconds(5)));
            Assert.True(SpinWait.SpinUntil(() => demand.PendingRegistrationCount == 0, TimeSpan.FromSeconds(5)));
            Assert.Equal(0, Volatile.Read(ref notStarted));
        }
        finally
        {
            releaseActive.Set();
            scheduler.Dispose();
        }
        await scheduler.Completion.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task DeliveredPromotionMovesQueuedOverscanToVisibleLaneWithoutChangingCapacity()
    {
        using var scheduler = new BoundedMarkdownImageActivationScheduler(capacity: 3, workerCount: 2);
        using var overscanEntered = new ManualResetEventSlim();
        using var releaseOverscan = new ManualResetEventSlim();
        using var promotedStarted = new ManualResetEventSlim();
        var demand = new FakeDemand(isVisible: false);

        Assert.True(scheduler.TrySchedule(OverscanDemand(), () =>
        {
            overscanEntered.Set();
            releaseOverscan.Wait(TimeSpan.FromSeconds(5));
        }, static () => { }, CancellationToken.None));
        Assert.True(overscanEntered.Wait(TimeSpan.FromSeconds(5)));

        Assert.True(scheduler.TrySchedule(demand, () =>
        {
            Assert.True(demand.IsVisible);
            promotedStarted.Set();
        }, static () => { }, CancellationToken.None));
        Assert.Equal(1, scheduler.QueuedCount);
        demand.PromoteAndWaitUntilClaimed();

        try
        {
            Assert.True(promotedStarted.Wait(TimeSpan.FromSeconds(5)),
                "The visible-only worker should receive the promoted queued activation.");
            await demand.CallbackDelivery.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(0, scheduler.QueuedCount);
            Assert.Equal(0, scheduler.VisibleWaitingCount);
        }
        finally
        {
            releaseOverscan.Set();
            scheduler.Dispose();
        }
        await scheduler.Completion.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task PromotionBetweenWorkerDemandReadAndDequeueStartsOnceWithoutRequeue()
    {
        using var scheduler = new BoundedMarkdownImageActivationScheduler(capacity: 1, workerCount: 1);
        using var activeEntered = new ManualResetEventSlim();
        using var releaseActive = new ManualResetEventSlim();
        using var demandReadEntered = new ManualResetEventSlim();
        using var releaseDemandRead = new ManualResetEventSlim();
        using var candidateStarted = new ManualResetEventSlim();
        var demand = new FakeDemand(isVisible: false);
        int starts = 0;
        int notStarted = 0;

        Assert.True(scheduler.TrySchedule(VisibleDemand(), () =>
        {
            activeEntered.Set();
            releaseActive.Wait(TimeSpan.FromSeconds(5));
        }, static () => { }, CancellationToken.None));
        Assert.True(activeEntered.Wait(TimeSpan.FromSeconds(5)));

        Assert.True(scheduler.TrySchedule(demand, () =>
        {
            Assert.True(demand.IsVisible);
            Interlocked.Increment(ref starts);
            candidateStarted.Set();
        }, () => Interlocked.Increment(ref notStarted), CancellationToken.None));
        Assert.Equal(1, scheduler.QueuedCount);
        demand.BlockNextVisibilityRead(demandReadEntered, releaseDemandRead);

        try
        {
            releaseActive.Set();
            Assert.True(demandReadEntered.Wait(TimeSpan.FromSeconds(5)));

            // The worker captured false immediately before this promotion. Its
            // asynchronous callback is now claimed but blocked on the scheduler
            // gate. Releasing the stale read forces the dequeue/start race.
            demand.PromoteAndWaitUntilClaimed();
            releaseDemandRead.Set();

            Assert.True(candidateStarted.Wait(TimeSpan.FromSeconds(5)));
            await demand.CallbackDelivery.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, Volatile.Read(ref starts));
            Assert.Equal(0, Volatile.Read(ref notStarted));
            Assert.Equal(0, scheduler.QueuedCount);
            Assert.Equal(0, scheduler.VisibleWaitingCount);
            Assert.Equal(0, demand.PendingRegistrationCount);
        }
        finally
        {
            releaseDemandRead.Set();
            releaseActive.Set();
            scheduler.Dispose();
        }
        await scheduler.Completion.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task CancellationRetiresPromotedActivationOnceAndLateClaimedCallbackCannotResurrectIt()
    {
        using var scheduler = new BoundedMarkdownImageActivationScheduler(
            capacity: 1, visibleWaiterCapacity: 1, workerCount: 1);
        using var activeEntered = new ManualResetEventSlim();
        using var releaseActive = new ManualResetEventSlim();
        using var cancellation = new CancellationTokenSource();
        var demand = new FakeDemand(isVisible: false);
        int starts = 0;
        int notStarted = 0;

        Assert.True(scheduler.TrySchedule(VisibleDemand(), () =>
        {
            activeEntered.Set();
            releaseActive.Wait(TimeSpan.FromSeconds(5));
        }, static () => { }, CancellationToken.None));
        Assert.True(activeEntered.Wait(TimeSpan.FromSeconds(5)));
        Assert.True(scheduler.TrySchedule(demand, () => Interlocked.Increment(ref starts),
            () => Interlocked.Increment(ref notStarted), cancellation.Token));

        demand.Promote(deliver: false);
        Action staleClaimedCallback = demand.ClaimPromotionCallback();
        cancellation.Cancel();
        Assert.True(SpinWait.SpinUntil(() => scheduler.QueuedCount == 0, TimeSpan.FromSeconds(5)));
        Assert.Equal(1, Volatile.Read(ref notStarted));
        Assert.Equal(0, demand.PendingRegistrationCount);

        staleClaimedCallback();
        Assert.Equal(0, scheduler.QueuedCount);
        Assert.Equal(0, Volatile.Read(ref starts));
        Assert.Equal(1, Volatile.Read(ref notStarted));
        try
        {
            releaseActive.Set();
        }
        finally
        {
            releaseActive.Set();
            scheduler.Dispose();
        }
        await scheduler.Completion.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task PromotedSlotDoesNotConsumeWaiterCapacityAndCountersDrainExactlyOnce()
    {
        using var scheduler = new BoundedMarkdownImageActivationScheduler(
            capacity: 1, visibleWaiterCapacity: 1, workerCount: 1);
        using var activeEntered = new ManualResetEventSlim();
        using var releaseActive = new ManualResetEventSlim();
        var started = new ConcurrentQueue<string>();
        int rejected = 0;

        Assert.True(scheduler.TrySchedule(VisibleDemand(), () =>
        {
            activeEntered.Set();
            releaseActive.Wait(TimeSpan.FromSeconds(5));
            started.Enqueue("active");
        }, static () => { }, CancellationToken.None));
        Assert.True(activeEntered.Wait(TimeSpan.FromSeconds(5)));

        var promoted = new FakeDemand(isVisible: false);
        Assert.True(scheduler.TrySchedule(promoted, () => started.Enqueue("promoted"),
            static () => { }, CancellationToken.None));
        promoted.Promote(deliver: false);
        Assert.True(scheduler.TrySchedule(VisibleDemand(), () => started.Enqueue("waiter"),
            static () => { }, CancellationToken.None));
        Assert.Equal(1, scheduler.QueuedCount);
        Assert.Equal(1, scheduler.VisibleWaitingCount);

        Assert.False(scheduler.TrySchedule(VisibleDemand(), static () => { },
            () => Interlocked.Increment(ref rejected), CancellationToken.None));
        Assert.Equal(1, Volatile.Read(ref rejected));
        Assert.Equal(1, scheduler.QueuedCount);
        Assert.Equal(1, scheduler.VisibleWaitingCount);

        try
        {
            releaseActive.Set();
            Assert.True(SpinWait.SpinUntil(() => started.Count == 3, TimeSpan.FromSeconds(5)));
            Assert.Equal(new[] { "active", "promoted", "waiter" }, started.ToArray());
            Assert.Equal(0, scheduler.QueuedCount);
            Assert.Equal(0, scheduler.VisibleWaitingCount);
        }
        finally
        {
            releaseActive.Set();
            scheduler.Dispose();
        }
        await scheduler.Completion.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task RegistrationFailureLeavesQueueCountsAndCallbacksUntouched()
    {
        using var scheduler = new BoundedMarkdownImageActivationScheduler(capacity: 2, workerCount: 1);
        using var activeEntered = new ManualResetEventSlim();
        using var releaseActive = new ManualResetEventSlim();
        Assert.True(scheduler.TrySchedule(VisibleDemand(), () =>
        {
            activeEntered.Set();
            releaseActive.Wait(TimeSpan.FromSeconds(5));
        }, static () => { }, CancellationToken.None));
        Assert.True(activeEntered.Wait(TimeSpan.FromSeconds(5)));

        object gate = typeof(BoundedMarkdownImageActivationScheduler)
            .GetField("_gate", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(scheduler)!;
        int notStarted = 0;
        var demand = new FakeDemand(isVisible: false) { ThrowOnRegistration = true };
        Assert.False(scheduler.TrySchedule(demand, static () => { }, () =>
        {
            Assert.False(Monitor.IsEntered(gate));
            Interlocked.Increment(ref notStarted);
        }, CancellationToken.None));
        Assert.Equal(1, Volatile.Read(ref notStarted));
        Assert.Equal(0, demand.PendingRegistrationCount);
        Assert.Equal(0, scheduler.VisibleWaitingCount);
        Assert.Equal(0, scheduler.QueuedCount);

        try { releaseActive.Set(); }
        finally
        {
            releaseActive.Set();
            scheduler.Dispose();
        }
        await scheduler.Completion.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task DisposalRetiresQueuedAndWaitingWorkOutsideGateAndRejectsLatePromotion()
    {
        using var scheduler = new BoundedMarkdownImageActivationScheduler(
            capacity: 3, visibleWaiterCapacity: 1, workerCount: 1);
        using var activeEntered = new ManualResetEventSlim();
        using var releaseActive = new ManualResetEventSlim();
        object gate = typeof(BoundedMarkdownImageActivationScheduler)
            .GetField("_gate", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(scheduler)!;
        int started = 0;
        int notStarted = 0;

        Assert.True(scheduler.TrySchedule(VisibleDemand(), () =>
        {
            activeEntered.Set();
            releaseActive.Wait(TimeSpan.FromSeconds(5));
        }, static () => { }, CancellationToken.None));
        Assert.True(activeEntered.Wait(TimeSpan.FromSeconds(5)));

        var promoted = new FakeDemand(isVisible: false);
        Assert.True(scheduler.TrySchedule(promoted, () => Interlocked.Increment(ref started),
            () => AssertRetiredOutsideGate(gate, ref notStarted), CancellationToken.None));
        Assert.True(scheduler.TrySchedule(VisibleDemand(), () => Interlocked.Increment(ref started),
            () => AssertRetiredOutsideGate(gate, ref notStarted), CancellationToken.None));
        Assert.True(scheduler.TrySchedule(VisibleDemand(), () => Interlocked.Increment(ref started),
            () => AssertRetiredOutsideGate(gate, ref notStarted), CancellationToken.None));

        promoted.Promote(deliver: false);
        Action lateClaimedPromotion = promoted.ClaimPromotionCallback();
        Assert.True(scheduler.TrySchedule(VisibleDemand(), () => Interlocked.Increment(ref started),
            () => AssertRetiredOutsideGate(gate, ref notStarted), CancellationToken.None));
        Assert.Equal(3, scheduler.QueuedCount);
        Assert.Equal(1, scheduler.VisibleWaitingCount);

        scheduler.Dispose();
        Assert.Equal(4, Volatile.Read(ref notStarted));
        Assert.Equal(0, scheduler.QueuedCount);
        Assert.Equal(0, scheduler.VisibleWaitingCount);
        Assert.Equal(0, promoted.PendingRegistrationCount);
        Assert.False(scheduler.Completion.IsCompleted);

        lateClaimedPromotion();
        Assert.Equal(4, Volatile.Read(ref notStarted));
        Assert.Equal(0, Volatile.Read(ref started));
        Assert.Equal(0, scheduler.QueuedCount);

        releaseActive.Set();
        await scheduler.Completion.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static void AssertRetiredOutsideGate(object gate, ref int count)
    {
        Assert.False(Monitor.IsEntered(gate));
        Interlocked.Increment(ref count);
    }

    [Fact]
    public async Task IncumbentPromotionDuringRegistrationIsReconciledBeforeAdmission()
    {
        using var scheduler = new BoundedMarkdownImageActivationScheduler(capacity: 2, workerCount: 1);
        using var activeEntered = new ManualResetEventSlim();
        using var releaseActive = new ManualResetEventSlim();
        var started = new ConcurrentQueue<string>();

        Assert.True(scheduler.TrySchedule(VisibleDemand(), () =>
        {
            activeEntered.Set();
            releaseActive.Wait(TimeSpan.FromSeconds(5));
            started.Enqueue("active");
        }, static () => { }, CancellationToken.None));
        Assert.True(activeEntered.Wait(TimeSpan.FromSeconds(5)));

        var incumbent = new FakeDemand(isVisible: false);
        Assert.True(scheduler.TrySchedule(incumbent, () => started.Enqueue("incumbent"),
            static () => { }, CancellationToken.None));
        var incoming = new FakeDemand(isVisible: false)
        {
            DuringNextRegistration = () => incumbent.Promote(deliver: false),
        };

        Assert.True(scheduler.TrySchedule(incoming, () => started.Enqueue("incoming"),
            static () => { }, CancellationToken.None));
        Assert.Equal(2, scheduler.QueuedCount);
        Assert.Equal(0, scheduler.VisibleWaitingCount);

        try
        {
            releaseActive.Set();
            Assert.True(SpinWait.SpinUntil(() => started.Count == 3, TimeSpan.FromSeconds(5)));
            Assert.Equal(new[] { "active", "incumbent", "incoming" }, started.ToArray());
        }
        finally
        {
            releaseActive.Set();
            scheduler.Dispose();
        }
        await scheduler.Completion.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task CancellationDuringRegistrationDoesNotEvictAcceptedWork()
    {
        using var scheduler = new BoundedMarkdownImageActivationScheduler(capacity: 2, workerCount: 1);
        using var activeEntered = new ManualResetEventSlim();
        using var releaseActive = new ManualResetEventSlim();
        using var cancellation = new CancellationTokenSource();
        int incumbentStarted = 0;
        int candidateNotStarted = 0;

        Assert.True(scheduler.TrySchedule(VisibleDemand(), () =>
        {
            activeEntered.Set();
            releaseActive.Wait(TimeSpan.FromSeconds(5));
        }, static () => { }, CancellationToken.None));
        Assert.True(activeEntered.Wait(TimeSpan.FromSeconds(5)));

        var incumbent = new FakeDemand(isVisible: false);
        Assert.True(scheduler.TrySchedule(incumbent, () => Interlocked.Increment(ref incumbentStarted),
            static () => { }, CancellationToken.None));
        var incoming = new FakeDemand(isVisible: false)
        {
            DuringNextRegistration = cancellation.Cancel,
        };

        Assert.False(scheduler.TrySchedule(incoming, static () => { },
            () => Interlocked.Increment(ref candidateNotStarted), cancellation.Token));
        Assert.Equal(1, scheduler.QueuedCount);
        Assert.Equal(0, scheduler.VisibleWaitingCount);
        Assert.Equal(1, Volatile.Read(ref candidateNotStarted));
        Assert.Equal(0, incoming.PendingRegistrationCount);

        try
        {
            releaseActive.Set();
            Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref incumbentStarted) == 1,
                TimeSpan.FromSeconds(5)));
        }
        finally
        {
            releaseActive.Set();
            scheduler.Dispose();
        }
        await scheduler.Completion.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task CompletedActivationClearsImageDelegatesEvenWhenLateCallbackRetainsIt()
    {
        using var scheduler = new BoundedMarkdownImageActivationScheduler(capacity: 1, workerCount: 1);
        using var activeEntered = new ManualResetEventSlim();
        using var releaseActive = new ManualResetEventSlim();
        using var cancellation = new CancellationTokenSource();
        int notStarted = 0;

        Assert.True(scheduler.TrySchedule(VisibleDemand(), () =>
        {
            activeEntered.Set();
            releaseActive.Wait(TimeSpan.FromSeconds(5));
        }, static () => { }, CancellationToken.None));
        Assert.True(activeEntered.Wait(TimeSpan.FromSeconds(5)));

        var demand = new FakeDemand(isVisible: false);
        WeakReference target = ScheduleTargetWithoutRetainingIt(scheduler, demand, cancellation.Token,
            () => Interlocked.Increment(ref notStarted));
        Action staleClaimedCallback = demand.ClaimPromotionCallback();
        cancellation.Cancel();
        Assert.True(SpinWait.SpinUntil(() => scheduler.QueuedCount == 0, TimeSpan.FromSeconds(5)));
        Assert.Equal(1, Volatile.Read(ref notStarted));

        // The claimed callback keeps Activation alive, but completion must have
        // severed both callbacks that could retain image/layout/resolver state.
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Assert.False(target.IsAlive);

        staleClaimedCallback();
        Assert.Equal(0, scheduler.QueuedCount);
        Assert.Equal(1, Volatile.Read(ref notStarted));
        try { releaseActive.Set(); }
        finally
        {
            releaseActive.Set();
            scheduler.Dispose();
        }
        await scheduler.Completion.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ThrowingPromotionRegistrationCleanupDoesNotTerminateWorker()
    {
        using var scheduler = new BoundedMarkdownImageActivationScheduler(capacity: 1, workerCount: 1);
        using var activeEntered = new ManualResetEventSlim();
        using var releaseActive = new ManualResetEventSlim();
        using var queuedStarted = new ManualResetEventSlim();
        var demand = new FakeDemand(isVisible: false) { ThrowOnDispose = true };

        Assert.True(scheduler.TrySchedule(VisibleDemand(), () =>
        {
            activeEntered.Set();
            releaseActive.Wait(TimeSpan.FromSeconds(5));
        }, static () => { }, CancellationToken.None));
        Assert.True(activeEntered.Wait(TimeSpan.FromSeconds(5)));
        Assert.True(scheduler.TrySchedule(demand, queuedStarted.Set, static () => { }, CancellationToken.None));

        try
        {
            releaseActive.Set();
            Assert.True(queuedStarted.Wait(TimeSpan.FromSeconds(5)));
            Assert.Equal(0, scheduler.QueuedCount);
            Assert.Equal(0, demand.PendingRegistrationCount);
        }
        finally
        {
            releaseActive.Set();
            scheduler.Dispose();
        }
        await scheduler.Completion.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference ScheduleTargetWithoutRetainingIt(
        BoundedMarkdownImageActivationScheduler scheduler,
        FakeDemand demand,
        CancellationToken cancellationToken,
        Action onNotStarted)
    {
        var target = new ActivationTarget();
        var weak = new WeakReference(target);
        Assert.True(scheduler.TrySchedule(demand, target.Start, onNotStarted, cancellationToken));
        return weak;
    }

    private sealed class ActivationTarget
    {
        internal void Start() { }
    }

    private static FakeDemand VisibleDemand() => new(isVisible: true);
    private static FakeDemand OverscanDemand() => new(isVisible: false);

    /// <summary>
    /// Asynchronous test implementation of the internal demand contract. It
    /// can hold callbacks, claim one before Register returns on another thread,
    /// or fail registration without depending on ThreadPool timing.
    /// </summary>
    private sealed class FakeDemand(bool isVisible) : IMarkdownImageActivationDemand
    {
        private readonly object _gate = new();
        private readonly List<FakeRegistration> _registrations = [];
        private readonly TaskCompletionSource _callbackDelivered = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private int _visible = isVisible ? 1 : 0;
        private int _blockNextVisibilityRead;
        private ManualResetEventSlim? _visibilityReadEntered;
        private ManualResetEventSlim? _releaseVisibilityRead;

        internal bool PromoteDuringNextRegistration { get; init; }
        internal bool StartCallbackBeforeRegistrationReturns { get; init; }
        internal bool ThrowOnRegistration { get; init; }
        internal bool ThrowOnDispose { get; init; }
        internal Action? DuringNextRegistration { get; init; }
        internal bool CallbackBeganBeforeRegistrationReturned { get; private set; }
        public bool IsVisible
        {
            get
            {
                int observed = Volatile.Read(ref _visible);
                if (Interlocked.Exchange(ref _blockNextVisibilityRead, 0) != 0)
                {
                    _visibilityReadEntered!.Set();
                    if (!_releaseVisibilityRead!.Wait(TimeSpan.FromSeconds(5)))
                        throw new TimeoutException("The controlled visibility read was not released.");
                }
                return observed != 0;
            }
        }
        internal int PendingRegistrationCount { get { lock (_gate) return _registrations.Count; } }
        internal Task CallbackDelivery => _callbackDelivered.Task;

        internal void BlockNextVisibilityRead(
            ManualResetEventSlim entered,
            ManualResetEventSlim release)
        {
            _visibilityReadEntered = entered;
            _releaseVisibilityRead = release;
            Volatile.Write(ref _blockNextVisibilityRead, 1);
        }

        public IDisposable RegisterVisiblePromotion(Action callback)
        {
            ArgumentNullException.ThrowIfNull(callback);
            if (ThrowOnRegistration)
                throw new InvalidOperationException("Injected registration failure.");

            FakeRegistration registration = new(this, callback);
            bool shouldDeliver;
            Action? duringRegistration;
            lock (_gate)
            {
                if (PromoteDuringNextRegistration)
                    Volatile.Write(ref _visible, 1);
                _registrations.Add(registration);
                shouldDeliver = IsVisible;
                duringRegistration = DuringNextRegistration;
            }

            duringRegistration?.Invoke();

            if (shouldDeliver)
            {
                if (StartCallbackBeforeRegistrationReturns)
                {
                    registration.QueueAndWaitUntilClaimed();
                    CallbackBeganBeforeRegistrationReturned = true;
                }
                else
                {
                    registration.Queue();
                }
            }
            return registration;
        }

        internal void Promote(bool deliver)
        {
            FakeRegistration[] registrations;
            lock (_gate)
            {
                Volatile.Write(ref _visible, 1);
                registrations = _registrations.ToArray();
            }
            if (deliver)
            {
                foreach (FakeRegistration registration in registrations)
                    registration.Queue();
            }
        }

        internal void PromoteAndWaitUntilClaimed()
        {
            FakeRegistration[] registrations;
            lock (_gate)
            {
                Volatile.Write(ref _visible, 1);
                registrations = _registrations.ToArray();
            }

            foreach (FakeRegistration registration in registrations)
                registration.QueueAndWaitUntilClaimed();
        }

        internal Action ClaimPromotionCallback()
        {
            lock (_gate)
            {
                FakeRegistration registration = Assert.Single(_registrations);
                return registration.ClaimCallbackForLateDelivery();
            }
        }

        internal void DeliverClaimedPromotion()
        {
            Action callback = ClaimPromotionCallback();
            callback();
        }

        private void Remove(FakeRegistration registration)
        {
            lock (_gate)
                _registrations.Remove(registration);
        }

        private sealed class FakeRegistration(FakeDemand owner, Action callback) : IDisposable
        {
            private FakeDemand? _owner = owner;
            private Action? _callback = callback;
            // Keep the delivery signal independent of registration retirement:
            // a worker can dispose this registration before a queued callback
            // begins, but the test still needs to observe that callback's drain.
            private readonly TaskCompletionSource _deliverySignal = owner._callbackDelivered;
            private int _queued;

            internal void Queue()
            {
                if (Interlocked.Exchange(ref _queued, 1) != 0)
                    return;
                _ = Task.Run(InvokeClaimedCallback);
            }

            internal void QueueAndWaitUntilClaimed()
            {
                using var claimed = new ManualResetEventSlim();
                if (Interlocked.Exchange(ref _queued, 1) == 0)
                    _ = Task.Run(() => InvokeClaimedCallback(claimed));
                if (!claimed.Wait(TimeSpan.FromSeconds(5)))
                    throw new TimeoutException("Promotion callback was not claimed before registration returned.");
            }

            internal Action ClaimCallbackForLateDelivery()
            {
                Action? claimed = Interlocked.Exchange(ref _callback, null);
                return claimed ?? (static () => { });
            }

            private void InvokeClaimedCallback() => InvokeClaimedCallback(null);

            private void InvokeClaimedCallback(ManualResetEventSlim? claimed)
            {
                Action? action = Interlocked.Exchange(ref _callback, null);
                claimed?.Set();
                try { action?.Invoke(); }
                finally
                {
                    FakeDemand? currentOwner = Interlocked.Exchange(ref _owner, null);
                    currentOwner?.Remove(this);
                    _deliverySignal.TrySetResult();
                }
            }

            public void Dispose()
            {
                Interlocked.Exchange(ref _callback, null);
                FakeDemand? owner = Interlocked.Exchange(ref _owner, null);
                owner?.Remove(this);
                if (owner?.ThrowOnDispose == true)
                    throw new InvalidOperationException("Injected registration cleanup failure.");
            }
        }
    }
}
