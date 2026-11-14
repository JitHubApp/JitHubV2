using Windows.Foundation;
using MarkdownRenderer.Layout.Internal.Snapshots;

namespace MarkdownRenderer.Controls.Internal.Pipeline.Commit;

/// <summary>What layout commit publication needs from the control.</summary>
internal interface ILayoutCommitHost
{
    /// <summary>True while the scroll viewer is in an intermediate (inertial or animated) view.</summary>
    bool IsScrollViewIntermediate { get; }

    bool RequestGeometryRecoveryIfRequired(LayoutSnapshot? snapshot);

    void RequestFullRebuild();

    void ApplySnapshotSize(Size size);

    void ScheduleVisibleCodeBlockHighlighting();

    void RefreshInteractiveTextAdornerAfterLayoutChange();

    void InvalidateAutomationLayout();

    void InvalidateCanvas();

    void TryResumePendingScrollToBlock(LayoutSnapshot snapshot, long snapshotGeneration);
}
