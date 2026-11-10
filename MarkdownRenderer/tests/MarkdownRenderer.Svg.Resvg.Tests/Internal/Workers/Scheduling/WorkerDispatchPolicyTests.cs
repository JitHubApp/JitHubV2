using System.Runtime.InteropServices;
using MarkdownRenderer.Images;
using MarkdownRenderer.Svg.Resvg.Internal.Workers.Scheduling;
using Xunit;

namespace MarkdownRenderer.Svg.Resvg.Tests.Internal.Workers.Scheduling;

/// <summary>
/// Pins the scheduler's dispatch policy: secondary-worker eligibility and
/// power gating, its start thresholds, visible-tier counters and promotion,
/// and secondary warm-up with its failure latch.
/// </summary>
public sealed class WorkerDispatchPolicyTests
{
    [Theory]
    [InlineData(Architecture.X64, 8, false, false, true)]
    [InlineData(Architecture.Arm64, 16, false, false, true)]
    [InlineData(Architecture.X64, 8, true, false, false)]
    [InlineData(Architecture.X64, 8, false, true, false)]
    [InlineData(Architecture.X64, 7, false, false, false)]
    [InlineData(Architecture.Arm64, 7, false, false, false)]
    [InlineData(Architecture.Arm64, 8, true, false, false)]
    [InlineData(Architecture.X86, 32, false, false, false)]
    [InlineData(Architecture.Arm, 32, false, false, false)]
    public void SecondaryEligibility_IsArchitectureCpuAndEnergyBounded(
        Architecture architecture,
        int processors,
        bool energySaverOn,
        bool energySaverQueryFailed,
        bool expected) =>
        Assert.Equal(expected, WorkerSchedulingPolicy.CanUseSecondary(
            architecture,
            processors,
            energySaverOn,
            energySaverQueryFailed));

    [Theory]
    [InlineData(Architecture.X64, 8, false, false, true)]
    [InlineData(Architecture.X64, 8, true, false, false)]
    [InlineData(Architecture.X64, 8, false, true, false)]
    [InlineData(Architecture.X64, 7, false, false, false)]
    [InlineData(Architecture.X86, 32, false, false, false)]
    public void SecondaryPowerPolicyGatesThePairedQueueHandoff(
        Architecture architecture,
        int processors,
        bool energySaverOn,
        bool energySaverQueryFailed,
        bool expected)
    {
        bool machineEligible = WorkerSchedulingPolicy.CanUseSecondary(
            architecture,
            processors,
            energySaverOn,
            energySaverQueryFailed);
        bool canDispatch = WorkerSchedulingPolicy.CanDispatchSecondary(
            machineEligible,
            workerReady: true,
            workerBusy: false);
        Assert.Equal(expected, canDispatch);

        var queue = new WorkerSchedulingQueue<object>();
        object secondaryPreferred = new();
        object primaryNext = new();
        queue.Enqueue(
            secondaryPreferred,
            MarkdownSvgRenderPriority.Visible,
            isRender: true,
            preferredWorkerInstanceId: 42);
        queue.Enqueue(
            primaryNext,
            MarkdownSvgRenderPriority.Visible,
            isRender: true,
            preferredWorkerInstanceId: 17);

        Assert.Equal(
            expected,
            queue.TryDequeueOldestVisibleForSecondaryAffinity(
                secondaryWorkerInstanceId: 42,
                fontCatalogReady: true,
                canDispatchSecondary: canDispatch,
                out object? handedOff));
        if (expected)
        {
            Assert.Same(secondaryPreferred, handedOff);
            Assert.True(queue.TryDequeuePrimary(17, out object? primary));
            Assert.Same(primaryNext, primary);
        }
        else
        {
            Assert.Null(handedOff);
            Assert.Equal(2, queue.Count);
            Assert.True(queue.TryDequeuePrimary(0, out object? primary));
            Assert.Same(secondaryPreferred, primary);
        }
    }

    [Theory]
    [InlineData(false, false, false, false)]
    [InlineData(false, false, true, false)]
    [InlineData(false, true, false, false)]
    [InlineData(false, true, true, false)]
    [InlineData(true, false, false, false)]
    [InlineData(true, false, true, false)]
    [InlineData(true, true, false, true)]
    [InlineData(true, true, true, false)]
    public void SecondaryDispatchRequiresMachineEligibilityAndIdleReadyWorker(
        bool machineEligible,
        bool workerReady,
        bool workerBusy,
        bool expected) =>
        Assert.Equal(expected, WorkerSchedulingPolicy.CanDispatchSecondary(
            machineEligible,
            workerReady,
            workerBusy));

