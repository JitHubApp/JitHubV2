using Microsoft.UI.Xaml;
using Windows.Foundation;

namespace MarkdownRenderer.Layout.Internal.Relayout;

/// <summary>
/// Per-root rollback state captured by a changed-block relayout.
/// </summary>
internal struct ChangedBlockMeasure
{
    internal ChangedBlockMeasure(
        int ordinal,
        bool isMeasured,
        Rect previousBounds,
        Thickness previousMargin,
        bool sharedFlowWithPrevious,
        bool sharedFlowWithNext)
    {
        Ordinal = ordinal;
        IsMeasured = isMeasured;
        PreviousBounds = previousBounds;
        PreviousMargin = previousMargin;
        SharedFlowWithPrevious = sharedFlowWithPrevious;
        SharedFlowWithNext = sharedFlowWithNext;
        MeasureAttempted = false;
        Height = 0;
    }

    internal readonly int Ordinal;
    internal readonly bool IsMeasured;
    internal readonly Rect PreviousBounds;
    internal readonly Thickness PreviousMargin;
    internal readonly bool SharedFlowWithPrevious;
    internal readonly bool SharedFlowWithNext;
    internal bool MeasureAttempted;
    internal float Height;
}
