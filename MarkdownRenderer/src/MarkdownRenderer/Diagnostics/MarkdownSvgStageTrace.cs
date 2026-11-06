using System;
using System.Diagnostics;

namespace MarkdownRenderer.Diagnostics;

/// <summary>
/// Allocation-free span for an SVG stage. A zero work id means no observer is
/// listening, and the span then reads no clock and emits nothing.
/// </summary>
internal readonly struct MarkdownSvgStageTrace : IDisposable
{
    private readonly long _workId;
    private readonly MarkdownSvgWorkStage _stage;
    private readonly long _startedAt;
    private readonly long _sourceBytes;
    private readonly long _targetPixels;

    private MarkdownSvgStageTrace(
        long workId,
        MarkdownSvgWorkStage stage,
        long startedAt,
        long sourceBytes,
        long targetPixels)
    {
        _workId = workId;
        _stage = stage;
        _startedAt = startedAt;
        _sourceBytes = sourceBytes;
        _targetPixels = targetPixels;
    }

    internal static long BeginWork() => MarkdownPerformanceEventSource.Log.BeginSvgWork();

    internal static long CurrentWorkId => MarkdownPerformanceEventSource.Log.GetCurrentSvgWorkId();

    internal static void SetCurrentWorkId(long workId) =>
        MarkdownPerformanceEventSource.Log.SetCurrentSvgWorkId(workId);

    internal static MarkdownSvgWorkScope BeginWorkScope()
    {
        long workId = BeginWork();
        return BeginWorkScope(workId);
    }

    internal static MarkdownSvgWorkScope BeginWorkScope(long workId)
    {
        if (workId == 0)
            return default;

        long previousWorkId = CurrentWorkId;
        SetCurrentWorkId(workId);
        return new MarkdownSvgWorkScope(previousWorkId, workId);
    }

    internal static MarkdownSvgStageTrace Measure(
        long workId,
        MarkdownSvgWorkStage stage,
        long sourceBytes,
        long targetPixels = 0)
    {
        long startedAt = workId != 0 && MarkdownPerformanceEventSource.Log.IsSvgWorkMeasurementEnabled()
            ? Stopwatch.GetTimestamp()
            : 0;
        return new MarkdownSvgStageTrace(workId, stage, startedAt, sourceBytes, targetPixels);
    }

    internal static MarkdownSvgStageTrace MeasureFrom(
        long workId,
        MarkdownSvgWorkStage stage,
        long startedAt,
        long sourceBytes,
        long targetPixels = 0)
    {
        return new MarkdownSvgStageTrace(
            workId,
            stage,
            workId != 0 && MarkdownPerformanceEventSource.Log.IsSvgWorkMeasurementEnabled() ? startedAt : 0,
            sourceBytes,
            targetPixels);
    }

    public void Dispose()
    {
        if (_startedAt == 0)
            return;

        long elapsedTicks = Math.Max(0, Stopwatch.GetTimestamp() - _startedAt);
        long packedMetrics =
            (((long)(int)_stage & 0x1F) << 49) |
            ((Math.Min(_sourceBytes, 0x00FF_FFFFL) & 0x00FF_FFFFL) << 25) |
            (Math.Min(_targetPixels, 0x01FF_FFFFL) & 0x01FF_FFFFL);
        MarkdownPerformanceEventSource.Log.SvgWorkStage(
            _workId,
            elapsedTicks,
            packedMetrics);
    }
}