    [Theory]
    [InlineData(true, true, false, 2, true)]
    [InlineData(true, true, false, 1, false)]
    [InlineData(true, false, false, 2, false)]
    [InlineData(true, true, true, 2, false)]
    [InlineData(false, true, false, 2, false)]
    public void SecondaryStartsOnlyForTwoQueuedVisibleUncachedRenders(
        bool eligible,
        bool primaryBusy,
        bool secondaryExists,
        int visibleRenders,
        bool expected) =>
        Assert.Equal(expected, WorkerSchedulingPolicy.ShouldStartSecondary(
            eligible,
            primaryBusy,
            secondaryExists,
            visibleRenders));

    [Fact]
    public void SecondaryStartThresholdIgnoresVisibleOpenBacklogUntilTwoRendersAreQueued()
    {
        var queue = new WorkerSchedulingQueue<object>();
        queue.Enqueue(new object(), MarkdownSvgRenderPriority.Visible, isRender: false, isOpen: true);
        queue.Enqueue(new object(), MarkdownSvgRenderPriority.Visible, isRender: false, isOpen: true);
        queue.Enqueue(new object(), MarkdownSvgRenderPriority.Visible, isRender: true);

        Assert.Equal(2, queue.VisibleOpenCount);
        Assert.Equal(1, queue.VisibleRenderCount);
        Assert.False(WorkerSchedulingPolicy.ShouldStartSecondary(
            canUseSecondary: true,
            primaryBusy: true,
            secondaryExists: false,
            queuedVisibleUncachedRenders: queue.VisibleRenderCount));

        queue.Enqueue(new object(), MarkdownSvgRenderPriority.Visible, isRender: true);

        Assert.Equal(2, queue.VisibleRenderCount);
        Assert.True(WorkerSchedulingPolicy.ShouldStartSecondary(
            canUseSecondary: true,
            primaryBusy: true,
            secondaryExists: false,
            queuedVisibleUncachedRenders: queue.VisibleRenderCount));
    }

    [Fact]
    public void VisibleOpenCountExcludesOverscanAndNonOpenNonRenderWork()
    {
        var queue = new WorkerSchedulingQueue<object>();
        queue.Enqueue(new object(), MarkdownSvgRenderPriority.Visible, isRender: false, isOpen: true);
        queue.Enqueue(new object(), MarkdownSvgRenderPriority.Visible, isRender: false);
        queue.Enqueue(new object(), MarkdownSvgRenderPriority.Overscan, isRender: false, isOpen: true);
        queue.Enqueue(new object(), MarkdownSvgRenderPriority.Visible, isRender: true);

        Assert.Equal(1, queue.VisibleOpenCount);
        Assert.Equal(1, queue.VisibleRenderCount);
    }

    [Fact]
    public void PromotedWorkJoinsVisibleTierInItsOriginalArrivalOrderAndUpdatesCounters()
    {
        var queue = new WorkerSchedulingQueue<object>();
        object promotedFirst = new();
        object alreadyVisible = new();
        object promotedLast = new();
        queue.Enqueue(promotedFirst, MarkdownSvgRenderPriority.Overscan, isRender: false, isOpen: true);
        queue.Enqueue(alreadyVisible, MarkdownSvgRenderPriority.Visible, isRender: false, isOpen: true);
        queue.Enqueue(promotedLast, MarkdownSvgRenderPriority.Overscan, isRender: true, requiresFontCatalog: true);

        Assert.Equal(1, queue.VisibleOpenCount);
        Assert.Equal(0, queue.VisibleRenderCount);
        Assert.True(queue.PromoteVisible(promotedFirst));
        Assert.True(queue.PromoteVisible(promotedLast));
        Assert.False(queue.PromoteVisible(promotedFirst));
        Assert.Equal(2, queue.VisibleOpenCount);
        Assert.Equal(1, queue.VisibleRenderCount);
        Assert.Equal(1, queue.VisibleTextRenderCount);

        Assert.True(queue.TryDequeuePrimary(out object? first));
        Assert.Same(promotedFirst, first);
        Assert.True(queue.TryDequeuePrimary(out object? second));
        Assert.Same(alreadyVisible, second);
        Assert.True(queue.TryDequeuePrimary(out object? third));
        Assert.Same(promotedLast, third);
        Assert.Equal(0, queue.VisibleOpenCount);
        Assert.Equal(0, queue.VisibleRenderCount);
    }

    [Fact]
    public void PumpSynchronizationPromotesTicketBeforeCountsAndDequeue()
    {
        var queue = new WorkerSchedulingQueue<PromotionProbe>();
        var probe = new PromotionProbe { IsVisible = false };
        queue.Enqueue(probe, MarkdownSvgRenderPriority.Overscan, isRender: true, requiresFontCatalog: true);

        probe.IsVisible = true;
        Assert.Equal(1, queue.PromoteVisibleWhere(static item => item.IsVisible));
        Assert.Equal(1, queue.VisibleRenderCount);
        Assert.Equal(1, queue.VisibleTextRenderCount);
        Assert.Equal(0, queue.PromoteVisibleWhere(static item => item.IsVisible));

        Assert.True(queue.TryDequeuePrimary(out PromotionProbe? dequeued));
        Assert.Same(probe, dequeued);
    }

