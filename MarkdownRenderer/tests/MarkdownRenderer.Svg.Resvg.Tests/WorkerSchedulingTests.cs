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
    public async Task OpenSourceMappingIsReleasedAfterAttachmentCompletes()
    {
        byte[] source = Encoding.UTF8.GetBytes(
            "<svg xmlns='http://www.w3.org/2000/svg' width='16' height='8'><rect width='16' height='8'/></svg>");
        var request = new MarkdownSvgOpenRequest(source);
        var options = new ResvgMarkdownSvgRendererOptions();
        SvgPreflightResult preflight = await SvgPreflight.InspectAsync(
            request, options, CancellationToken.None);
        SharedMemoryLease? sourceMapping = null;
        using var pool = new WorkerPool(
            options,
            TimeProvider.System,
            static () => true,
            (name, bytes) => sourceMapping = new SharedMemoryLease(name, bytes, 0));

        WorkerOpenResult result = await pool.OpenDocumentAsync(
            preflight,
            request,
            CancellationToken.None);

        Assert.Equal(WorkerStatus.Ok, result.Response.Status);
        Assert.True(result.Response.WorkerInstanceId > 0);
        Assert.Equal(result.Response.WorkerInstanceId, result.Document.PreferredWorkerInstanceId);
        Assert.NotNull(sourceMapping);
        Assert.Throws<ObjectDisposedException>(() => sourceMapping!.ReadOpenProgressPhase());
    }

    [Fact]
    public async Task RecoverablePowerQueryFailureKeepsAdmissionOnPrimaryAndDoesNotStrandWork()
    {
        int queryCount = 0;
        var policySnapshots = new ConcurrentQueue<SvgWorkerPolicySnapshot>();
        using WorkerScheduler pool = TestWorkerSchedulers.Create(
            () =>
            {
                Interlocked.Increment(ref queryCount);
                throw new InvalidOperationException("Power state is unavailable for this test.");
            },
            new SvgWorkerPolicyTrace(observer: policySnapshots.Enqueue));
        using var queuedCancellation = new CancellationTokenSource();
        IDisposable? firstLease = null;
        IDisposable? secondLease = null;
        Task<WorkerLease>? queuedAdmission = null;

        try
        {
            firstLease = await AcquireWorkerLeaseAsync(pool, CancellationToken.None);
            AssertNoSecondaryWorker(policySnapshots);

            Task<WorkerLease> pendingAdmission = InvokeAcquireAsync(pool, queuedCancellation.Token);
            queuedAdmission = pendingAdmission;
            Assert.False(pendingAdmission.IsCompleted);
            Assert.Contains(policySnapshots, snapshot =>
                snapshot.EnergySaverQueryFailed == 1 &&
                snapshot.EnergySaverOn == 1 &&
                snapshot.CanUseSecondary == 0 &&
                snapshot.ShouldStartSecondary == 0 &&
                snapshot.VisibleRenderCount == 1);
            AssertNoSecondaryWorker(policySnapshots);

            queuedCancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await pendingAdmission.WaitAsync(TimeSpan.FromSeconds(5)));
            queuedAdmission = null;
            AssertNoSecondaryWorker(policySnapshots);

            firstLease.Dispose();
            firstLease = null;

            // If the failed power query had escaped after enqueue, that orphan
            // would be dispatched first after the primary lease is released,
            // leaving this independently-owned admission pending forever.
            secondLease = await AcquireWorkerLeaseAsync(pool, CancellationToken.None);
            AssertNoSecondaryWorker(policySnapshots);
            secondLease.Dispose();
            secondLease = null;
            Assert.True(Volatile.Read(ref queryCount) >= 3);
        }
        finally
        {
            queuedCancellation.Cancel();
            if (queuedAdmission is not null)
            {
                try
                {
                    await queuedAdmission.WaitAsync(TimeSpan.FromSeconds(5));
                }
                catch (Exception)
                {
                    // Cleanup observes either cancellation or a failed test's
                    // admission error so no task fault escapes unobserved.
                }

                if (queuedAdmission.IsCompletedSuccessfully)
                    (await queuedAdmission).Dispose();
            }
            secondLease?.Dispose();
            firstLease?.Dispose();
        }
    }

    [Fact]
    public void PowerQueryFailureClassifierDoesNotHideFatalResourceFaults()
    {
        Assert.False(WorkerEnergySaver.IsRecoverablePowerQueryFailure(new OutOfMemoryException()));
        Assert.False(WorkerEnergySaver.IsRecoverablePowerQueryFailure(new InsufficientMemoryException()));
        Assert.False(WorkerEnergySaver.IsRecoverablePowerQueryFailure(new InsufficientExecutionStackException()));
        Assert.False(WorkerEnergySaver.IsRecoverablePowerQueryFailure(new StackOverflowException()));
        Assert.False(WorkerEnergySaver.IsRecoverablePowerQueryFailure(new AccessViolationException()));
        Assert.False(WorkerEnergySaver.IsRecoverablePowerQueryFailure(new SEHException()));
        Assert.True(WorkerEnergySaver.IsRecoverablePowerQueryFailure(new InvalidOperationException("recoverable")));
        Assert.True(WorkerEnergySaver.IsRecoverablePowerQueryFailure(new AggregateException(
            new InvalidOperationException("recoverable"),
            new PlatformNotSupportedException())));
        Assert.False(WorkerEnergySaver.IsRecoverablePowerQueryFailure(new AggregateException(
            new InvalidOperationException("recoverable"),
            new AggregateException(new AccessViolationException()))));
    }

    private static async Task<IDisposable> AcquireWorkerLeaseAsync(
        WorkerScheduler pool,
        CancellationToken cancellationToken) =>
        await InvokeAcquireAsync(pool, cancellationToken).WaitAsync(TimeSpan.FromSeconds(5));

    private static Task<WorkerLease> InvokeAcquireAsync(WorkerScheduler pool, CancellationToken cancellationToken) =>
        TestWorkerSchedulers.AcquireAsync(pool, cancellationToken);

    // The policy trace records every scheduling pass with visible work or a
    // secondary slot, so a secondary that was ever created or requested shows.
    private static void AssertNoSecondaryWorker(IEnumerable<SvgWorkerPolicySnapshot> snapshots) =>
        Assert.DoesNotContain(snapshots, snapshot =>
            snapshot.ShouldStartSecondary != 0 || snapshot.SecondaryReadinessState != 0);

    [Fact]
    public async Task RestartReleasesTheSingleProcessJobSlotBeforeStartingAnotherWorker()
    {
        var options = new ResvgMarkdownSvgRendererOptions();
        using var job = WorkerProcessSandbox.CreateConstrainedJob(
            options.WorkerCommitBytes,
            activeProcessLimit: 1);
        string executable = Path.Combine(AppContext.BaseDirectory, WorkerProtocol.WorkerFileName);

        for (int attempt = 0; attempt < 4; attempt++)
        {
            WindowsWorkerProcess worker = await new WindowsWorkerProcessLauncher(executable, options).StartAsync(
                job,
                CancellationToken.None);
            Assert.False(worker.HasExited);
            WorkerMemorySnapshot memory = worker.GetProcessMemorySnapshot();
            Assert.True(memory.WorkingSetKiB > 0);
            Assert.True(memory.PrivateCommitKiB > 0,
                $"working={memory.WorkingSetKiB}, private={memory.PrivateCommitKiB}, faults={memory.PageFaults}");
            Assert.True(memory.PageFaults >= 0);
            worker.DisposeForRestart();
            Assert.Equal(
                WorkerMemorySnapshot.Unavailable,
                worker.GetProcessMemorySnapshot());
        }
    }

    [Fact]
    public async Task WorkerExecutableIdentityMatchesThePackagedBinaryWhenDiagnosticsAreEnabled()
    {
        using var listener = new TimeoutListener();
        var options = new ResvgMarkdownSvgRendererOptions();
        using var job = WorkerProcessSandbox.CreateConstrainedJob(
            options.WorkerCommitBytes,
            activeProcessLimit: 1);
        string executable = Path.Combine(AppContext.BaseDirectory, WorkerProtocol.WorkerFileName);
        await using var executableStream = File.OpenRead(executable);
        string expectedHash = Convert.ToHexString(await SHA256.HashDataAsync(executableStream));

        WindowsWorkerProcess worker = await new WindowsWorkerProcessLauncher(executable, options).StartAsync(
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

    [Fact]
    public void FontCatalogDeadlineEvidenceIncludesTheContentFreeInitializationPhase()
    {
        using var listener = new TimeoutListener();

        WorkerTimeoutEvents.Log.FontCatalogDeadlinePhase("user-roaming-fonts");

        Assert.Contains("user-roaming-fonts", listener.FontCatalogPhases);
    }

    private sealed class TimeoutListener : EventListener
    {
        public ConcurrentQueue<(int Stage, int DeadlineMilliseconds, int WorkerProcessCpuMilliseconds,
            int TransportPhase, int RequestWriteMilliseconds, int WorkerExited,
            int OpenProgressPhase, int WorkerWorkingSetKiB,
            int WorkerPrivateCommitKiB, int WorkerPageFaults, int ElapsedWallMilliseconds,
            string WorkerInputSha256, string WorkerExecutableSha256)> Events { get; } = new();
        public ConcurrentQueue<string> FontCatalogPhases { get; } = new();

        protected override void OnEventSourceCreated(EventSource eventSource)
        {
            if (eventSource.Name == "MarkdownRenderer.Svg.Resvg.Worker")
                EnableEvents(eventSource, EventLevel.Warning);
        }

        protected override void OnEventWritten(EventWrittenEventArgs eventData)
        {
            if (eventData.EventId == 2 && eventData.Payload is { Count: 1 } phasePayload &&
                phasePayload[0] is string phase)
            {
                FontCatalogPhases.Enqueue(phase);
                return;
            }

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
