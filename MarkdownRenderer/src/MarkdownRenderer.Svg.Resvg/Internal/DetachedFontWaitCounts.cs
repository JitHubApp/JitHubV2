namespace MarkdownRenderer.Svg.Resvg.Internal;

/// <summary>The detached font-wait counters as one value, prepared before it is published.</summary>
/// <param name="Count">Admitted requests parked off-slot.</param>
/// <param name="VisibleRenderCount">Parked visible renders.</param>
/// <param name="VisibleTextRenderCount">Parked visible text renders.</param>
internal readonly record struct DetachedFontWaitCounts(int Count, int VisibleRenderCount, int VisibleTextRenderCount);
