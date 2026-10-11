using System.Diagnostics;
using System.Text.Json;
using JitHub.Services.Markdown;
using Xunit;

namespace JitHub.WinUI.Tests.Services;

[Collection("Markdown renderer audit environment")]
public sealed class SvgWorkerStageEvidenceTests
{
    [Fact]
    public void WorkerSubstagesAreOptInAndPersistNumericCorrelationMetadata()
    {
        const string evidenceVariable = "JITHUB_MARKDOWN_SVG_STAGE_EVIDENCE_PATH";
        const string fixtureVariable = "JITHUB_MARKDOWN_LIFECYCLE_FIXTURE";
        string path = Path.Combine(Path.GetTempPath(), $"jithub-svg-worker-stage-{Guid.NewGuid():N}.ndjson");
        long workId = Random.Shared.NextInt64(1, long.MaxValue);
        string? previousPath = Environment.GetEnvironmentVariable(evidenceVariable);
        string? previousFixture = Environment.GetEnvironmentVariable(fixtureVariable);
        try
        {
            Environment.SetEnvironmentVariable(evidenceVariable, path);
            MarkdownLifecycleAutomationBridge.ConfigureLaunchOptions(false, null);
            MarkdownLifecycleAutomationBridge.RecordSvgWorkerStage(
                workId,
                stageId: 109,
                workerInstanceId: 3,
                requestId: 41,
                documentId: 9,
                elapsedStopwatchTicks: Stopwatch.Frequency / 4,
                workerCpuTicks: 12_500,
                sourceBytes: 563,
                targetPixels: 1_880);
            Assert.False(File.Exists(path));

            MarkdownLifecycleAutomationBridge.ConfigureLaunchOptions(true, null);
            MarkdownLifecycleAutomationBridge.RecordSvgWorkerStage(
                workId,
                stageId: 109,
                workerInstanceId: 3,
                requestId: 41,
                documentId: 9,
                elapsedStopwatchTicks: Stopwatch.Frequency / 4,
                workerCpuTicks: 12_500,
                sourceBytes: 563,
                targetPixels: 1_880);

            string line = Assert.Single(ReadCorrelatedSvgWorkerStageRows(path, workId, 3, 41, 9));
            using JsonDocument document = JsonDocument.Parse(line);
            JsonElement signal = document.RootElement;
            Assert.Equal(13, signal.GetProperty("EventId").GetInt32());
            Assert.Equal(workId, signal.GetProperty("WorkId").GetInt64());
            Assert.Equal(109, signal.GetProperty("StageId").GetInt32());
            Assert.Equal(3, signal.GetProperty("WorkerInstanceId").GetInt64());
            Assert.Equal(41, signal.GetProperty("RequestId").GetInt64());
            Assert.Equal(9, signal.GetProperty("DocumentId").GetInt64());
            Assert.Equal(563, signal.GetProperty("SourceBytes").GetInt64());
            Assert.Equal(1_880, signal.GetProperty("TargetPixels").GetInt64());
            Assert.Equal(250, signal.GetProperty("ElapsedMilliseconds").GetDouble());
            Assert.Equal(1.25, signal.GetProperty("WorkerCpuMilliseconds").GetDouble());
            Assert.False(signal.TryGetProperty("Source", out _));
            Assert.False(signal.TryGetProperty("Url", out _));
            Assert.False(signal.TryGetProperty("Hash", out _));

#if MARKDOWN_LAYOUT_DIAGNOSTICS
            MarkdownLifecycleAutomationBridge.RecordSvgWorkerStage(
                workId,
                stageId: 116,
                workerInstanceId: 3,
                requestId: 41,
                documentId: 9,
                elapsedStopwatchTicks: Stopwatch.Frequency / 10,
                workerCpuTicks: 25_000,
                sourceBytes: 0,
                targetPixels: 0);
            Assert.Single(ReadCorrelatedSvgWorkerStageRows(path, workId, 3, 41, 9));

            MarkdownLifecycleAutomationBridge.RecordSvgWorkerStage(
                workId,
                stageId: 117,
                workerInstanceId: 3,
                requestId: 41,
                documentId: 9,
                elapsedStopwatchTicks: Stopwatch.Frequency / 10,
                workerCpuTicks: -1,
                sourceBytes: 0,
                targetPixels: 0);
            string[] afterPipeStage = ReadCorrelatedSvgWorkerStageRows(path, workId, 3, 41, 9);
            Assert.Equal(2, afterPipeStage.Length);
            string pipeStageLine = Assert.Single(afterPipeStage, line =>
            {
                using JsonDocument stageDocument = JsonDocument.Parse(line);
                return stageDocument.RootElement.GetProperty("StageId").GetInt32() == 117;
            });
            using (JsonDocument pipeStageDocument = JsonDocument.Parse(pipeStageLine))
            {
                JsonElement pipeStage = pipeStageDocument.RootElement;
                Assert.Equal(13, pipeStage.GetProperty("EventId").GetInt32());
                Assert.Equal(Environment.ProcessId, pipeStage.GetProperty("ProcessId").GetInt32());
                Assert.Equal(workId, pipeStage.GetProperty("WorkId").GetInt64());
                Assert.Equal(117, pipeStage.GetProperty("StageId").GetInt32());
                Assert.Equal(3, pipeStage.GetProperty("WorkerInstanceId").GetInt64());
                Assert.Equal(41, pipeStage.GetProperty("RequestId").GetInt64());
                Assert.Equal(9, pipeStage.GetProperty("DocumentId").GetInt64());
                Assert.Equal(Stopwatch.Frequency / 10, pipeStage.GetProperty("ElapsedStopwatchTicks").GetInt64());
                Assert.Equal(-1, pipeStage.GetProperty("WorkerCpuTicks").GetInt64());
                Assert.Equal(0, pipeStage.GetProperty("SourceBytes").GetInt64());
                Assert.Equal(0, pipeStage.GetProperty("TargetPixels").GetInt64());
                Assert.InRange(pipeStage.GetProperty("ElapsedMilliseconds").GetDouble(), 99, 101);
                Assert.Equal(-1d, pipeStage.GetProperty("WorkerCpuMilliseconds").GetDouble());
                Assert.Equal(JsonValueKind.String, pipeStage.GetProperty("Timestamp").ValueKind);
                Assert.False(pipeStage.TryGetProperty("Source", out _));
                Assert.False(pipeStage.TryGetProperty("Url", out _));
                Assert.False(pipeStage.TryGetProperty("Hash", out _));
            }

            MarkdownLifecycleAutomationBridge.RecordSvgWorkerStage(
                workId,
                stageId: 116,
                workerInstanceId: 3,
                requestId: 41,
                documentId: 9,
                elapsedStopwatchTicks: Stopwatch.Frequency / 10,
                workerCpuTicks: -1,
                sourceBytes: 1,
                targetPixels: 0);
            Assert.Equal(2, ReadCorrelatedSvgWorkerStageRows(path, workId, 3, 41, 9).Length);

            MarkdownLifecycleAutomationBridge.RecordSvgWorkerStage(
                workId,
                stageId: 121,
                workerInstanceId: 3,
                requestId: 41,
                documentId: 9,
                elapsedStopwatchTicks: Stopwatch.Frequency / 10,
                workerCpuTicks: -1,
                sourceBytes: 0,
                targetPixels: 0);
            Assert.Equal(2, ReadCorrelatedSvgWorkerStageRows(path, workId, 3, 41, 9).Length);

            MarkdownLifecycleAutomationBridge.FlushDeferredSvgStageEvidence();
            string[] flushedLines = ReadCorrelatedSvgWorkerStageRows(path, workId, 3, 41, 9);
            Assert.Equal(3, flushedLines.Length);
            string timingLine = Assert.Single(flushedLines, line =>
            {
                using JsonDocument stageDocument = JsonDocument.Parse(line);
                return stageDocument.RootElement.GetProperty("StageId").GetInt32() == 116;
            });
            using (JsonDocument timingDocument = JsonDocument.Parse(timingLine))
            {
                JsonElement timingSignal = timingDocument.RootElement;
                Assert.Equal(13, timingSignal.GetProperty("EventId").GetInt32());
                Assert.Equal(116, timingSignal.GetProperty("StageId").GetInt32());
                Assert.Equal(workId, timingSignal.GetProperty("WorkId").GetInt64());
                Assert.Equal(Environment.ProcessId, timingSignal.GetProperty("ProcessId").GetInt32());
                Assert.Equal(3, timingSignal.GetProperty("WorkerInstanceId").GetInt64());
                Assert.Equal(41, timingSignal.GetProperty("RequestId").GetInt64());
                Assert.Equal(9, timingSignal.GetProperty("DocumentId").GetInt64());
                Assert.Equal(0, timingSignal.GetProperty("SourceBytes").GetInt64());
                Assert.Equal(0, timingSignal.GetProperty("TargetPixels").GetInt64());
                Assert.Equal(Stopwatch.Frequency / 10, timingSignal.GetProperty("ElapsedStopwatchTicks").GetInt64());
                Assert.Equal(25_000, timingSignal.GetProperty("WorkerCpuTicks").GetInt64());
                Assert.InRange(timingSignal.GetProperty("ElapsedMilliseconds").GetDouble(), 99, 101);
                Assert.Equal(2.5, timingSignal.GetProperty("WorkerCpuMilliseconds").GetDouble());
                Assert.True(timingSignal.GetProperty("StopwatchTicks").GetInt64() > 0);
                Assert.Equal(Stopwatch.Frequency, timingSignal.GetProperty("StopwatchFrequency").GetInt64());
                Assert.Equal(JsonValueKind.String, timingSignal.GetProperty("Timestamp").ValueKind);
                Assert.False(timingSignal.TryGetProperty("Source", out _));
                Assert.False(timingSignal.TryGetProperty("Url", out _));
                Assert.False(timingSignal.TryGetProperty("Hash", out _));
            }

            MarkdownLifecycleAutomationBridge.FlushDeferredSvgStageEvidence();
            Assert.Equal(3, ReadCorrelatedSvgWorkerStageRows(path, workId, 3, 41, 9).Length);
#else
            MarkdownLifecycleAutomationBridge.RecordSvgWorkerStage(
                workId,
                stageId: 110,
                workerInstanceId: 3,
                requestId: 41,
                documentId: 9,
                elapsedStopwatchTicks: Stopwatch.Frequency / 4,
                workerCpuTicks: -1,
                sourceBytes: 563,
                targetPixels: 1_880);
            Assert.Single(ReadCorrelatedSvgWorkerStageRows(path, workId, 3, 41, 9));
#endif
        }
        finally
        {
            MarkdownLifecycleAutomationBridge.ConfigureLaunchOptions(false, null);
            Environment.SetEnvironmentVariable(evidenceVariable, previousPath);
            Environment.SetEnvironmentVariable(fixtureVariable, previousFixture);
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    private static string[] ReadCorrelatedSvgWorkerStageRows(
        string path,
        long workId,
        long workerInstanceId,
        long requestId,
        long documentId) =>
        File.ReadAllLines(path)
            .Where(line =>
            {
                using JsonDocument document = JsonDocument.Parse(line);
                JsonElement signal = document.RootElement;
                return signal.GetProperty("ProcessId").GetInt32() == Environment.ProcessId &&
                    signal.GetProperty("EventId").GetInt32() == 13 &&
                    signal.GetProperty("WorkId").GetInt64() == workId &&
                    signal.GetProperty("WorkerInstanceId").GetInt64() == workerInstanceId &&
                    signal.GetProperty("RequestId").GetInt64() == requestId &&
                    signal.GetProperty("DocumentId").GetInt64() == documentId;
            })
            .ToArray();

#if MARKDOWN_LAYOUT_DIAGNOSTICS
    [Fact]
    public void FontCatalogPhaseEvidenceIsBoundedAndFlushesOnlyOnce()
    {
        const string evidenceVariable = "JITHUB_MARKDOWN_SVG_STAGE_EVIDENCE_PATH";
        string path = Path.Combine(Path.GetTempPath(), $"jithub-svg-worker-phases-{Guid.NewGuid():N}.ndjson");
        string? previousPath = Environment.GetEnvironmentVariable(evidenceVariable);
        try
        {
            Environment.SetEnvironmentVariable(evidenceVariable, path);
            MarkdownLifecycleAutomationBridge.ConfigureLaunchOptions(true, null);
            for (int index = 0; index < 70; index++)
            {
                MarkdownLifecycleAutomationBridge.RecordSvgWorkerStage(
                    workId: 9000 + index,
                    stageId: 110,
                    workerInstanceId: 3,
                    requestId: index + 1,
                    documentId: 9,
                    elapsedStopwatchTicks: index,
                    workerCpuTicks: -1,
                    sourceBytes: 0,
                    targetPixels: 0);
            }

            Assert.False(File.Exists(path));
            MarkdownLifecycleAutomationBridge.FlushDeferredSvgStageEvidence();
            string[] lines = File.ReadAllLines(path);
            Assert.Equal(64, lines.Length);
            foreach (string line in lines)
            {
                using JsonDocument document = JsonDocument.Parse(line);
                JsonElement signal = document.RootElement;
                Assert.Equal(13, signal.GetProperty("EventId").GetInt32());
                Assert.Equal(110, signal.GetProperty("StageId").GetInt32());
                Assert.Equal(0, signal.GetProperty("SourceBytes").GetInt64());
                Assert.Equal(0, signal.GetProperty("TargetPixels").GetInt64());
                Assert.True(signal.GetProperty("StopwatchTicks").GetInt64() > 0);
                Assert.Equal(Stopwatch.Frequency, signal.GetProperty("StopwatchFrequency").GetInt64());
            }

            MarkdownLifecycleAutomationBridge.RecordSvgWorkerStage(
                workId: 9999,
                stageId: 116,
                workerInstanceId: 3,
                requestId: 99,
                documentId: 9,
                elapsedStopwatchTicks: 1,
                workerCpuTicks: 1,
                sourceBytes: 0,
                targetPixels: 0);
            MarkdownLifecycleAutomationBridge.FlushDeferredSvgStageEvidence();
            Assert.Equal(64, File.ReadAllLines(path).Length);
        }
        finally
        {
            MarkdownLifecycleAutomationBridge.ConfigureLaunchOptions(false, null);
            Environment.SetEnvironmentVariable(evidenceVariable, previousPath);
            if (File.Exists(path))
                File.Delete(path);
        }
    }
#endif
}
