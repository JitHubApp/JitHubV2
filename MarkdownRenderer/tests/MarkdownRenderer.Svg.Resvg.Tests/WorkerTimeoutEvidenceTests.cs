using System.Collections.Concurrent;
using System.Diagnostics.Tracing;
using System.Security.Cryptography;
using MarkdownRenderer.Svg.Resvg.Internal;
using MarkdownRenderer.Svg.Resvg.Internal.Diagnostics;
using MarkdownRenderer.Svg.Resvg.Internal.Workers.Processes;
using Xunit;

namespace MarkdownRenderer.Svg.Resvg.Tests;

/// <summary>
/// Pins the worker timeout diagnostics: the packaged executable digest, the
/// content-free timeout event payload, and the initialization window that
/// font-catalog probe timeouts report.
/// </summary>
public sealed class WorkerTimeoutEvidenceTests
{
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
}
