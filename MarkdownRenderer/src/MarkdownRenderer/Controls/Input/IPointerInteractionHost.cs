using MarkdownRenderer.Document;
using MarkdownRenderer.Layout.Internal.Snapshots;
using Windows.Foundation;

namespace MarkdownRenderer.Controls.Input;

/// <summary>
/// The renderer services that <see cref="DocumentPointerInteraction"/> and
/// <see cref="TouchGestureInteraction"/> call while they turn pointer input
/// into selection, hover and link intent.
/// </summary>
internal interface IPointerInteractionHost
{
    /// <summary>True when the reader may select text.</summary>
    bool IsSelectionEnabled { get; }

    /// <summary>
    /// True while <paramref name="snapshot"/> with this generation and layout
    /// revision is still the published layout of a live renderer.
    /// </summary>
    bool IsCurrentScrollTarget(LayoutSnapshot snapshot, long snapshotGeneration, long layoutRevision);

    /// <summary>
    /// Drops the request to restore the touch handles after the next rebuild.
    /// A new press owns the selection's presentation.
    /// </summary>
    void CancelPendingTouchSelectionRestore();

    /// <summary>Records where keyboard navigation resumes after a pointer interaction.</summary>
    void RememberFocusResumePoint(Point documentPoint);

    /// <summary>Moves keyboard focus to the renderer for a pointer interaction.</summary>
    void FocusRendererForPointerInteraction();

    /// <summary>Clears the keyboard focus ring, since pointer and keyboard are separate modalities.</summary>
    void ClearKeyboardFocusItem();

    /// <summary>True when the canvas point is over a code block's action button.</summary>
    bool IsPointOverCodeBlockAction(Point point);

    /// <summary>True when the canvas point is over a hosted embed or a code block action.</summary>
    bool IsPointOverEmbed(Point point);

    /// <summary>
    /// Snaps a canvas point inside an inline or block embed to the embed's
    /// nearer edge, so a selection never ends inside it.
    /// </summary>
    bool TryHitTestEmbed(Point point, out DocumentPosition position);

    /// <summary>
    /// True when the canvas point is over a vector scene, reporting whether
    /// the item under it is linked and whether it is selectable.
    /// </summary>
    bool TryGetVectorPointerState(Point point, out bool linked, out bool selectable);

    /// <summary>Repaints the link and focus state drawn above the text.</summary>
    void InvalidateInteractiveTextAdorner();

    /// <summary>Refreshes the selection adorner and touch handles after the selection changed.</summary>
    void UpdateSelectionOverlay();

    /// <summary>Clears the selection and every interaction that depends on it.</summary>
    void ClearSelectionForExternalInteraction();

    /// <summary>Resumes a rebuild that waited for the pointer interaction to end.</summary>
    void ResumePendingRebuildAfterPointerInteraction();
}