    private sealed class PromotionProbe
    {
        public bool IsVisible { get; set; }
    }

    [Theory]
    [InlineData(true, false, 2, true)]
    [InlineData(true, false, 1, false)]
    [InlineData(false, false, 2, false)]
    [InlineData(true, true, 2, false)]
    public void SecondaryFontCatalogWarmupRequiresPrimaryReadyAndTwoQueuedTextRenders(
        bool primaryReady,
        bool secondaryReady,
        int queuedVisibleTextRenders,
        bool expected) =>
        Assert.Equal(expected, WorkerSchedulingPolicy.ShouldWarmSecondaryFontCatalog(
            primaryReady,
            secondaryReady,
            queuedVisibleTextRenders));

    [Theory]
    [InlineData(true, 1, true)]
    [InlineData(true, 0, false)]
    [InlineData(false, 1, false)]
    public void ReleaseRetainsFailedSecondarySentinelOnlyUntilQueuedBatchDrains(
        bool failedWarmup,
        int queuedWorkCount,
        bool expected) =>
        Assert.Equal(expected, WorkerSchedulingPolicy.ShouldRetainFailedSecondaryAfterRelease(
            failedWarmup,
            queuedWorkCount));

    [Fact]
    public async Task SecondaryWarmupOpensGeometryAfterStartupWhileFontCatalogRefreshes()
    {
        var readiness = new SecondaryWorkerReadiness();
        Assert.True(readiness.TryBeginWarmup(workerAlreadyReady: false, fontCatalogAlreadyReady: false));

        var startEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowStart = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fontsEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowFonts = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);

        Task warmup = readiness.WarmAsync(
            _ =>
            {
                startEntered.TrySetResult();
                return allowStart.Task;
            },
            () => true,
            _ =>
            {
                fontsEntered.TrySetResult();
                return allowFonts.Task;
            },
            CancellationToken.None);

        await startEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(readiness.CanDispatch(requiresFontCatalog: false));
        Assert.False(readiness.CanDispatch(requiresFontCatalog: true));

        allowStart.TrySetResult();
        await fontsEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(readiness.IsWorkerReady);
        Assert.True(readiness.IsWarming);
        Assert.True(readiness.CanDispatch(requiresFontCatalog: false));
        Assert.False(readiness.CanDispatch(requiresFontCatalog: true, requestedGeneration: 1));

        allowFonts.TrySetResult(1);
        await warmup.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(readiness.IsWorkerReady);
        Assert.True(readiness.IsFontCatalogReady);
        Assert.Equal(1L, readiness.FontCatalogReadyGeneration);
        Assert.True(readiness.CanDispatch(requiresFontCatalog: false));
        Assert.True(readiness.CanDispatch(requiresFontCatalog: true, requestedGeneration: 1));
    }

    [Fact]
    public async Task SecondaryWarmupSkipsFontCatalogWhenTextPressureHasDisappeared()
    {
        var readiness = new SecondaryWorkerReadiness();
        Assert.True(readiness.TryBeginWarmup(workerAlreadyReady: false, fontCatalogAlreadyReady: false));
        int fontStarts = 0;

        await readiness.WarmAsync(
            _ => Task.CompletedTask,
            () => false,
            _ =>
            {
                Interlocked.Increment(ref fontStarts);
                return Task.CompletedTask;
            },
            CancellationToken.None);

        Assert.Equal(0, Volatile.Read(ref fontStarts));
        Assert.True(readiness.CanDispatch(requiresFontCatalog: false));
        Assert.False(readiness.CanDispatch(requiresFontCatalog: true));
    }

    [Fact]
    public async Task SecondaryWarmupFailureLatchesSlotForSafeFallbackWithoutRetryLoop()
    {
        var readiness = new SecondaryWorkerReadiness();
        Assert.True(readiness.TryBeginWarmup(workerAlreadyReady: false, fontCatalogAlreadyReady: false));

        await Assert.ThrowsAsync<InvalidOperationException>(() => readiness.WarmAsync(
            _ => throw new InvalidOperationException("fake worker start failed"),
            () => false,
            _ => Task.CompletedTask,
            CancellationToken.None));

        Assert.False(readiness.IsWarming);
        Assert.False(readiness.IsWorkerReady);
        Assert.False(readiness.IsFontCatalogReady);
        Assert.True(readiness.HasFailed);
        Assert.False(readiness.CanDispatch(requiresFontCatalog: false));
        Assert.False(readiness.TryBeginWarmup(workerAlreadyReady: false, fontCatalogAlreadyReady: false));
    }
}
