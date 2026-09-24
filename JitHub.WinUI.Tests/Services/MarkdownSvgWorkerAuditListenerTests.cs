using System.Diagnostics.Tracing;
using System.Text.Json;
using JitHub.Services.Markdown;
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
                stage: 3,
                deadlineMilliseconds: 3_000,
                workerProcessCpuMilliseconds: 1_250);

            string line = Assert.Single(File.ReadAllLines(path));
            using JsonDocument document = JsonDocument.Parse(line);
            Assert.Equal("render", document.RootElement.GetProperty("Phase").GetString());
            Assert.Equal(3_000, document.RootElement.GetProperty("DeadlineMilliseconds").GetInt32());
            Assert.Equal(1_250, document.RootElement.GetProperty("WorkerProcessCpuMilliseconds").GetInt32());
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
        public void Timeout(int stage, int deadlineMilliseconds, int workerProcessCpuMilliseconds) =>
            WriteEvent(1, stage, deadlineMilliseconds, workerProcessCpuMilliseconds);
    }
}
