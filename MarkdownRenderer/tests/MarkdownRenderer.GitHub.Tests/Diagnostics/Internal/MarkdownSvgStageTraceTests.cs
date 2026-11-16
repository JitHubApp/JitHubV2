using System.Diagnostics.Tracing;
using System.Linq;
using System.Threading;
using MarkdownRenderer.Diagnostics.Internal;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests.Diagnostics.Internal;

public sealed class MarkdownSvgStageTraceTests
{
    [Fact]
    public void StageTracePacksStageSourceBytesAndTargetPixels()
    {
        using var listener = new SvgStageListener();
        long workId = MarkdownSvgStageTrace.BeginWork();
        Assert.True(workId > 0);
        using (MarkdownSvgStageTrace.Measure(
            workId,
            MarkdownSvgWorkStage.ProviderOpen,
            sourceBytes: 1_024,
            targetPixels: 8_192))
        {
            Thread.SpinWait(1_000);
        }

        var stage = Assert.Single(listener.StagesFor(workId));
        Assert.Equal(workId, stage.WorkId);
        Assert.True(stage.ElapsedTicks >= 0);
        Assert.Equal((int)MarkdownSvgWorkStage.ProviderOpen, (int)((stage.PackedMetrics >> 49) & 0x1F));
        Assert.Equal(1_024, (stage.PackedMetrics >> 25) & 0x00FF_FFFFL);
        Assert.Equal(8_192, stage.PackedMetrics & 0x01FF_FFFFL);
    }

    [Fact]
    public void StageTraceEmitsCorrelatedNumericOnlyTiming()
    {
        using var listener = new SvgStageListener();
        long workId = MarkdownSvgStageTrace.BeginWork();
        Assert.True(workId > 0);

        using (MarkdownSvgStageTrace.Measure(
            workId,
            MarkdownSvgWorkStage.ProviderRender,
            sourceBytes: 731,
            targetPixels: 1024L * 768))
        {
            Thread.SpinWait(10_000);
        }

        var observed = Assert.Single(listener.Events, item =>
            item.EventId == 12 &&
            item.Payload.Length == 3 &&
            item.Payload[0] is long observedWorkId &&
            observedWorkId == workId);
        Assert.Equal(12, observed.EventId);
        Assert.Equal(3, observed.Payload.Length);
        Assert.Equal(workId, Assert.IsType<long>(observed.Payload[0]));
        Assert.True(Assert.IsType<long>(observed.Payload[1]) >= 0);
        long packedMetrics = Assert.IsType<long>(observed.Payload[2]);
        Assert.Equal((int)MarkdownSvgWorkStage.ProviderRender, (int)((packedMetrics >> 49) & 0x1F));
        Assert.Equal(731L, (packedMetrics >> 25) & 0x00FF_FFFFL);
        Assert.Equal(1024L * 768, packedMetrics & 0x01FF_FFFFL);
        Assert.DoesNotContain(observed.Payload, static value => value is string);
    }

    [Fact]
    public void FiveBitStageEncodingPreservesExistingIdsAndCarriesDispatchStage()
    {
        using var listener = new SvgStageListener();
        long workId = MarkdownSvgStageTrace.BeginWork();
        Assert.True(workId > 0);
        using (MarkdownSvgStageTrace.Measure(
            workId,
            MarkdownSvgWorkStage.ProviderOpen,
            sourceBytes: 777,
            targetPixels: 12_345))
        {
        }
        using (MarkdownSvgStageTrace.Measure(
            workId,
            MarkdownSvgWorkStage.OuterDispatchQueueWait,
            sourceBytes: 777,
            targetPixels: 12_345))
        {
        }

        var stages = listener.StagesFor(workId);
        Assert.Equal(2, stages.Length);
        Assert.Equal(workId, stages[0].WorkId);
        Assert.Equal(workId, stages[1].WorkId);
        Assert.Equal(4, (int)((stages[0].PackedMetrics >> 49) & 0x1F));
        Assert.Equal(16, (int)((stages[1].PackedMetrics >> 49) & 0x1F));
        Assert.All(stages, stage =>
        {
            Assert.Equal(777, (stage.PackedMetrics >> 25) & 0x00FF_FFFFL);
            Assert.Equal(12_345, stage.PackedMetrics & 0x01FF_FFFFL);
        });
    }

    private sealed class SvgStageListener : EventListener
    {
        private readonly List<(int EventId, object?[] Payload)> _events = [];

        internal (int EventId, object?[] Payload)[] Events
        {
            get
            {
                lock (_events)
                    return [.. _events];
            }
        }

        internal (long WorkId, long ElapsedTicks, long PackedMetrics)[] StagesFor(long workId) =>
            [.. Events
                .Where(item => item.Payload[0] is long observed && observed == workId)
                .Select(item => (
                    (long)item.Payload[0]!,
                    (long)item.Payload[1]!,
                    (long)item.Payload[2]!))];

        protected override void OnEventSourceCreated(EventSource source)
        {
            if (source.Name == "MarkdownRenderer-Performance")
                EnableEvents(source, EventLevel.Informational);
        }

        protected override void OnEventWritten(EventWrittenEventArgs eventData)
        {
            if (eventData.EventSource.Name != "MarkdownRenderer-Performance" ||
                eventData.EventId != 12 ||
                eventData.Payload is not { Count: 3 } payload)
            {
                return;
            }

            lock (_events)
                _events.Add((eventData.EventId, payload.ToArray()));
        }
    }
}
