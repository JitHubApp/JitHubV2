#if MARKDOWN_LAYOUT_DIAGNOSTICS
using System.Diagnostics;
using System.Text.Json;
using JitHub.Services.Markdown;
using Xunit;

namespace JitHub.WinUI.Tests.Services;

[Collection("Markdown renderer audit environment")]
public sealed class FirstVisibleImageActivationEvidenceTests
{
    private const string EvidenceVariable = "JITHUB_MARKDOWN_SVG_STAGE_EVIDENCE_PATH";
    private const string FixtureVariable = "JITHUB_MARKDOWN_LIFECYCLE_FIXTURE";

    [Fact]
    public void ActivationTraceBuffersThenAppendsOnceWithoutReplacingSvgStages()
    {
        string path = Path.Combine(Path.GetTempPath(), $"jithub-image-activation-{Guid.NewGuid():N}.ndjson");
        string? previousPath = Environment.GetEnvironmentVariable(EvidenceVariable);
        string? previousFixture = Environment.GetEnvironmentVariable(FixtureVariable);
        try
        {
            Environment.SetEnvironmentVariable(EvidenceVariable, path);
            Environment.SetEnvironmentVariable(FixtureVariable, null);
            File.WriteAllText(path, "{\"EventId\":12}" + Environment.NewLine);

            MarkdownLifecycleAutomationBridge.ConfigureLaunchOptions(false, null);
            MarkdownLifecycleAutomationBridge.RecordImageActivationTrace(
                generation: 1,
                imageOrdinal: 2,
                stageId: 6,
                stopwatchTicks: 100,
                stopwatchFrequency: Stopwatch.Frequency,
                viewportMeasured: 1,
                intersectsViewport: 1,
                viewportWidthSixtyFourthDips: 64_000,
                viewportHeightSixtyFourthDips: 57_600);
            MarkdownLifecycleAutomationBridge.FlushDeferredSvgStageEvidence();
            Assert.Single(File.ReadAllLines(path));

            MarkdownLifecycleAutomationBridge.ConfigureLaunchOptions(true, null);
            MarkdownLifecycleAutomationBridge.RecordImageActivationTrace(
                generation: 17,
                imageOrdinal: 4,
                stageId: 3,
                stopwatchTicks: 200,
                stopwatchFrequency: Stopwatch.Frequency,
                viewportMeasured: 0,
                intersectsViewport: 1,
                viewportWidthSixtyFourthDips: 64,
                viewportHeightSixtyFourthDips: 128);
            foreach (int stageId in new[] { 7, 12, 13, 15 })
            {
                MarkdownLifecycleAutomationBridge.RecordImageActivationTrace(
                    generation: 17,
                    imageOrdinal: 4,
                    stageId,
                    stopwatchTicks: 200 + stageId,
                    stopwatchFrequency: Stopwatch.Frequency,
                    viewportMeasured: 0,
                    intersectsViewport: 1,
                    viewportWidthSixtyFourthDips: 64,
                    viewportHeightSixtyFourthDips: 128);
            }

            // Recording during the measured path is memory-only.
            Assert.Single(File.ReadAllLines(path));

            MarkdownLifecycleAutomationBridge.FlushDeferredSvgStageEvidence();
            string[] lines = File.ReadAllLines(path);
            Assert.Equal(6, lines.Length);
            using JsonDocument document = JsonDocument.Parse(lines[1]);
            JsonElement signal = document.RootElement;
            Assert.Equal(15, signal.GetProperty("EventId").GetInt32());
            Assert.Equal(17, signal.GetProperty("Generation").GetInt64());
            Assert.Equal(4, signal.GetProperty("ImageOrdinal").GetInt32());
            Assert.Equal(3, signal.GetProperty("StageId").GetInt32());
            Assert.Equal(200, signal.GetProperty("StopwatchTicks").GetInt64());
            Assert.Equal(Stopwatch.Frequency, signal.GetProperty("StopwatchFrequency").GetInt64());
            Assert.Equal(0, signal.GetProperty("ViewportMeasured").GetInt32());
            Assert.Equal(1, signal.GetProperty("IntersectsViewport").GetInt32());
            Assert.Equal(64, signal.GetProperty("ViewportWidthSixtyFourthDips").GetInt64());
            Assert.Equal(128, signal.GetProperty("ViewportHeightSixtyFourthDips").GetInt64());
            Assert.False(signal.TryGetProperty("Source", out _));
            Assert.False(signal.TryGetProperty("Url", out _));
            Assert.False(signal.TryGetProperty("Hash", out _));

            Assert.Equal(
                new[] { 3, 7, 12, 13, 15 },
                lines.Skip(1).Select(line =>
                {
                    using JsonDocument stagedDocument = JsonDocument.Parse(line);
                    return stagedDocument.RootElement.GetProperty("StageId").GetInt32();
                }));

            MarkdownLifecycleAutomationBridge.FlushDeferredSvgStageEvidence();
            Assert.Equal(6, File.ReadAllLines(path).Length);
        }
        finally
        {
            MarkdownLifecycleAutomationBridge.ConfigureLaunchOptions(false, null);
            Environment.SetEnvironmentVariable(EvidenceVariable, previousPath);
            Environment.SetEnvironmentVariable(FixtureVariable, previousFixture);
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public void ActivationTraceAcceptsStagesOneThroughFifteenAndCapsTheShutdownFlushAt64Records()
    {
        string path = Path.Combine(Path.GetTempPath(), $"jithub-image-activation-cap-{Guid.NewGuid():N}.ndjson");
        string? previousPath = Environment.GetEnvironmentVariable(EvidenceVariable);
        string? previousFixture = Environment.GetEnvironmentVariable(FixtureVariable);
        try
        {
            Environment.SetEnvironmentVariable(EvidenceVariable, path);
            Environment.SetEnvironmentVariable(FixtureVariable, null);
            MarkdownLifecycleAutomationBridge.ConfigureLaunchOptions(true, null);

            foreach (int invalidStage in new[] { 0, 16 })
            {
                MarkdownLifecycleAutomationBridge.RecordImageActivationTrace(
                    generation: 1,
                    imageOrdinal: 0,
                    stageId: invalidStage,
                    stopwatchTicks: 100,
                    stopwatchFrequency: Stopwatch.Frequency,
                    viewportMeasured: 1,
                    intersectsViewport: 1,
                    viewportWidthSixtyFourthDips: 64,
                    viewportHeightSixtyFourthDips: 128);
            }
            for (int index = 0; index < 70; index++)
            {
                MarkdownLifecycleAutomationBridge.RecordImageActivationTrace(
                    generation: 2,
                    imageOrdinal: index,
                    stageId: index % 15 + 1,
                    stopwatchTicks: 1_000 + index,
                    stopwatchFrequency: Stopwatch.Frequency,
                    viewportMeasured: index % 2,
                    intersectsViewport: 1,
                    viewportWidthSixtyFourthDips: 64,
                    viewportHeightSixtyFourthDips: 128);
            }

            Assert.False(File.Exists(path));
            MarkdownLifecycleAutomationBridge.FlushDeferredSvgStageEvidence();
            string[] lines = File.ReadAllLines(path);
            Assert.Equal(64, lines.Length);
            foreach (string line in lines)
            {
                using JsonDocument document = JsonDocument.Parse(line);
                JsonElement signal = document.RootElement;
                Assert.Equal(15, signal.GetProperty("EventId").GetInt32());
                Assert.InRange(signal.GetProperty("StageId").GetInt32(), 1, 15);
                Assert.DoesNotContain(signal.EnumerateObject(), static property =>
                    property.Value.ValueKind == JsonValueKind.String);
            }

            MarkdownLifecycleAutomationBridge.RecordImageActivationTrace(
                generation: 3,
                imageOrdinal: 0,
                stageId: 1,
                stopwatchTicks: 2_000,
                stopwatchFrequency: Stopwatch.Frequency,
                viewportMeasured: 1,
                intersectsViewport: 0,
                viewportWidthSixtyFourthDips: 64,
                viewportHeightSixtyFourthDips: 128);
            MarkdownLifecycleAutomationBridge.FlushDeferredSvgStageEvidence();
            Assert.Equal(64, File.ReadAllLines(path).Length);
        }
        finally
        {
            MarkdownLifecycleAutomationBridge.ConfigureLaunchOptions(false, null);
            Environment.SetEnvironmentVariable(EvidenceVariable, previousPath);
            Environment.SetEnvironmentVariable(FixtureVariable, previousFixture);
            if (File.Exists(path))
                File.Delete(path);
        }
    }
}
#endif
