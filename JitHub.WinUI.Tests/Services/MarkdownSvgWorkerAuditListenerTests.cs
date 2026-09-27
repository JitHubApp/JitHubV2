using System.Diagnostics.Tracing;
using System.Security.Cryptography;
using System.Text.Json;
using JitHub.Services.Markdown;
using MarkdownRenderer.Images;
using Xunit;

namespace JitHub.WinUI.Tests.Services;

[CollectionDefinition("Markdown SVG worker audit environment", DisableParallelization = true)]
public sealed class MarkdownSvgWorkerAuditEnvironmentCollection
{
}

[Collection("Markdown SVG worker audit environment")]
public sealed class MarkdownSvgWorkerAuditListenerTests
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
            using var listener = new MarkdownSvgWorkerAuditListener();

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
            using var listener = new MarkdownSvgWorkerAuditListener();

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

            string line = Assert.Single(File.ReadAllLines(path));
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
            Assert.False(document.RootElement.TryGetProperty("Source", out _));
            Assert.False(document.RootElement.TryGetProperty("Url", out _));
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
            int workerPageFaults) =>
            WriteEvent(1, [stage, deadlineMilliseconds, workerProcessCpuMilliseconds,
                transportPhase, requestWriteMilliseconds, workerExited,
                openProgressPhase, workerWorkingSetKiB, workerPrivateCommitKiB,
                workerPageFaults]);
    }

    [EventSource(Name = "MarkdownRenderer.Svg.Resvg.Preflight")]
    private sealed class TestPreflightEventSource : EventSource
    {
        public static readonly TestPreflightEventSource Log = new();

        [Event(1, Level = EventLevel.Warning)]
        public void Rejected(string reason, int sourceByteLength, string sourceSha256) =>
            WriteEvent(1, reason, sourceByteLength, sourceSha256);
    }
}
