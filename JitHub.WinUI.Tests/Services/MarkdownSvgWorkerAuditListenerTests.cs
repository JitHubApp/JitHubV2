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

            TestWorkerEventSource.Log.Timeout(stage: 3, deadlineMilliseconds: 3_000);

            string line = Assert.Single(File.ReadAllLines(path));
            using JsonDocument document = JsonDocument.Parse(line);
            Assert.Equal("render", document.RootElement.GetProperty("Phase").GetString());
            Assert.Equal(3_000, document.RootElement.GetProperty("DeadlineMilliseconds").GetInt32());
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
        public void Timeout(int stage, int deadlineMilliseconds) =>
            WriteEvent(1, stage, deadlineMilliseconds);
    }
}
