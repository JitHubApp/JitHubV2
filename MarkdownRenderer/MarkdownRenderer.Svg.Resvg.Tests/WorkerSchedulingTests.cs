using System.Collections.Concurrent;
using System.Diagnostics.Tracing;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using MarkdownRenderer.Images;
using MarkdownRenderer.Svg.Resvg.Internal;
using Xunit;

namespace MarkdownRenderer.Svg.Resvg.Tests;

public sealed class WorkerSchedulingTests
{
    [Fact]
    public async Task OpenMappingReportsCompletedPhaseOutsideTheSourceAndRasterRanges()
    {
        byte[] source = Encoding.UTF8.GetBytes(
            "<svg xmlns='http://www.w3.org/2000/svg' width='16' height='8'><rect width='16' height='8'/></svg>");
        var request = new MarkdownSvgOpenRequest(source);
        var options = new ResvgMarkdownSvgRendererOptions();
        SvgPreflightResult preflight = await SvgPreflight.InspectAsync(
            request, options, CancellationToken.None);
        using var mapping = new SharedMemoryLease(
            $"MarkdownRenderer.Resvg.Test.{Guid.NewGuid():N}", preflight.Source, 0);
        using var pool = new WorkerPool(options);

        WorkerOpenResult result = await pool.OpenDocumentAsync(
            preflight, request, mapping, CancellationToken.None);

        Assert.Equal(WorkerStatus.Ok, result.Response.Status);
        Assert.Equal(7, mapping.ReadOpenProgressPhase());
        Assert.Equal(0, mapping.Memory.Length);
    }

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
            WindowsWorkerProcess.WorkerMemorySnapshot memory = worker.GetProcessMemorySnapshot();
            Assert.True(memory.WorkingSetKiB > 0);
            Assert.True(memory.PrivateCommitKiB > 0,
                $"working={memory.WorkingSetKiB}, private={memory.PrivateCommitKiB}, faults={memory.PageFaults}");
            Assert.True(memory.PageFaults >= 0);
            worker.DisposeForRestart();
            Assert.Equal(
                WindowsWorkerProcess.WorkerMemorySnapshot.Unavailable,
                worker.GetProcessMemorySnapshot());
        }
    }

    [Fact]
    public async Task WorkerExecutableIdentityMatchesThePackagedBinaryWhenDiagnosticsAreEnabled()
    {
        using var listener = new TimeoutListener();
        var options = new ResvgMarkdownSvgRendererOptions();
        using var job = WindowsWorkerProcess.CreateConstrainedJob(
            options.WorkerCommitBytes,
            activeProcessLimit: 1);
        string executable = Path.Combine(AppContext.BaseDirectory, WorkerProtocol.WorkerFileName);
        await using var executableStream = File.OpenRead(executable);
        string expectedHash = Convert.ToHexString(await SHA256.HashDataAsync(executableStream));

        WindowsWorkerProcess worker = await WindowsWorkerProcess.StartAsync(
            executable,
            options,
            job,
            CancellationToken.None);
        try
        {
            Assert.Equal(expectedHash, worker.WorkerExecutableSha256);
        }
        finally
        {
            worker.DisposeForRestart();
        }
    }

    [Fact]
    public void WorkerTimeoutEvidenceContainsOnlyTimingAndTransportState()
    {
        using var listener = new TimeoutListener();

        WorkerTimeoutEvents.Log.Timeout((int)WorkerOperation.Open, 3_000, 1_250,
            2, 4, 0, 4, 120_832, 94_208, 6_400, 2_850,
            "0123456789ABCDEF", "FEDCBA9876543210");

        Assert.Contains(((int)WorkerOperation.Open, 3_000, 1_250,
            2, 4, 0, 4, 120_832, 94_208, 6_400, 2_850,
            "0123456789ABCDEF", "FEDCBA9876543210"), listener.Events);
    }

    [Fact]
    public void FontCatalogProbeTimeoutEvidenceKeepsTheWholeInitializationWindow()
    {
        WorkerTimeoutEvidenceScope initialization = new(TimeSpan.FromSeconds(15), 1_000_000);
        WorkerTimeoutEvidenceScope finalProbe = WorkerTimeoutEvidenceScope.Resolve(
            TimeSpan.FromTicks(1),
            1_200_000,
            initialization);
        Assert.Equal(TimeSpan.FromSeconds(15), finalProbe.TotalDeadline);
        Assert.Equal(1_000_000, finalProbe.WorkerCpuAtStart);

        WorkerTimeoutEvidenceScope ordinaryRequest = WorkerTimeoutEvidenceScope.Resolve(
            TimeSpan.FromSeconds(3),
            1_200_000,
            null);
        Assert.Equal(TimeSpan.FromSeconds(3), ordinaryRequest.TotalDeadline);
        Assert.Equal(1_200_000, ordinaryRequest.WorkerCpuAtStart);
        Assert.NotEqual(0, ordinaryRequest.WallStartedAt);
    }

    private sealed class TimeoutListener : EventListener
    {
        public ConcurrentQueue<(int Stage, int DeadlineMilliseconds, int WorkerProcessCpuMilliseconds,
            int TransportPhase, int RequestWriteMilliseconds, int WorkerExited,
            int OpenProgressPhase, int WorkerWorkingSetKiB,
            int WorkerPrivateCommitKiB, int WorkerPageFaults, int ElapsedWallMilliseconds,
            string WorkerInputSha256, string WorkerExecutableSha256)> Events { get; } = new();

        protected override void OnEventSourceCreated(EventSource eventSource)
        {
            if (eventSource.Name == "MarkdownRenderer.Svg.Resvg.Worker")
                EnableEvents(eventSource, EventLevel.Warning);
        }

        protected override void OnEventWritten(EventWrittenEventArgs eventData)
        {
            if (eventData.EventId == 1 && eventData.Payload is { Count: 13 } payload &&
                payload[0] is int stage && payload[1] is int deadlineMilliseconds &&
                payload[2] is int workerProcessCpuMilliseconds &&
                payload[3] is int transportPhase &&
                payload[4] is int requestWriteMilliseconds &&
                payload[5] is int workerExited && payload[6] is int openProgressPhase &&
                payload[7] is int workerWorkingSetKiB &&
                payload[8] is int workerPrivateCommitKiB &&
                payload[9] is int workerPageFaults &&
                payload[10] is int elapsedWallMilliseconds &&
                payload[11] is string workerInputSha256 &&
                payload[12] is string workerExecutableSha256)
            {
                Events.Enqueue((stage, deadlineMilliseconds, workerProcessCpuMilliseconds,
                    transportPhase, requestWriteMilliseconds, workerExited, openProgressPhase,
                    workerWorkingSetKiB, workerPrivateCommitKiB, workerPageFaults,
                    elapsedWallMilliseconds, workerInputSha256, workerExecutableSha256));
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
