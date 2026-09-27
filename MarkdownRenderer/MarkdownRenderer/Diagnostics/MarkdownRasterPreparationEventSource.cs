using System.Diagnostics.Tracing;
using System.Threading;
using MarkdownRenderer.Performance;

namespace MarkdownRenderer.Diagnostics;

/// <summary>
/// Audit-only raster preparation timings. This separate provider lets the
/// production README audit observe image decoding without enabling the much
/// higher-volume frame and pipeline performance events.
/// </summary>
[EventSource(Name = "MarkdownRenderer-RasterPreparation")]
internal sealed class MarkdownRasterPreparationEventSource : EventSource
{
    internal static MarkdownRasterPreparationEventSource Log { get; } = new();

    private static long _nextPreparationId;

    private MarkdownRasterPreparationEventSource()
    {
    }

    [NonEvent]
    internal long Begin(int sourceBytes, int sourceWidth, int sourceHeight)
    {
        if (!IsEnabled(EventLevel.Informational, EventKeywords.None))
            return 0;

        long id = Interlocked.Increment(ref _nextPreparationId);
        // The dimensions are packed into one value to keep the ETW event's
        // payload fixed, small, and free of URLs or document content.
        Started(id, sourceBytes, ((long)sourceWidth << 32) | (uint)sourceHeight);
        return id;
    }

    [NonEvent]
    internal void RecordStage(
        long id,
        MarkdownRasterPreparationStage stage,
        long elapsedStopwatchTicks)
    {
        if (id != 0)
            Stage(id, (long)stage, elapsedStopwatchTicks);
    }

    [Event(1, Level = EventLevel.Informational)]
    internal void Started(long id, long sourceBytes, long packedDimensions) =>
        WriteEvent(1, id, sourceBytes, packedDimensions);

    /// <summary>
    /// Stage IDs: 1 stream write, 2 decoder creation, 3 WIC pixel decode,
    /// 4 Win2D bitmap upload, 5 direct Win2D load, 6 cache publication,
    /// 7 preparation admission and continuation scheduling.
    /// </summary>
    [Event(2, Level = EventLevel.Informational)]
    internal void Stage(long id, long stage, long elapsedStopwatchTicks) =>
        WriteEvent(2, id, stage, elapsedStopwatchTicks);
}
