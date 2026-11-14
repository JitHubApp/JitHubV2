using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using MarkdownRenderer.Images.Svg;
using MarkdownRenderer.Svg.Resvg.Internal;
using MarkdownRenderer.Svg.Resvg.Internal.Diagnostics;
using MarkdownRenderer.Svg.Resvg.Internal.Preflight;
using MarkdownRenderer.Svg.Resvg.Internal.Workers;
using MarkdownRenderer.Svg.Resvg.Internal.Workers.Processes;
using MarkdownRenderer.Svg.Resvg.Internal.Workers.Scheduling;
using MarkdownRenderer.Svg.Resvg.Internal.Workers.Transport;
using MarkdownRenderer.Svg.Resvg.Tests.TestSupport;
using Xunit;

namespace MarkdownRenderer.Svg.Resvg.Tests.Internal.Workers.Scheduling;

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
