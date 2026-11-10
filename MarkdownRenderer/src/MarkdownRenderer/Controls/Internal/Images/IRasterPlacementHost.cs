using System.Threading;
using MarkdownRenderer.Images;
using MarkdownRenderer.Images.Svg;
using MarkdownRenderer.Layout.Internal.Context;
using MarkdownRenderer.Layout.Internal.Snapshots;
using Windows.Foundation;

namespace MarkdownRenderer.Controls.Internal.Images;

/// <summary>What the raster placement publisher needs from the control.</summary>
internal interface IRasterPlacementHost
{
    /// <summary>
    /// True while device recovery, a rebuild or lazy layout is pending; a
    /// private placement must not compete with any of them.
    /// </summary>
    bool HasCompetingLayoutWork { get; }

    /// <summary>The width layout runs at, in DIPs.</summary>
    float LayoutWidth { get; }

    /// <summary>The owner token stamped on every image preparation this control starts.</summary>
    object ImagePreparationOwner { get; }

    /// <summary>Cancels image work when the loaded image lifetime ends.</summary>
    CancellationToken ImageLifetime { get; }

    /// <summary>True while the scroll viewer is in an intermediate (inertial or animated) view.</summary>
    bool IsScrollViewIntermediate { get; }

    /// <summary>
    /// True when <paramref name="context"/> was built from the control's
    /// current theme, session, image and SVG inputs, flow direction and scale.
    /// </summary>
    bool MatchesLayoutInputs(MarkdownLayoutContext context);

    void RetireSnapshot(LayoutSnapshot snapshot);

    /// <summary>Forgets the hovered target and the abbreviation tooltip's target.</summary>
    void ForgetHoverTarget();

    void ScheduleVisibleCodeBlockHighlighting();

    void NotifyAutomationSnapshotChanged();

    void HideAbbreviationTooltip();

    void ResetCursor();

    void ApplySnapshotSize(Size size);

    void RefreshInteractiveTextAdornerAfterLayoutChange();

    void InvalidateAutomationLayout();

    void InvalidateCanvas();

    void RaiseImageUnavailable(string url, MarkdownImageUnavailableReason reason, MarkdownSvgFailureReason? svgReason);
}
