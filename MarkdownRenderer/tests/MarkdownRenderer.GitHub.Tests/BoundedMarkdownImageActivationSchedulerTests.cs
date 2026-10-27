using System.Collections.Concurrent;
using MarkdownRenderer.Images;
using MarkdownRenderer.Performance;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

public sealed class BoundedMarkdownImageActivationSchedulerTests
{
    [Fact]
    public void WorkerCountKeepsConstrainedHostsAtOneAndLazilyStartsTheLane()
    {
        Assert.Equal(1, BoundedMarkdownImageActivationScheduler.ResolveWorkerCount(1, 8));
        Assert.Equal(2, BoundedMarkdownImageActivationScheduler.ResolveWorkerCount(8, 8));
        Assert.Equal(1, BoundedMarkdownImageActivationScheduler.ResolveWorkerCount(8, 4));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            BoundedMarkdownImageActivationScheduler.ResolveWorkerCount(0, 8));

        using var scheduler = new BoundedMarkdownImageActivationScheduler(capacity: 4, workerCount: 1);
        Assert.Equal(0, scheduler.WorkerCount);
        Assert.True(scheduler.TrySchedule(
            demand: VisibleDemand,
            startAsyncWork: static () => { },
            onNotStarted: static () => { },
            CancellationToken.None));
        Assert.True(SpinWait.SpinUntil(() => scheduler.WorkerCount == 1, TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task PartialWorkerStartupLeavesGeneralWorkerAbleToDrainOverscan()
    {
        int startupAttempts = 0;
        using var scheduler = new BoundedMarkdownImageActivationScheduler(
            capacity: 4,
            workerCount: 2,
            startWorker: worker =>
            {
                if (Interlocked.Increment(ref startupAttempts) == 2)
                    throw new ThreadStateException("Injected optional visible-worker startup failure.");
                worker.Start();
            });
        TaskCompletionSource visibleStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource overscanStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);

        Assert.True(scheduler.TrySchedule(VisibleDemand, () => visibleStarted.TrySetResult(),
            static () => { }, CancellationToken.None));
        Assert.Equal(1, scheduler.WorkerCount);
        await visibleStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(scheduler.TrySchedule(OverscanDemand, () => overscanStarted.TrySetResult(),
            static () => { }, CancellationToken.None));
        await overscanStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        scheduler.Dispose();
        await scheduler.Completion.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ZeroWorkerStartupFailureRetiresActivationAndAllowsStartupRetry()
    {
        int startupAttempts = 0;
        int retirementCount = 0;
        using var scheduler = new BoundedMarkdownImageActivationScheduler(
            capacity: 2,
            workerCount: 1,
            startWorker: worker =>
            {
                if (Interlocked.Increment(ref startupAttempts) == 1)
                    throw new ThreadStateException("Injected transient worker startup failure.");
                worker.Start();
            });

        Assert.False(scheduler.TrySchedule(VisibleDemand, static () => { },
            () => Interlocked.Increment(ref retirementCount), CancellationToken.None));
        Assert.Equal(0, scheduler.WorkerCount);
        Assert.Equal(1, Volatile.Read(ref retirementCount));

        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.True(scheduler.TrySchedule(OverscanDemand, () => started.TrySetResult(),
            static () => { }, CancellationToken.None));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, Volatile.Read(ref startupAttempts));

        scheduler.Dispose();
        await scheduler.Completion.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task SingleWorkerLaneStartsOverscanWithoutCreatingPerImageThreads()
    {
        using var scheduler = new BoundedMarkdownImageActivationScheduler(capacity: 2, workerCount: 1);
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);

        Assert.True(scheduler.TrySchedule(OverscanDemand, () => started.TrySetResult(),
            static () => { }, CancellationToken.None));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, scheduler.WorkerCount);
        scheduler.Dispose();
        await scheduler.Completion.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ReservedVisibleWorkerStartsWhileTheGeneralLaneIsInsideOverscanPrefix()
    {
        using var scheduler = new BoundedMarkdownImageActivationScheduler(capacity: 4, workerCount: 2);
        using var overscanEntered = new ManualResetEventSlim();
        using var releaseOverscan = new ManualResetEventSlim();
        TaskCompletionSource visibleStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);

        Assert.True(scheduler.TrySchedule(OverscanDemand, () =>
        {
            overscanEntered.Set();
            releaseOverscan.Wait(TimeSpan.FromSeconds(5));
        }, static () => { }, CancellationToken.None));
        Assert.True(overscanEntered.Wait(TimeSpan.FromSeconds(5)));

        try
        {
            Assert.True(scheduler.TrySchedule(VisibleDemand, () => visibleStarted.TrySetResult(),
                static () => { }, CancellationToken.None));
            await visibleStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            releaseOverscan.Set();
            scheduler.Dispose();
        }

        await scheduler.Completion.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task VisibleActivationEvictsQueuedOverscanAndOverflowDoesNotGrowTheQueue()
    {
        using var scheduler = new BoundedMarkdownImageActivationScheduler(capacity: 2, workerCount: 1);
        using var activeEntered = new ManualResetEventSlim();
        using var releaseActive = new ManualResetEventSlim();
        var started = new ConcurrentQueue<string>();
        int evicted = 0;
        int rejected = 0;

        Assert.True(scheduler.TrySchedule(OverscanDemand, () =>
        {
            activeEntered.Set();
            releaseActive.Wait(TimeSpan.FromSeconds(5));
            started.Enqueue("active");
        }, static () => { }, CancellationToken.None));
        Assert.True(activeEntered.Wait(TimeSpan.FromSeconds(5)));

        Assert.True(scheduler.TrySchedule(OverscanDemand, () => started.Enqueue("overscan-old"),
            () => Interlocked.Increment(ref evicted), CancellationToken.None));
        Assert.True(scheduler.TrySchedule(OverscanDemand, () => started.Enqueue("overscan-kept"),
            static () => { }, CancellationToken.None));

        Assert.True(scheduler.TrySchedule(VisibleDemand, () => started.Enqueue("visible"),
            static () => { }, CancellationToken.None));
        Assert.Equal(2, scheduler.QueuedCount);
        Assert.Equal(1, Volatile.Read(ref evicted));
        Assert.False(scheduler.TrySchedule(OverscanDemand, () => started.Enqueue("overflow"),
            () => Interlocked.Increment(ref rejected), CancellationToken.None));
        Assert.Equal(1, Volatile.Read(ref rejected));
        Assert.Equal(2, scheduler.QueuedCount);

        try
        {
            releaseActive.Set();
            Assert.True(SpinWait.SpinUntil(() => started.Count == 3, TimeSpan.FromSeconds(5)));
            Assert.Equal(new[] { "active", "visible", "overscan-kept" }, started.ToArray());
        }
        finally
        {
            releaseActive.Set();
            scheduler.Dispose();
        }

        await scheduler.Completion.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task EvictedOverscanActivationResetsAndCanBeRetriedAsVisible()
    {
        using var scheduler = new BoundedMarkdownImageActivationScheduler(capacity: 1, workerCount: 1);
        using var activeEntered = new ManualResetEventSlim();
        using var releaseActive = new ManualResetEventSlim();
        using var visibleStarted = new ManualResetEventSlim();
        using var retryStarted = new ManualResetEventSlim();
        bool evictedBoxLoadStarted = true;

        Assert.True(scheduler.TrySchedule(OverscanDemand, () =>
        {
            activeEntered.Set();
            releaseActive.Wait(TimeSpan.FromSeconds(5));
        }, static () => { }, CancellationToken.None));
        Assert.True(activeEntered.Wait(TimeSpan.FromSeconds(5)));

        Assert.True(scheduler.TrySchedule(OverscanDemand, static () => { },
            () => Volatile.Write(ref evictedBoxLoadStarted, false), CancellationToken.None));
        Assert.True(scheduler.TrySchedule(VisibleDemand, () => visibleStarted.Set(),
            static () => { }, CancellationToken.None));
        Assert.False(Volatile.Read(ref evictedBoxLoadStarted));

        try
        {
            releaseActive.Set();
            Assert.True(visibleStarted.Wait(TimeSpan.FromSeconds(5)));
            Assert.True(scheduler.TrySchedule(VisibleDemand, () =>
            {
                Volatile.Write(ref evictedBoxLoadStarted, true);
                retryStarted.Set();
            },
            () => Volatile.Write(ref evictedBoxLoadStarted, false), CancellationToken.None));
            Assert.True(retryStarted.Wait(TimeSpan.FromSeconds(5)));
            Assert.True(Volatile.Read(ref evictedBoxLoadStarted));
        }
        finally
        {
            releaseActive.Set();
            scheduler.Dispose();
        }

        await scheduler.Completion.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task CanceledQueuedActivationIsRemovedAndRetiredOnlyOnce()
    {
        using var scheduler = new BoundedMarkdownImageActivationScheduler(
            capacity: 1,
            visibleWaiterCapacity: 1,
            workerCount: 1);
        using var activeEntered = new ManualResetEventSlim();
        using var releaseActive = new ManualResetEventSlim();
        using var cancellation = new CancellationTokenSource();
        int canceledStartCount = 0;
        int canceledRetirementCount = 0;

        Assert.True(scheduler.TrySchedule(VisibleDemand, () =>
        {
            activeEntered.Set();
            releaseActive.Wait(TimeSpan.FromSeconds(5));
        }, static () => { }, CancellationToken.None));
        Assert.True(activeEntered.Wait(TimeSpan.FromSeconds(5)));

        Assert.True(scheduler.TrySchedule(VisibleDemand,
            () => Interlocked.Increment(ref canceledStartCount),
            () => Interlocked.Increment(ref canceledRetirementCount),
            cancellation.Token));
        Assert.Equal(1, scheduler.QueuedCount);
        cancellation.Cancel();
        Assert.True(SpinWait.SpinUntil(() => scheduler.QueuedCount == 0, TimeSpan.FromSeconds(5)));
        Assert.Equal(0, Volatile.Read(ref canceledStartCount));
        Assert.Equal(1, Volatile.Read(ref canceledRetirementCount));

        TaskCompletionSource replacementStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.True(scheduler.TrySchedule(VisibleDemand, () => replacementStarted.TrySetResult(),
            static () => { }, CancellationToken.None));
        try
        {
            releaseActive.Set();
            await replacementStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            releaseActive.Set();
            scheduler.Dispose();
        }

        await scheduler.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, Volatile.Read(ref canceledRetirementCount));
    }

    [Fact]
    public async Task VisibleOverflowWaiterIsBoundedAndPromotedWithoutAnotherViewportEvent()
    {
        using var scheduler = new BoundedMarkdownImageActivationScheduler(
            capacity: 1,
            visibleWaiterCapacity: 1,
            workerCount: 1);
        using var activeEntered = new ManualResetEventSlim();
        using var releaseActive = new ManualResetEventSlim();
        var started = new ConcurrentQueue<string>();
        int rejected = 0;

        Assert.True(scheduler.TrySchedule(VisibleDemand, () =>
        {
            activeEntered.Set();
            releaseActive.Wait(TimeSpan.FromSeconds(5));
            started.Enqueue("active");
        }, static () => { }, CancellationToken.None));
        Assert.True(activeEntered.Wait(TimeSpan.FromSeconds(5)));

        Assert.True(scheduler.TrySchedule(VisibleDemand, () => started.Enqueue("queued"),
            static () => { }, CancellationToken.None));
        Assert.True(scheduler.TrySchedule(VisibleDemand, () => started.Enqueue("waiter"),
            static () => { }, CancellationToken.None));
        Assert.Equal(1, scheduler.QueuedCount);
        Assert.Equal(1, scheduler.VisibleWaitingCount);

        // The lane has a fixed waiting bound. Rejection only invokes the
        // caller's retry path; no waiter thread or unbounded task is created.
        Assert.False(scheduler.TrySchedule(VisibleDemand, () => started.Enqueue("rejected"),
            () => Interlocked.Increment(ref rejected), CancellationToken.None));
        Assert.Equal(1, Volatile.Read(ref rejected));
        Assert.Equal(1, scheduler.VisibleWaitingCount);

        try
        {
            releaseActive.Set();
            Assert.True(SpinWait.SpinUntil(() => started.Count == 3, TimeSpan.FromSeconds(5)));
            Assert.Equal(new[] { "active", "queued", "waiter" }, started.ToArray());
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
    public async Task TopFiveHundredVisibleDemandFitsTheBoundedActivationEnvelope()
    {
        using var scheduler = new BoundedMarkdownImageActivationScheduler(
            capacity: 32,
            visibleWaiterCapacity: 512,
            workerCount: 1);
        using var activeEntered = new ManualResetEventSlim();
        using var releaseActive = new ManualResetEventSlim();
        using var completed = new CountdownEvent(500);
        int startedCount = 0;
        int notStartedCount = 0;

        Assert.True(scheduler.TrySchedule(VisibleDemand, () =>
        {
            activeEntered.Set();
            releaseActive.Wait(TimeSpan.FromSeconds(5));
        }, static () => { }, CancellationToken.None));
        Assert.True(activeEntered.Wait(TimeSpan.FromSeconds(5)));

        for (int index = 0; index < 500; index++)
        {
            Assert.True(scheduler.TrySchedule(VisibleDemand, () =>
            {
                Interlocked.Increment(ref startedCount);
                completed.Signal();
            },
                () => Interlocked.Increment(ref notStartedCount),
                CancellationToken.None), $"Visible activation {index} was rejected.");
        }

        Assert.Equal(32, scheduler.QueuedCount);
        Assert.Equal(468, scheduler.VisibleWaitingCount);
        try
        {
            releaseActive.Set();
            Assert.True(completed.Wait(TimeSpan.FromSeconds(5)));
            Assert.Equal(0, scheduler.VisibleWaitingCount);
            Assert.Equal(500, Volatile.Read(ref startedCount));
            Assert.Equal(0, Volatile.Read(ref notStartedCount));
        }
        finally
        {
            releaseActive.Set();
            scheduler.Dispose();
        }

        await scheduler.Completion.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task VisibleWorkIsFairToOverscanDuringAContinuousVisibleQueue()
    {
        using var scheduler = new BoundedMarkdownImageActivationScheduler(capacity: 16, workerCount: 1);
        using var activeEntered = new ManualResetEventSlim();
        using var releaseActive = new ManualResetEventSlim();
        var order = new ConcurrentQueue<string>();

        Assert.True(scheduler.TrySchedule(VisibleDemand, () =>
        {
            activeEntered.Set();
            releaseActive.Wait(TimeSpan.FromSeconds(5));
            order.Enqueue("active");
        }, static () => { }, CancellationToken.None));
        Assert.True(activeEntered.Wait(TimeSpan.FromSeconds(5)));

        Assert.True(scheduler.TrySchedule(OverscanDemand, () => order.Enqueue("overscan"),
            static () => { }, CancellationToken.None));
        for (int index = 0; index < 9; index++)
        {
            int visible = index;
            Assert.True(scheduler.TrySchedule(VisibleDemand, () => order.Enqueue($"visible-{visible}"),
                static () => { }, CancellationToken.None));
        }

        try
        {
            releaseActive.Set();
            Assert.True(SpinWait.SpinUntil(() => order.Count == 11, TimeSpan.FromSeconds(5)));
            string[] observed = order.ToArray();
            Assert.Equal("active", observed[0]);
            Assert.Equal("overscan", observed[8]);
        }
        finally
        {
            releaseActive.Set();
            scheduler.Dispose();
        }

        await scheduler.Completion.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task DisposalRetiresQueuedWorkAndWaitsForAnActiveStarter()
    {
        var scheduler = new BoundedMarkdownImageActivationScheduler(capacity: 2, workerCount: 1);
        using var activeEntered = new ManualResetEventSlim();
        using var releaseActive = new ManualResetEventSlim();
        int queuedRetirements = 0;

        Assert.True(scheduler.TrySchedule(VisibleDemand, () =>
        {
            activeEntered.Set();
            releaseActive.Wait(TimeSpan.FromSeconds(5));
        }, static () => { }, CancellationToken.None));
        Assert.True(activeEntered.Wait(TimeSpan.FromSeconds(5)));
        Assert.True(scheduler.TrySchedule(OverscanDemand, static () => { },
            () => Interlocked.Increment(ref queuedRetirements), CancellationToken.None));

        scheduler.Dispose();
        Assert.Equal(1, Volatile.Read(ref queuedRetirements));
        Assert.False(scheduler.Completion.IsCompleted);
        try
        {
            releaseActive.Set();
            await scheduler.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            releaseActive.Set();
        }
    }

    [Fact]
    public async Task StarterRunsOnOneOfTheLaneThreads()
    {
        using var scheduler = new BoundedMarkdownImageActivationScheduler(capacity: 2, workerCount: 1);
        int callerThread = Environment.CurrentManagedThreadId;
        int executionThread = 0;
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.True(scheduler.TrySchedule(VisibleDemand, () =>
        {
            executionThread = Environment.CurrentManagedThreadId;
            started.TrySetResult();
        }, static () => { }, CancellationToken.None));

        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotEqual(callerThread, Volatile.Read(ref executionThread));
        scheduler.Dispose();
        await scheduler.Completion.WaitAsync(TimeSpan.FromSeconds(5));
    }

    // Compatibility fixture for the pre-promotion scheduler behavior. These
    // tests exercise fixed visible/overscan classifications only; promotion
    // semantics are covered separately by the demand-ticket tests.
    private static readonly IMarkdownImageActivationDemand VisibleDemand = new FixedDemand(isVisible: true);
    private static readonly IMarkdownImageActivationDemand OverscanDemand = new FixedDemand(isVisible: false);

    private sealed class FixedDemand(bool isVisible) : IMarkdownImageActivationDemand
    {
        public bool IsVisible { get; } = isVisible;

        public IDisposable RegisterVisiblePromotion(Action callback)
        {
            ArgumentNullException.ThrowIfNull(callback);
            // This immutable fixture never promotes after scheduling.
            return NoopRegistration.Instance;
        }
    }

    private sealed class NoopRegistration : IDisposable
    {
        internal static readonly NoopRegistration Instance = new();

        public void Dispose() { }
    }
}
