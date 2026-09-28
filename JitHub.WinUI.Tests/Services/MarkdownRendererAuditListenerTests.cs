using System.Diagnostics.Tracing;
using System.Security.Cryptography;
using System.Text.Json;
using JitHub.Services.Markdown;
using MarkdownRenderer.Images;
using Xunit;

namespace JitHub.WinUI.Tests.Services;

[CollectionDefinition("Markdown renderer audit environment", DisableParallelization = true)]
public sealed class MarkdownRendererAuditEnvironmentCollection
{
}

[Collection("Markdown renderer audit environment")]
public sealed class MarkdownRendererAuditListenerTests
{
    [Fact]
    public void ImageResolutionRecordsExactPayloadIdentityWithoutRetainingBytes()
    {
        const string variable = "JITHUB_MARKDOWN_IMAGE_RESOLUTION_EVIDENCE_PATH";
        string path = Path.Combine(
            Path.GetTempPath(),
            $"jithub-image-resolution-audit-{Guid.NewGuid():N}.ndjson");
        string? previousPath = Environment.GetEnvironmentVariable(variable);
        byte[] bytes = [1, 2, 3, 4, 5];
        try
        {
            Environment.SetEnvironmentVariable(variable, path);
            MarkdownLifecycleAutomationBridge.ConfigureLaunchOptions(true, null);
            MarkdownLifecycleAutomationBridge.RecordImageResolution(
                "fixture.svg",
                MarkdownImageResolution.Resolved(new MarkdownImageAsset(bytes, "image/svg+xml")),
                DateTimeOffset.UtcNow,
                12.5);

            string line = Assert.Single(File.ReadAllLines(path));
            using JsonDocument document = JsonDocument.Parse(line);
            Assert.Equal(
                Convert.ToHexString(SHA256.HashData(bytes)),
                document.RootElement.GetProperty("ContentSha256").GetString());
            Assert.Equal(bytes.Length, document.RootElement.GetProperty("ByteLength").GetInt32());
            Assert.False(document.RootElement.TryGetProperty("Bytes", out _));
            Assert.False(document.RootElement.TryGetProperty("Payload", out _));

            MarkdownLifecycleAutomationBridge.RecordImageResolution(
                "missing.svg",
                MarkdownImageResolution.Unavailable,
                DateTimeOffset.UtcNow,
                1);
            string unavailableLine = File.ReadAllLines(path)[1];
            using JsonDocument unavailableDocument = JsonDocument.Parse(unavailableLine);
            Assert.Equal(JsonValueKind.Null,
                unavailableDocument.RootElement.GetProperty("ContentSha256").ValueKind);
        }
        finally
        {
            MarkdownLifecycleAutomationBridge.ConfigureLaunchOptions(false, null);
            Environment.SetEnvironmentVariable(variable, previousPath);
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void PreflightRejectionRecordsOnlyPolicyAndContentIdentity()
    {
        const string variable = "JITHUB_MARKDOWN_SVG_PREFLIGHT_EVIDENCE_PATH";
        string path = Path.Combine(
            Path.GetTempPath(),
            $"jithub-svg-preflight-audit-{Guid.NewGuid():N}.ndjson");
        string? previousPath = Environment.GetEnvironmentVariable(variable);
        try
        {
            Environment.SetEnvironmentVariable(variable, path);
            MarkdownLifecycleAutomationBridge.ConfigureLaunchOptions(true, null);
            using var listener = new MarkdownRendererAuditListener();

            TestPreflightEventSource.Log.Rejected("missing-root", 469, new string('A', 64));

            string line = Assert.Single(File.ReadAllLines(path));
            using JsonDocument document = JsonDocument.Parse(line);
            Assert.Equal("missing-root", document.RootElement.GetProperty("Reason").GetString());
            Assert.Equal(469, document.RootElement.GetProperty("SourceByteLength").GetInt32());
            Assert.Equal(new string('A', 64), document.RootElement.GetProperty("SourceSha256").GetString());
            Assert.False(document.RootElement.TryGetProperty("Source", out _));
            Assert.False(document.RootElement.TryGetProperty("Url", out _));
        }
        finally
        {
            MarkdownLifecycleAutomationBridge.ConfigureLaunchOptions(false, null);
            Environment.SetEnvironmentVariable(variable, previousPath);
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void ShutdownStageSignalIsAuditOnlyAndKeepsOnlyTheLatestStage()
    {
        const string variable = "JITHUB_MARKDOWN_SHUTDOWN_STAGE_PATH";
        string path = Path.Combine(
            Path.GetTempPath(),
            $"jithub-markdown-shutdown-{Guid.NewGuid():N}.json");
        string? previousPath = Environment.GetEnvironmentVariable(variable);
        try
        {
            Environment.SetEnvironmentVariable(variable, path);
            MarkdownLifecycleAutomationBridge.ConfigureLaunchOptions(false, null);
            MarkdownLifecycleAutomationBridge.SignalShutdownStage("not-an-audit");
            Assert.False(File.Exists(path));

            MarkdownLifecycleAutomationBridge.ConfigureLaunchOptions(true, null);
            MarkdownLifecycleAutomationBridge.SignalShutdownStage("markdown-shutdown-started");
            MarkdownLifecycleAutomationBridge.SignalShutdownStage("performance-session-disposal-started");

            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
            Assert.Equal(
                "performance-session-disposal-started",
                document.RootElement.GetProperty("Stage").GetString());
            Assert.Equal(
                Environment.ProcessId,
                document.RootElement.GetProperty("ProcessId").GetInt32());
            Assert.False(document.RootElement.TryGetProperty("Source", out _));
            Assert.False(document.RootElement.TryGetProperty("Url", out _));
        }
        finally
        {
            MarkdownLifecycleAutomationBridge.ConfigureLaunchOptions(false, null);
            Environment.SetEnvironmentVariable(variable, previousPath);
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void WorkerTimeoutIsRecordedWithoutSourceData()
    {
        const string variable = "JITHUB_MARKDOWN_SVG_WORKER_EVIDENCE_PATH";
        string path = Path.Combine(
            Path.GetTempPath(),
            $"jithub-svg-worker-audit-{Guid.NewGuid():N}.ndjson");
        string? previousPath = Environment.GetEnvironmentVariable(variable);
        try
        {
            Environment.SetEnvironmentVariable(variable, path);
            MarkdownLifecycleAutomationBridge.ConfigureLaunchOptions(
                fixtureEnabled: true,
                targetHost: null);
            using var listener = new MarkdownRendererAuditListener();

            TestWorkerEventSource.Log.Timeout(
                stage: 2,
                deadlineMilliseconds: 3_000,
                workerProcessCpuMilliseconds: 1_250,
                transportPhase: 2,
                requestWriteMilliseconds: 4,
                workerExited: 0,
                openProgressPhase: 4,
                workerWorkingSetKiB: 120_832,
                workerPrivateCommitKiB: 94_208,
                workerPageFaults: 6_400);
            for (int progressPhase = 8; progressPhase <= 12; progressPhase++)
            {
                TestWorkerEventSource.Log.Timeout(
                    stage: 2,
                    deadlineMilliseconds: 3_000,
                    workerProcessCpuMilliseconds: 1_250,
                    transportPhase: 2,
                    requestWriteMilliseconds: 4,
                    workerExited: 0,
                    openProgressPhase: progressPhase,
                    workerWorkingSetKiB: 120_832,
                    workerPrivateCommitKiB: 94_208,
                    workerPageFaults: 6_400);
            }

            string[] lines = File.ReadAllLines(path);
            Assert.Equal(6, lines.Length);
            string line = lines[0];
            using JsonDocument document = JsonDocument.Parse(line);
            Assert.Equal("open", document.RootElement.GetProperty("Phase").GetString());
            Assert.Equal(3_000, document.RootElement.GetProperty("DeadlineMilliseconds").GetInt32());
            Assert.Equal(1_250, document.RootElement.GetProperty("WorkerProcessCpuMilliseconds").GetInt32());
            Assert.Equal("response-read", document.RootElement.GetProperty("TransportPhase").GetString());
            Assert.Equal(4, document.RootElement.GetProperty("RequestWriteMilliseconds").GetInt32());
            Assert.False(document.RootElement.GetProperty("WorkerExited").GetBoolean());
            Assert.Equal("security-inspected", document.RootElement.GetProperty("OpenProgressPhase").GetString());
            Assert.Equal(120_832, document.RootElement.GetProperty("WorkerWorkingSetKiB").GetInt32());
            Assert.Equal(94_208, document.RootElement.GetProperty("WorkerPrivateCommitKiB").GetInt32());
            Assert.Equal(6_400, document.RootElement.GetProperty("WorkerPageFaults").GetInt32());
            Assert.Equal(2_987, document.RootElement.GetProperty("ElapsedWallMilliseconds").GetInt32());
            Assert.Equal(new string('a', 64), document.RootElement.GetProperty("WorkerInputSha256").GetString());
            Assert.Equal(new string('b', 64), document.RootElement.GetProperty("WorkerExecutableSha256").GetString());
            Assert.False(document.RootElement.TryGetProperty("Source", out _));
            Assert.False(document.RootElement.TryGetProperty("Url", out _));

            string[] expectedProgressPhases =
            [
                "svg-options-constructed",
                "svg-resolver-configured",
                "svg-theme-ready",
                "usvg-input-ready",
                "usvg-tree-built",
            ];
            for (int index = 0; index < expectedProgressPhases.Length; index++)
            {
                using JsonDocument progressDocument = JsonDocument.Parse(lines[index + 1]);
                Assert.Equal(expectedProgressPhases[index], progressDocument.RootElement.GetProperty("OpenProgressPhase").GetString());
            }
        }
        finally
        {
            MarkdownLifecycleAutomationBridge.ConfigureLaunchOptions(
                fixtureEnabled: false,
                targetHost: null);
            Environment.SetEnvironmentVariable(variable, previousPath);
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public void RasterPreparationStagesRecordOnlyNumericAuditEvidence()
    {
        const string variable = "JITHUB_MARKDOWN_RASTER_PREPARATION_EVIDENCE_PATH";
        string path = Path.Combine(
            Path.GetTempPath(),
            $"jithub-raster-preparation-audit-{Guid.NewGuid():N}.ndjson");
        string? previousPath = Environment.GetEnvironmentVariable(variable);
        try
        {
            Environment.SetEnvironmentVariable(variable, path);
            MarkdownLifecycleAutomationBridge.ConfigureLaunchOptions(true, null);
            using var listener = new MarkdownRendererAuditListener();

            TestRasterPreparationEventSource.Log.Started(
                17, 8_948, ((long)313 << 32) | 38u);
            TestRasterPreparationEventSource.Log.Stage(17, 7, 10_000);

            string[] lines = File.ReadAllLines(path);
            Assert.Equal(2, lines.Length);
            using JsonDocument started = JsonDocument.Parse(lines[0]);
            Assert.Equal(17, started.RootElement.GetProperty("PreparationId").GetInt64());
            Assert.Equal(0, started.RootElement.GetProperty("Stage").GetInt32());
            Assert.Equal(8_948, started.RootElement.GetProperty("SourceBytes").GetInt64());
            Assert.Equal(313, started.RootElement.GetProperty("SourceWidth").GetInt32());
            Assert.Equal(38, started.RootElement.GetProperty("SourceHeight").GetInt32());
            Assert.False(started.RootElement.TryGetProperty("Source", out _));
            Assert.False(started.RootElement.TryGetProperty("Url", out _));
            Assert.False(started.RootElement.TryGetProperty("Bytes", out _));

            using JsonDocument stage = JsonDocument.Parse(lines[1]);
            Assert.Equal(7, stage.RootElement.GetProperty("Stage").GetInt32());
            Assert.True(stage.RootElement.GetProperty("ElapsedMilliseconds").GetDouble() > 0);
        }
        finally
        {
            MarkdownLifecycleAutomationBridge.ConfigureLaunchOptions(false, null);
            Environment.SetEnvironmentVariable(variable, previousPath);
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [EventSource(Name = "MarkdownRenderer.Svg.Resvg.Worker")]
    private sealed class TestWorkerEventSource : EventSource
    {
        public static readonly TestWorkerEventSource Log = new();

        [Event(1, Level = EventLevel.Warning)]
        public void Timeout(
            int stage,
            int deadlineMilliseconds,
            int workerProcessCpuMilliseconds,
            int transportPhase,
            int requestWriteMilliseconds,
            int workerExited,
            int openProgressPhase,
            int workerWorkingSetKiB,
            int workerPrivateCommitKiB,
            int workerPageFaults,
            int elapsedWallMilliseconds = 2_987,
            string workerInputSha256 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            string workerExecutableSha256 = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb") =>
            WriteEvent(1, [stage, deadlineMilliseconds, workerProcessCpuMilliseconds,
                transportPhase, requestWriteMilliseconds, workerExited,
                openProgressPhase, workerWorkingSetKiB, workerPrivateCommitKiB,
                workerPageFaults, elapsedWallMilliseconds, workerInputSha256,
                workerExecutableSha256]);
    }

    [EventSource(Name = "MarkdownRenderer.Svg.Resvg.Preflight")]
    private sealed class TestPreflightEventSource : EventSource
    {
        public static readonly TestPreflightEventSource Log = new();

        [Event(1, Level = EventLevel.Warning)]
        public void Rejected(string reason, int sourceByteLength, string sourceSha256) =>
            WriteEvent(1, reason, sourceByteLength, sourceSha256);
    }

    [EventSource(Name = "MarkdownRenderer-RasterPreparation")]
    private sealed class TestRasterPreparationEventSource : EventSource
    {
        public static readonly TestRasterPreparationEventSource Log = new();

        [Event(1, Level = EventLevel.Informational)]
        public void Started(long id, long sourceBytes, long packedDimensions) =>
            WriteEvent(1, id, sourceBytes, packedDimensions);

        [Event(2, Level = EventLevel.Informational)]
        public void Stage(long id, long stage, long elapsedStopwatchTicks) =>
            WriteEvent(2, id, stage, elapsedStopwatchTicks);
    }
}
