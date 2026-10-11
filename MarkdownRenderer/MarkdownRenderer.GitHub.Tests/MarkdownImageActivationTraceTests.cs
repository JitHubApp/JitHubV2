#if MARKDOWN_LAYOUT_DIAGNOSTICS
using System.Diagnostics.Tracing;
using MarkdownRenderer.Diagnostics;
using MarkdownRenderer.Images;
using MarkdownRenderer.Performance;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

public sealed class MarkdownImageActivationTraceTests
{
    [Fact]
    public void StaticallyRootedRecorderReceivesOnlyNumericActivationMetadata()
    {
        List<(long Generation, int ImageOrdinal, int StageId, long StopwatchTicks,
            long StopwatchFrequency, int ViewportMeasured, int IntersectsViewport,
            long ViewportWidthSixtyFourthDips, long ViewportHeightSixtyFourthDips)> observed = [];
        MarkdownPerformanceEventSource.SetImageActivationTraceDiagnosticRecorder(
            (generation, imageOrdinal, stageId, stopwatchTicks, stopwatchFrequency,
                viewportMeasured, intersectsViewport, viewportWidth, viewportHeight) =>
                observed.Add((generation, imageOrdinal, stageId, stopwatchTicks, stopwatchFrequency,
                    viewportMeasured, intersectsViewport, viewportWidth, viewportHeight)));
        try
        {
            MarkdownPerformanceEventSource.Log.RecordImageActivationStage(
                generation: 42,
                imageOrdinal: 7,
                stageId: MarkdownPerformanceEventSource.ImageActivationStageHostResolverEntered,
                viewportMeasured: 1,
                intersectsViewport: 1,
                viewportWidthSixtyFourthDips: 40_960,
                viewportHeightSixtyFourthDips: 36_864);

            var stage = Assert.Single(observed);
            Assert.Equal(42, stage.Generation);
            Assert.Equal(7, stage.ImageOrdinal);
            Assert.Equal(MarkdownPerformanceEventSource.ImageActivationStageHostResolverEntered, stage.StageId);
            Assert.True(stage.StopwatchTicks > 0);
            Assert.True(stage.StopwatchFrequency > 0);
            Assert.Equal(1, stage.ViewportMeasured);
            Assert.Equal(1, stage.IntersectsViewport);
            Assert.Equal(40_960, stage.ViewportWidthSixtyFourthDips);
            Assert.Equal(36_864, stage.ViewportHeightSixtyFourthDips);
        }
        finally
        {
            MarkdownPerformanceEventSource.SetImageActivationTraceDiagnosticRecorder(null);
        }
    }

    [Fact]
    public void EventSourcePayloadIsNumericAndCarriesTheGenerationAndStage()
    {
        using var listener = new ImageActivationListener();
        const long generation = 91_734;
        MarkdownPerformanceEventSource.Log.ImageActivationStage(
            generation,
            imageOrdinal: 3,
            stageId: MarkdownPerformanceEventSource.ImageActivationStageSchedulerEntered,
            stopwatchTicks: 123_456,
            stopwatchFrequency: 10_000_000,
            viewportMeasured: 0,
            intersectsViewport: 1,
            viewportWidthSixtyFourthDips: 64,
            viewportHeightSixtyFourthDips: 128);

        var observed = Assert.Single(listener.Events, item => item.Generation == generation);
        Assert.Equal(15, observed.EventId);
        Assert.Equal(9, observed.Payload.Length);
        Assert.Equal(generation, Assert.IsType<long>(observed.Payload[0]));
        Assert.Equal(3, Assert.IsType<int>(observed.Payload[1]));
        Assert.Equal(MarkdownPerformanceEventSource.ImageActivationStageSchedulerEntered,
            Assert.IsType<int>(observed.Payload[2]));
        Assert.Equal(123_456, Assert.IsType<long>(observed.Payload[3]));
        Assert.Equal(10_000_000, Assert.IsType<long>(observed.Payload[4]));
        Assert.DoesNotContain(observed.Payload, static value => value is string);
    }

    [Fact]
    public async Task CorrelatedImageResolutionRecordsDocumentSlotAndHostBoundaries()
    {
        List<(long Generation, int ImageOrdinal, int StageId, long Ticks)> stages = [];
        MarkdownPerformanceEventSource.SetImageActivationTraceDiagnosticRecorder(
            (generation, imageOrdinal, stageId, stopwatchTicks, _, _, _, _, _) =>
            {
                if (generation == 712)
                    stages.Add((generation, imageOrdinal, stageId, stopwatchTicks));
            });
        try
        {
            var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
            await using (session.ConfigureAwait(false))
            {
                var resolver = new StaticImageResolver();
                var context = new MarkdownImageResolveContext(null);
                using IMarkdownPerformanceDocumentScope scope = session.OpenDocument(resolver, context);
                var correlation = new MarkdownImageActivationCorrelation(
                    Generation: 712,
                    ImageOrdinal: 4,
                    ViewportMeasured: 1,
                    IntersectsViewport: 1,
                    ViewportWidthSixtyFourthDips: 40_960,
                    ViewportHeightSixtyFourthDips: 36_864);

                MarkdownImageResolution resolution = await scope.ResolveWithActivationTraceAsync(
                    "image.png", context, correlation, CancellationToken.None);

                Assert.True(resolution.IsHandled);
                Assert.Equal(1, resolver.CallCount);
            }

            Assert.Equal(
                [
                    MarkdownPerformanceEventSource.ImageActivationStageDocumentScopeEntered,
                    MarkdownPerformanceEventSource.ImageActivationStageFetchSlotWaitStarted,
                    MarkdownPerformanceEventSource.ImageActivationStageFetchSlotAcquired,
                    MarkdownPerformanceEventSource.ImageActivationStageHostResolverEntered,
                ],
                stages.Select(static stage => stage.StageId));
            Assert.Equal(stages.OrderBy(static stage => stage.Ticks), stages);
            Assert.All(stages, stage =>
            {
                Assert.Equal(712, stage.Generation);
                Assert.Equal(4, stage.ImageOrdinal);
            });
        }
        finally
        {
            MarkdownPerformanceEventSource.SetImageActivationTraceDiagnosticRecorder(null);
        }
    }

    private sealed class StaticImageResolver : IMarkdownImageResolver
    {
        private int _callCount;
        internal int CallCount => Volatile.Read(ref _callCount);

        public ValueTask<MarkdownImageResolution> ResolveAsync(
            string source,
            MarkdownImageResolveContext context,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _callCount);
            return ValueTask.FromResult(MarkdownImageResolution.Resolved(
                new MarkdownImageAsset([1, 2, 3], "image/png", CacheKey: source)));
        }
    }

    private sealed class ImageActivationListener : EventListener
    {
        private readonly List<(long Generation, int EventId, object?[] Payload)> _events = [];

        internal (long Generation, int EventId, object?[] Payload)[] Events
        {
            get
            {
                lock (_events)
                    return [.. _events];
            }
        }

        protected override void OnEventSourceCreated(EventSource source)
        {
            if (source.Name == MarkdownPerformanceEventSource.ProviderName)
                EnableEvents(source, EventLevel.Informational);
        }

        protected override void OnEventWritten(EventWrittenEventArgs eventData)
        {
            if (eventData.EventSource.Name != MarkdownPerformanceEventSource.ProviderName ||
                eventData.EventId != 15 ||
                eventData.Payload is not { Count: 9 } payload ||
                payload[0] is not long generation)
            {
                return;
            }

            lock (_events)
                _events.Add((generation, eventData.EventId, payload.ToArray()));
        }
    }
}
#endif
