using System.Collections.Concurrent;
using System.Diagnostics.Tracing;
using System.Runtime.InteropServices;
using MarkdownRenderer.Images;
using MarkdownRenderer.Svg.Resvg.Internal;
using Xunit;

namespace MarkdownRenderer.Svg.Resvg.Tests;

public sealed class WorkerSchedulingTests
{
    [Fact]
    public async Task RestartReleasesTheSingleProcessJobSlotBeforeStartingAnotherWorker()
    {
        var options = new ResvgMarkdownSvgRendererOptions();
        using var job = WindowsWorkerProcess.CreateConstrainedJob(
            options.WorkerCommitBytes,
            activeProcessLimit: 1);
        string executable = Path.Combine(AppContext.BaseDirectory, WorkerProtocol.WorkerFileName);

        for (int attempt = 0; attempt < 4; attempt++)
        {
            WindowsWorkerProcess worker = await WindowsWorkerProcess.StartAsync(
                executable,
                options,
                job,
                CancellationToken.None);
            Assert.False(worker.HasExited);
            worker.DisposeForRestart();
        }
    }

    [Fact]
    public void WorkerTimeoutEvidenceContainsOnlyPhaseDeadlineAndWorkerCpu()
    {
        using var listener = new TimeoutListener();

        WorkerTimeoutEvents.Log.Timeout((int)WorkerOperation.Render, 3_000, 1_250);

        Assert.Contains(((int)WorkerOperation.Render, 3_000, 1_250), listener.Events);
    }

    private sealed class TimeoutListener : EventListener
    {
        public ConcurrentQueue<(int Stage, int DeadlineMilliseconds, int WorkerProcessCpuMilliseconds)> Events { get; } = new();

        protected override void OnEventSourceCreated(EventSource eventSource)
        {
            if (eventSource.Name == "MarkdownRenderer.Svg.Resvg.Worker")
                EnableEvents(eventSource, EventLevel.Warning);
        }

        protected override void OnEventWritten(EventWrittenEventArgs eventData)
        {
            if (eventData.EventId == 1 && eventData.Payload is { Count: 3 } payload &&
                payload[0] is int stage && payload[1] is int deadlineMilliseconds &&
                payload[2] is int workerProcessCpuMilliseconds)
            {
                Events.Enqueue((stage, deadlineMilliseconds, workerProcessCpuMilliseconds));
            }
        }
    }

    [Theory]
    [InlineData(Architecture.X64, 8, false, true)]
    [InlineData(Architecture.Arm64, 16, false, true)]
    [InlineData(Architecture.X64, 7, false, false)]
    [InlineData(Architecture.Arm64, 8, true, false)]
    [InlineData(Architecture.X86, 32, false, false)]
    [InlineData(Architecture.Arm, 32, false, false)]
    public void SecondaryEligibility_IsArchitectureCpuAndEnergyBounded(
        Architecture architecture,
        int processors,
        bool energySaverOn,
        bool expected) =>
        Assert.Equal(expected, WorkerSchedulingPolicy.CanUseSecondary(
            architecture,
            processors,
            energySaverOn));

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
    public void Queue_IsStableVisibleFirstAndRemovesCanceledStaleWork()
    {
        var queue = new WorkerSchedulingQueue<object>();
        object overscan1 = new();
        object canceledOverscan = new();
        object visible1 = new();
        object visible2 = new();
        queue.Enqueue(overscan1, MarkdownSvgRenderPriority.Overscan, isRender: true);
        queue.Enqueue(canceledOverscan, MarkdownSvgRenderPriority.Overscan, isRender: true);
        queue.Enqueue(visible1, MarkdownSvgRenderPriority.Visible, isRender: true);
        queue.Enqueue(visible2, MarkdownSvgRenderPriority.Visible, isRender: true);

        Assert.True(queue.Remove(canceledOverscan));
        Assert.Equal(3, queue.Count);
        Assert.True(queue.TryDequeuePrimary(out object? first));
        Assert.Same(visible1, first);
        Assert.True(queue.TryDequeuePrimary(out object? second));
        Assert.Same(visible2, second);
        Assert.True(queue.TryDequeuePrimary(out object? third));
        Assert.Same(overscan1, third);
    }

    [Fact]
    public void Secondary_ConsumesOnlyVisibleRenderWork()
    {
        var queue = new WorkerSchedulingQueue<object>();
        object visibleOpen = new();
        object overscanRender = new();
        object visibleRender = new();
        queue.Enqueue(visibleOpen, MarkdownSvgRenderPriority.Visible, isRender: false);
        queue.Enqueue(overscanRender, MarkdownSvgRenderPriority.Overscan, isRender: true);
        queue.Enqueue(visibleRender, MarkdownSvgRenderPriority.Visible, isRender: true);

        Assert.Equal(1, queue.VisibleRenderCount);
        Assert.True(queue.TryDequeueSecondary(out object? selected));
        Assert.Same(visibleRender, selected);
        Assert.Equal(2, queue.Count);
    }

    [Fact]
    public void TimePolicies_UseExactThirtySecondAndHundredMillisecondBoundaries()
    {
        Assert.Equal(TimeSpan.FromSeconds(15), WorkerSchedulingPolicy.InitializationDeadline);
        Assert.True(
            WorkerSchedulingPolicy.InitializationDeadline >
            ResvgMarkdownSvgRendererOptions.HardMaxRequestDeadline);

        DateTimeOffset origin = DateTimeOffset.UnixEpoch;
        Assert.False(WorkerSchedulingPolicy.ShouldRetireSecondary(
            origin,
            origin.AddMilliseconds(29_999)));
        Assert.True(WorkerSchedulingPolicy.ShouldRetireSecondary(
            origin,
            origin.AddSeconds(30)));

        Assert.False(WorkerSchedulingPolicy.ShouldTerminateCanceledActive(
            true,
            1,
            origin,
            origin.AddMilliseconds(99)));
        Assert.True(WorkerSchedulingPolicy.ShouldTerminateCanceledActive(
            true,
            1,
            origin,
            origin.AddMilliseconds(100)));
        Assert.False(WorkerSchedulingPolicy.ShouldTerminateCanceledActive(
            true,
            0,
            origin,
            origin.AddSeconds(1)));
    }
}
