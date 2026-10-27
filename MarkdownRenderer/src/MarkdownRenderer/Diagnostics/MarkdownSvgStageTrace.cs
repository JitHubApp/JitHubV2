#if MARKDOWN_LAYOUT_DIAGNOSTICS
using System;
using System.Diagnostics;
using System.Threading;
using MarkdownRenderer.Diagnostics;

namespace MarkdownRenderer.Layout.Boxes;

/// <summary>
/// Numeric SVG work stages emitted only by diagnostic builds. The event
/// contract contains no source identity or user-authored metadata.
/// </summary>
internal enum MarkdownSvgWorkStage
{
    SourcePreparation = 1,
    HostPreflight = 2,
    HostAdmissionWait = 3,
    ProviderOpen = 4,
    BitmapCacheWait = 5,
    ProviderRender = 6,
    BitmapUpload = 7,
    BitmapCacheStore = 8,
    UiDispatchDelay = 9,
    UiPublish = 10,
    SvgBitmapBufferCopy = 11,
    SvgCanvasBitmapCreateFromBytes = 12,
    SvgCacheWeightSizeQuery = 13,
    SvgCacheEntryPixelSizeQuery = 14,
    SvgCacheLruInsertAndEviction = 15,
    OuterDispatchQueueWait = 16,
}

/// <summary>
/// Allocation-free span for an SVG stage. This type and all call sites are
/// removed from shipping builds unless MARKDOWN_LAYOUT_DIAGNOSTICS is enabled.
/// </summary>
internal readonly struct MarkdownSvgStageTrace : IDisposable
{
    private static readonly AsyncLocal<long> AmbientWorkId = new();

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

    internal static long CurrentWorkId => AmbientWorkId.Value;

    internal static void SetCurrentWorkId(long workId) => AmbientWorkId.Value = workId;

    internal static MarkdownSvgWorkScope BeginWorkScope()
    {
        long workId = BeginWork();
        return BeginWorkScope(workId);
    }

    internal static MarkdownSvgWorkScope BeginWorkScope(long workId)
    {
        if (workId == 0)
            return default;

        long previousWorkId = AmbientWorkId.Value;
        AmbientWorkId.Value = workId;
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

/// <summary>
/// Restores the prior ambient SVG work ID after async provider and worker
/// operations complete. AsyncLocal carries the ID across Task.Run/await without
/// adding content identity to provider requests or the worker protocol.
/// </summary>
internal readonly struct MarkdownSvgWorkScope : IDisposable
{
    private readonly long _previousWorkId;
    private readonly long _workId;

    internal MarkdownSvgWorkScope(long previousWorkId, long workId)
    {
        _previousWorkId = previousWorkId;
        _workId = workId;
    }

    internal long WorkId => _workId;

    public void Dispose()
    {
        if (_workId != 0 && MarkdownSvgStageTrace.CurrentWorkId == _workId)
            MarkdownSvgStageTrace.SetCurrentWorkId(_previousWorkId);
    }
}
#endif
