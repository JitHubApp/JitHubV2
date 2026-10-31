#if MARKDOWN_LAYOUT_DIAGNOSTICS
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using MarkdownRenderer.Diagnostics;
using MarkdownRenderer.Svg.Resvg.Internal;
using Xunit;

namespace MarkdownRenderer.Svg.Resvg.Tests;

public sealed class SvgWorkerStageTraceTests
{
    [Fact]
    public async Task EmitsCorrelatedNumericWorkerStagesOnly()
    {
        using var listener = new WorkerStageListener();

        SvgWorkerStageTrace.Record(
            workId: 8721,
            SvgWorkerStageId.RenderTransaction,
            workerInstanceId: 3,
            requestId: 41,
            documentId: 9,
            startedAt: Stopwatch.GetTimestamp(),
            workerCpuTicks: 250,
            sourceBytes: 563,
            targetPixels: 1_880);

        WorkerStageEvent payload = await listener.NextAsync();
        Assert.Equal(13, payload.EventId);
        Assert.Equal(9, payload.Values.Length);
        Assert.Equal(8721L, payload.Values[0]);
        Assert.Equal(107, payload.Values[1]);
        Assert.Equal(3L, payload.Values[2]);
        Assert.Equal(41L, payload.Values[3]);
        Assert.Equal(9L, payload.Values[4]);
        Assert.True((long)payload.Values[5]! >= 0);
        Assert.Equal(250L, payload.Values[6]);
        Assert.Equal(563L, payload.Values[7]);
        Assert.Equal(1_880L, payload.Values[8]);
        Assert.DoesNotContain(payload.Names, static name =>
            name.Equals("source", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("url", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("hash", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("text", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ZeroWorkIdDoesNotEmitOrStartMeasurement()
    {
        using var listener = new WorkerStageListener();

        SvgWorkerStageTrace.Record(
            workId: 0,
            SvgWorkerStageId.RenderTransaction,
            workerInstanceId: 3,
            requestId: 41,
            documentId: 9,
            startedAt: Stopwatch.GetTimestamp(),
            workerCpuTicks: -1,
            sourceBytes: 563,
            targetPixels: 1_880);

        Assert.False(listener.TryRead(out _));
    }

    [Fact]
    public async Task EmitsNativeFontPhaseWallAndCpuTicksAsNumericStageEvidence()
    {
        using var listener = new WorkerStageListener();

        SvgWorkerStageTrace.RecordElapsed(
            workId: 8721,
            SvgWorkerStageId.FontCatalogTextPipelineWarmup,
            workerInstanceId: 3,
            requestId: 41,
            documentId: 9,
            elapsedStopwatchTicks: Stopwatch.Frequency / 2,
            workerCpuTicks: 125_000);

        WorkerStageEvent payload = await listener.NextAsync();
        Assert.Equal(13, payload.EventId);
        Assert.Equal(9, payload.Values.Length);
        Assert.Equal(8721L, payload.Values[0]);
        Assert.Equal(115, payload.Values[1]);
        Assert.Equal(3L, payload.Values[2]);
        Assert.Equal(41L, payload.Values[3]);
        Assert.Equal(9L, payload.Values[4]);
        Assert.Equal(Stopwatch.Frequency / 2, payload.Values[5]);
        Assert.Equal(125_000L, payload.Values[6]);
        Assert.Equal(0L, payload.Values[7]);
        Assert.Equal(0L, payload.Values[8]);
        Assert.DoesNotContain(payload.Names, static name =>
            name.Equals("path", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("font", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("source", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("url", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("text", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task EmitsPreflightDispatchQueueWaitAsNumericStageEvidence()
    {
        using var listener = new WorkerStageListener();

        SvgWorkerStageTrace.RecordElapsed(
            workId: 8721,
            SvgWorkerStageId.PreflightDispatchQueueWait,
            workerInstanceId: 0,
            requestId: 0,
            documentId: 0,
            elapsedStopwatchTicks: 0,
            workerCpuTicks: -1);

        WorkerStageEvent payload = await listener.NextAsync();
        Assert.Equal(13, payload.EventId);
        Assert.Equal(9, payload.Values.Length);
        Assert.Equal(8721L, payload.Values[0]);
        Assert.Equal(120, payload.Values[1]);
        Assert.Equal(0L, payload.Values[2]);
        Assert.Equal(0L, payload.Values[3]);
        Assert.Equal(0L, payload.Values[4]);
        Assert.Equal(0L, payload.Values[5]);
        Assert.Equal(-1L, payload.Values[6]);
        Assert.Equal(0L, payload.Values[7]);
        Assert.Equal(0L, payload.Values[8]);
        Assert.DoesNotContain(payload.Names, static name =>
            name.Equals("source", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("url", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("hash", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("text", StringComparison.OrdinalIgnoreCase));
    }

    private sealed class WorkerStageListener : EventListener
    {
        private readonly ConcurrentQueue<WorkerStageEvent> _events = new();
        private readonly SemaphoreSlim _available = new(0);

        protected override void OnEventSourceCreated(EventSource eventSource)
        {
            if (eventSource.Name == "MarkdownRenderer-Performance")
                EnableEvents(eventSource, EventLevel.Informational);
        }

        protected override void OnEventWritten(EventWrittenEventArgs eventData)
        {
            if (eventData.EventId != 13)
                return;

            _events.Enqueue(new WorkerStageEvent(
                eventData.EventId,
                eventData.Payload?.ToArray() ?? [],
                eventData.PayloadNames?.ToArray() ?? []));
            _available.Release();
        }

        internal async Task<WorkerStageEvent> NextAsync()
        {
            await _available.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(_events.TryDequeue(out WorkerStageEvent? payload));
            return payload!;
        }

        internal bool TryRead(out WorkerStageEvent? payload) => _events.TryDequeue(out payload);
    }

    private sealed record WorkerStageEvent(int EventId, object?[] Values, string[] Names);
}
#endif
