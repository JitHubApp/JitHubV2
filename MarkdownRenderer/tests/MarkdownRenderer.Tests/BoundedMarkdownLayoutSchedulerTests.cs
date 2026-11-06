using System.Collections.Concurrent;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using MarkdownRenderer.Performance;

namespace MarkdownRenderer.Tests;

public sealed class BoundedMarkdownLayoutSchedulerTests
{
    [Fact]
    public void DefaultWorkerCountUsesProcessorCountAndSupportsConstrainedHosts()
    {
        Assert.Equal(1, BoundedMarkdownLayoutScheduler.ResolveWorkerCount(1, 8));
        Assert.Equal(2, BoundedMarkdownLayoutScheduler.ResolveWorkerCount(2, 8));
        Assert.Equal(2, BoundedMarkdownLayoutScheduler.ResolveWorkerCount(16, 8));
        Assert.Equal(1, BoundedMarkdownLayoutScheduler.ResolveWorkerCount(16, 4));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            BoundedMarkdownLayoutScheduler.ResolveWorkerCount(0, 8));
    }

    [Fact]
    public async Task ImmediateAdmissionReturnsTheWorkItemTaskWithoutAnAsyncWrapper()
    {
        var scheduler = new BoundedMarkdownLayoutScheduler(
            primaryCapacity: 1,
            backgroundCapacity: 1,
            workerCount: 1);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        Task<int> scheduled = scheduler.ScheduleAsync(
            MarkdownLayoutWorkPriority.Primary,
            _ =>
            {
                entered.Set();
                release.Wait();
                return 42;
            },
            CancellationToken.None);

        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            Assert.Equal(typeof(Task<int>), scheduled.GetType());
            release.Set();
            Assert.Equal(42, await scheduled.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally
        {
            release.Set();
            scheduler.Dispose();
        }

        await scheduler.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        scheduler.ReleaseResources();
    }

    [Fact]
    public async Task PrimaryWorkPreemptsQueuedBackgroundBacklog()
    {
        using var scheduler = new BoundedMarkdownLayoutScheduler(
            primaryCapacity: 2,
            backgroundCapacity: 4,
            workerCount: 1);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var order = new ConcurrentQueue<string>();

        Task<int> active = scheduler.ScheduleAsync(
            MarkdownLayoutWorkPriority.Primary,
            _ =>
            {
                entered.Set();
                release.Wait();
                order.Enqueue("active");
                return 1;
            },
            CancellationToken.None);
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));

        Task<int>[] background = Enumerable.Range(0, 4)
            .Select(index => scheduler.ScheduleAsync(
                MarkdownLayoutWorkPriority.Background,
                _ =>
                {
                    order.Enqueue($"background-{index}");
                    return index;
                },
                CancellationToken.None))
            .ToArray();
        Task<int> primary = scheduler.ScheduleAsync(
            MarkdownLayoutWorkPriority.Primary,
            _ =>
            {
                order.Enqueue("primary");
                return 5;
            },
            CancellationToken.None);

        try
        {
            release.Set();
            Assert.Equal(1, await active.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(5, await primary.WaitAsync(TimeSpan.FromSeconds(5)));
            await Task.WhenAll(background).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("primary", order.ToArray()[1]);
        }
        finally
        {
            release.Set();
            scheduler.Dispose();
        }
        await scheduler.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        scheduler.ReleaseResources();
    }

    [Fact]
    public async Task ReservedPrimaryWorkerRunsWhileBackgroundWorkerIsBusy()
    {
        using var scheduler = new BoundedMarkdownLayoutScheduler(
            primaryCapacity: 2,
            backgroundCapacity: 2,
            workerCount: 2);
        using var backgroundEntered = new ManualResetEventSlim();
        using var releaseBackground = new ManualResetEventSlim();
        using var primaryEntered = new ManualResetEventSlim();

        Task<int> background = scheduler.ScheduleAsync(
            MarkdownLayoutWorkPriority.Background,
            _ =>
            {
                backgroundEntered.Set();
                releaseBackground.Wait();
                return 1;
            },
            CancellationToken.None);
        Assert.True(backgroundEntered.Wait(TimeSpan.FromSeconds(5)));

        Task<int> primary = scheduler.ScheduleAsync(
            MarkdownLayoutWorkPriority.Primary,
            _ =>
            {
                primaryEntered.Set();
                return 2;
            },
            CancellationToken.None);

        try
        {
            Assert.True(primaryEntered.Wait(TimeSpan.FromSeconds(5)));
            Assert.Equal(2, await primary.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally
        {
            releaseBackground.Set();
            await background.WaitAsync(TimeSpan.FromSeconds(5));
            scheduler.Dispose();
        }
        await scheduler.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        scheduler.ReleaseResources();
    }

    [Fact]
    public async Task BackgroundWorkReceivesServiceUnderContinuousPrimaryLoad()
    {
        using var scheduler = new BoundedMarkdownLayoutScheduler(
            primaryCapacity: 8,
            backgroundCapacity: 2,
            workerCount: 1);
        using var activeEntered = new ManualResetEventSlim();
        using var releaseActive = new ManualResetEventSlim();
        var order = new ConcurrentQueue<string>();

        Task<int> active = scheduler.ScheduleAsync(
            MarkdownLayoutWorkPriority.Primary,
            _ =>
            {
                activeEntered.Set();
                releaseActive.Wait();
                order.Enqueue("active");
                return 0;
            },
            CancellationToken.None);
        Assert.True(activeEntered.Wait(TimeSpan.FromSeconds(5)));

        Task<int> background = scheduler.ScheduleAsync(
            MarkdownLayoutWorkPriority.Background,
            _ =>
            {
                order.Enqueue("background");
                return 1;
            },
            CancellationToken.None);
        Task<int>[] primary = Enumerable.Range(0, 5)
            .Select(index => scheduler.ScheduleAsync(
                MarkdownLayoutWorkPriority.Primary,
                _ =>
                {
                    order.Enqueue($"primary-{index}");
                    return index;
                },
                CancellationToken.None))
            .ToArray();

        try
        {
            releaseActive.Set();
            await active.WaitAsync(TimeSpan.FromSeconds(5));
            await background.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.WhenAll(primary).WaitAsync(TimeSpan.FromSeconds(5));

            string[] completedOrder = order.ToArray();
            int backgroundIndex = Array.IndexOf(completedOrder, "background");
            int fourthQueuedPrimaryIndex = Array.IndexOf(completedOrder, "primary-3");
            Assert.True(backgroundIndex >= 0, string.Join(", ", completedOrder));
            Assert.True(fourthQueuedPrimaryIndex >= 0, string.Join(", ", completedOrder));
            Assert.True(
                backgroundIndex < fourthQueuedPrimaryIndex,
                string.Join(", ", completedOrder));
        }
        finally
        {
            releaseActive.Set();
            scheduler.Dispose();
        }
        await scheduler.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        scheduler.ReleaseResources();
    }

    [Fact]
    public async Task CancelingQueuedWorkRemovesItAndReleasesItsBoundedSlot()
    {
        using var scheduler = new BoundedMarkdownLayoutScheduler(
            primaryCapacity: 1,
            backgroundCapacity: 1,
            workerCount: 1);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var canceled = new CancellationTokenSource();
        int canceledWorkRuns = 0;

        Task<int> active = scheduler.ScheduleAsync(
            MarkdownLayoutWorkPriority.Primary,
            _ =>
            {
                entered.Set();
                release.Wait();
                return 1;
            },
            CancellationToken.None);
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));

        Task<int> canceledWork = scheduler.ScheduleAsync(
            MarkdownLayoutWorkPriority.Background,
            _ =>
            {
                Interlocked.Increment(ref canceledWorkRuns);
                return 2;
            },
            canceled.Token);
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await canceledWork.WaitAsync(TimeSpan.FromSeconds(5)));

        Task<int> replacement = scheduler.ScheduleAsync(
            MarkdownLayoutWorkPriority.Background,
            _ => 3,
            CancellationToken.None);
        try
        {
            release.Set();
            Assert.Equal(1, await active.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(3, await replacement.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(0, Volatile.Read(ref canceledWorkRuns));
        }
        finally
        {
            release.Set();
            scheduler.Dispose();
        }
        await scheduler.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        scheduler.ReleaseResources();
    }

    [Fact]
    public async Task AdmissionWaitersAreCountedAndRetiredWhenCallerCancels()
    {
        using var scheduler = new BoundedMarkdownLayoutScheduler(
            primaryCapacity: 1,
            backgroundCapacity: 1,
            workerCount: 1);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var waitingCancellationOne = new CancellationTokenSource();
        using var waitingCancellationTwo = new CancellationTokenSource();
        using var waitingCancellationThree = new CancellationTokenSource();

        Task<int> active = scheduler.ScheduleAsync(
            MarkdownLayoutWorkPriority.Primary,
            _ =>
            {
                entered.Set();
                release.Wait();
                return 1;
            },
            CancellationToken.None);
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));

        Task<int>[] waiting =
        [
            scheduler.ScheduleAsync(MarkdownLayoutWorkPriority.Primary, _ => 2, waitingCancellationOne.Token),
            scheduler.ScheduleAsync(MarkdownLayoutWorkPriority.Primary, _ => 3, waitingCancellationTwo.Token),
            scheduler.ScheduleAsync(MarkdownLayoutWorkPriority.Primary, _ => 4, waitingCancellationThree.Token),
        ];
        Assert.True(SpinWait.SpinUntil(
            () => scheduler.PrimaryAdmissionWaiters == waiting.Length,
            TimeSpan.FromSeconds(5)));
        Assert.Equal(0, scheduler.BackgroundAdmissionWaiters);

        waitingCancellationOne.Cancel();
        waitingCancellationTwo.Cancel();
        waitingCancellationThree.Cancel();
        foreach (Task<int> waiter in waiting)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await waiter.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        Assert.True(SpinWait.SpinUntil(
            () => scheduler.PrimaryAdmissionWaiters == 0,
            TimeSpan.FromSeconds(5)));

        try
        {
            release.Set();
            Assert.Equal(1, await active.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally
        {
            release.Set();
            scheduler.Dispose();
        }
        await scheduler.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        scheduler.ReleaseResources();
    }

    [Fact]
    public async Task DisposalCancelsQueuedAndWaitingWorkButLetsActiveWorkFinish()
    {
        var scheduler = new BoundedMarkdownLayoutScheduler(
            primaryCapacity: 1,
            backgroundCapacity: 1,
            workerCount: 1);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        int queuedWorkRuns = 0;

        Task<int> active = scheduler.ScheduleAsync(
            MarkdownLayoutWorkPriority.Primary,
            _ =>
            {
                entered.Set();
                release.Wait();
                return 1;
            },
            CancellationToken.None);
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));

        Task<int> queued = scheduler.ScheduleAsync(
            MarkdownLayoutWorkPriority.Background,
            _ =>
            {
                Interlocked.Increment(ref queuedWorkRuns);
                return 2;
            },
            CancellationToken.None);
        Task<int> waitingForCapacity = scheduler.ScheduleAsync(
            MarkdownLayoutWorkPriority.Primary,
            _ => 3,
            CancellationToken.None);
        Assert.True(SpinWait.SpinUntil(
            () => scheduler.PrimaryAdmissionWaiters == 1,
            TimeSpan.FromSeconds(5)));

        scheduler.Dispose();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await queued.WaitAsync(TimeSpan.FromSeconds(5)));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await waitingForCapacity.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(SpinWait.SpinUntil(
            () => scheduler.PrimaryAdmissionWaiters == 0,
            TimeSpan.FromSeconds(5)));

        try
        {
            Assert.False(active.IsCompleted);
            release.Set();
            Assert.Equal(1, await active.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(0, Volatile.Read(ref queuedWorkRuns));
        }
        finally
        {
            release.Set();
        }
        await scheduler.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        scheduler.ReleaseResources();
    }

    [Fact]
    public async Task RunningWorkObservesItsLinkedCancellationToken()
    {
        using var scheduler = new BoundedMarkdownLayoutScheduler(
            primaryCapacity: 1,
            backgroundCapacity: 1,
            workerCount: 1);
        using var cancellation = new CancellationTokenSource();
        using var entered = new ManualResetEventSlim();

        Task<int> active = scheduler.ScheduleAsync(
            MarkdownLayoutWorkPriority.Primary,
            token =>
            {
                entered.Set();
                token.WaitHandle.WaitOne();
                token.ThrowIfCancellationRequested();
                return 1;
            },
            cancellation.Token);
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await active.WaitAsync(TimeSpan.FromSeconds(5)));

        scheduler.Dispose();
        await scheduler.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        scheduler.ReleaseResources();
    }

}
