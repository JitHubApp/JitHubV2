#if MARKDOWN_LAYOUT_DIAGNOSTICS
namespace MarkdownRenderer.Diagnostics;

/// <summary>
/// Numeric-only correlation for the one visible image selected by the audit
/// activation trace. It intentionally carries no source identity or content.
/// </summary>
internal readonly record struct MarkdownImageActivationCorrelation(
    long Generation,
    int ImageOrdinal,
    int ViewportMeasured,
    int IntersectsViewport,
    long ViewportWidthSixtyFourthDips,
    long ViewportHeightSixtyFourthDips)
{
    internal void RecordStage(int stageId) =>
        MarkdownPerformanceEventSource.Log.RecordImageActivationStage(
            Generation,
            ImageOrdinal,
            stageId,
            ViewportMeasured,
            IntersectsViewport,
            ViewportWidthSixtyFourthDips,
            ViewportHeightSixtyFourthDips);
}
#endif
