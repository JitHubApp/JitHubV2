namespace MarkdownRenderer.Diagnostics.Internal;

/// <summary>
/// Typed, allocation-free observer used by the in-process pinned-machine
/// harness. External profilers continue to consume the EventSource contract;
/// this sink avoids letting EventListener payload allocation perturb a 4 KiB
/// per-frame gate.
/// </summary>
internal interface IMarkdownPerformanceSink
{
    void BitmapCacheCleanupFailed(int hresult);
    void PipelineStarted(long generation, long sourceUtf16Bytes);
    void FirstViewportPresented(long generation, long sourceUtf16Bytes, long elapsedStopwatchTicks);
    void ScrollWork(long logicalFrameId, long elapsedStopwatchTicks, long allocatedBytes);
    void PaintWork(long logicalFrameId, long elapsedStopwatchTicks, long allocatedBytes);
    void PipelineSuperseded(long generation);
    void SupersededPipelineStopped(long generation, long elapsedStopwatchTicks);
    void ScrollAllocationBreakdown(
        long logicalFrameId,
        long lazyLayoutBytes,
        long adornerBytes,
        long realizationBytes,
        long highlightingBytes);
    void PaintAllocationBreakdown(
        long logicalFrameId,
        long schedulingBytes,
        long platformSessionBytes,
        long snapshotPaintBytes,
        long interactivePaintBytes);
    void SnapshotPaintAllocationBreakdown(
        long logicalFrameId,
        long inlineBytes,
        long tableBytes,
        long codeBytes,
        long otherBytes);
    void CodeActionRealization(long logicalFrameId, long realizedCount, long offscreenCount);
}
