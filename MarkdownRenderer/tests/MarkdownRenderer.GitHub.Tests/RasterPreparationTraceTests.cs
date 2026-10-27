using System.Diagnostics.Tracing;
using MarkdownRenderer.Performance;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

public sealed class RasterPreparationTraceTests
{
    [Fact]
    public void OptionalSessionEmitsNumericRasterStages()
    {
        using var listener = new RasterListener();
        using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        var trace = (IMarkdownPerformanceSessionInternal)session;

        long id = trace.BeginRasterPreparation(8_948, 120, 85);
        Assert.True(id > 0);
        trace.RecordRasterPreparationStage(id, MarkdownRasterPreparationStage.PreparationAdmission, 12_345);

        Assert.Contains(listener.Events, item =>
            item.EventId == 1 && item.Id == id && item.Value == 8_948 &&
            item.Auxiliary == ((120L << 32) | 85));
        Assert.Contains(listener.Events, item =>
            item.EventId == 2 && item.Id == id && item.Value == 7 &&
            item.Auxiliary == 12_345);
    }

    private sealed class RasterListener : EventListener
    {
        private readonly List<(int EventId, long Id, long Value, long Auxiliary)> _events = [];

        internal (int EventId, long Id, long Value, long Auxiliary)[] Events
        {
            get
            {
                lock (_events)
                    return [.. _events];
            }
        }

        protected override void OnEventSourceCreated(EventSource source)
        {
            if (source.Name == "MarkdownRenderer-RasterPreparation")
                EnableEvents(source, EventLevel.Informational);
        }

        protected override void OnEventWritten(EventWrittenEventArgs eventData)
        {
            if (eventData.EventSource.Name != "MarkdownRenderer-RasterPreparation" ||
                eventData.Payload is not { Count: 3 } payload ||
                payload[0] is not long id || payload[1] is not long value ||
                payload[2] is not long auxiliary)
                return;

            lock (_events)
                _events.Add((eventData.EventId, id, value, auxiliary));
        }
    }
}
