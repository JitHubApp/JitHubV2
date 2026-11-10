using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Layout.Internal.Images;
using MarkdownRenderer.Performance;

namespace MarkdownRenderer.Controls.Images;

/// <summary>What image load completion routing needs from the control.</summary>
internal interface IImageLoadCompletionHost
{
    IMarkdownPerformanceSession? PerformanceSession { get; }

    /// <summary>The width layout runs at, in DIPs.</summary>
    float LayoutWidth { get; }

    bool RequestGeometryRecoveryIfRequired(LayoutSnapshot? snapshot);

    /// <summary>Raises the image's UI Automation status change. Can run host code.</summary>
    void NotifyImageStatusChanged(ImageBox image);

    void RequestFullRebuild();

    /// <summary>Records a full rebuild whose cross-thread dispatch was rejected, for the next safe retry.</summary>
    void RecordRejectedCrossThreadFullRebuild();

    /// <summary>Invalidates the canvas and the selection adorner.</summary>
    void InvalidatePaint();
}
