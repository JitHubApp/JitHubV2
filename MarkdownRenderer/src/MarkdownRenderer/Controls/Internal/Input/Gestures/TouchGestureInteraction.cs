using System;
using MarkdownRenderer.Controls.Internal.Input.HorizontalOverflow;
using MarkdownRenderer.Controls.Internal.Input.Links;
using MarkdownRenderer.Controls.Internal.Pipeline;
using MarkdownRenderer.Controls.Internal.Selection.TouchSelection;
using MarkdownRenderer.Controls.Internal.View;
using MarkdownRenderer.Layout.Internal.Snapshots;
using MarkdownRenderer.Selection;
using MarkdownRenderer.Selection.Internal;
using Microsoft.UI.Xaml;
using Windows.Foundation;

namespace MarkdownRenderer.Controls.Internal.Input.Gestures;

/// <summary>
/// The gestures WinUI recognizes on the document canvas: tap, double-tap,
/// hold and right-tap. They select words for touch and pen, open the
/// selection's context menu, and record the activation of a tapped link.
/// </summary>
/// <remarks>
/// A gesture that opens the menu or activates a link only records that
/// intent on the input; the renderer runs it after the snapshot lease ends.
/// </remarks>
internal sealed class TouchGestureInteraction
{
    private readonly IPointerInteractionHost _host;
    private readonly FrameworkElement _renderer;
    private readonly MarkdownViewSurface _surface;
    private readonly PublishedLayout _published;
    private readonly MarkdownViewport _viewport;
    private readonly SelectionController _selection;
    private readonly MarkdownTouchSelectionSession _touchSelection;
    private readonly TouchTapSuppressionPolicy _touchTapSuppression;
    private readonly HorizontalOverflowPointerDrag _overflowDrag;
    private readonly DocumentPointerInteraction _document;

    internal TouchGestureInteraction(
        IPointerInteractionHost host,
        FrameworkElement renderer,
        MarkdownViewSurface surface,
        PublishedLayout published,
        MarkdownViewport viewport,
        SelectionController selection,
        MarkdownTouchSelectionSession touchSelection,
        TouchTapSuppressionPolicy touchTapSuppression,
        HorizontalOverflowPointerDrag overflowDrag,
        DocumentPointerInteraction document)
    {
        _host = host;
        _renderer = renderer;
        _surface = surface;
        _published = published;
        _viewport = viewport;
        _selection = selection;
        _touchSelection = touchSelection;
        _touchTapSuppression = touchTapSuppression;
        _overflowDrag = overflowDrag;
        _document = document;
    }

    /// <summary>
    /// A touch tap opens the context menu inside the selection, clears the
    /// selection elsewhere, and activates a tapped link.
    /// </summary>
    internal void ProcessTapped(ref PointerInput e)
    {
        if (e.InteractionSnapshot is not { } snapshot || _surface.Canvas is null ||
            e.GestureDevice != Microsoft.UI.Input.PointerDeviceType.Touch)
        {
            return;
        }

        Point point = e.GesturePosition;
        bool suppressTap = _touchTapSuppression.ShouldSuppressTap(
            e.TouchTapGestureEpoch,
            Environment.TickCount64,
            point);
        _touchTapSuppression.CompleteTap(e.TouchTapGestureEpoch);
        if (suppressTap)
        {
            e.Handled = true;
            return;
        }

        if (_host.IsPointOverEmbed(point) || _host.IsPointOverCodeBlockAction(point))
            return;

        if (_selection.IsActive && IsPointInsideSelection(snapshot, point))
        {
            e.ShowContextMenuAfterInteraction = true;
            e.TouchSelectionContextMenu = true;
            e.Handled = true;
            return;
        }

        if (_selection.IsActive)
            e.ClearSelectionAfterInteraction = true;

        // Tapped is raised only after WinUI's gesture recognizer has ruled out
        // manipulation. Unlike PointerPressed, this lets an ancestor
        // ScrollViewer retain vertical-pan ownership without a speculative
        // renderer capture.
        _host.RememberFocusResumePoint(point);
        if (LinkTargets.TryCreateActivationIntent(
            snapshot,
            point,
            MarkdownLinkInputKind.Touch,
            e.LinkModifiers,
            out LinkActivationIntent intent))
        {
            e.SetLinkActivation(intent);
            e.Handled = true;
        }
    }

    /// <summary>A touch double-tap selects the word under it.</summary>
    internal void ProcessDoubleTapped(ref PointerInput e)
    {
        if (_published.Snapshot is not { } snapshot || _surface.Canvas is null ||
            e.GestureDevice != Microsoft.UI.Input.PointerDeviceType.Touch)
        {
            return;
        }
        long snapshotGeneration = _published.Generation;
        long layoutRevision = snapshot.LayoutRevision;

        Point point = e.GesturePosition;
        if (_host.IsPointOverEmbed(point) || _host.IsPointOverCodeBlockAction(point))
            return;

        _overflowDrag.Clear();
        e.ResumeRebuildAfterInteraction = true;
        _document.EndCapturedSessionForContextGesture(ref e);
        if (_host.IsCurrentScrollTarget(snapshot, snapshotGeneration, layoutRevision) &&
            _document.TrySelectWordForTouchContext(
                point,
                snapshot,
                snapshotGeneration,
                layoutRevision,
                e.HasGestureContact ? e.GestureContact : null))
        {
            SuppressTouchTap(point, e.TouchTapGestureEpoch);
            e.Handled = true;
        }
    }

    /// <summary>
    /// A touch or pen hold selects the word under it, or keeps the selection
    /// it lands inside so the release can open its commands.
    /// </summary>
    internal void ProcessHolding(ref PointerInput e)
    {
        if (_published.Snapshot is not { } snapshot || _surface.Canvas is null ||
            e.GestureDevice is not (
                Microsoft.UI.Input.PointerDeviceType.Touch or
                Microsoft.UI.Input.PointerDeviceType.Pen))
        {
            return;
        }
        long snapshotGeneration = _published.Generation;
        long layoutRevision = snapshot.LayoutRevision;

        Point point = e.GesturePosition;
        if (_host.IsPointOverEmbed(point) || _host.IsPointOverCodeBlockAction(point))
            return;

        // A stationary hold has established selection/context intent. Clear a
        // still-pending block-local pan and end any pen-selection capture: the
        // flyout and explicit selection handles own subsequent interaction.
        _overflowDrag.Clear();
        e.ResumeRebuildAfterInteraction = true;
        _document.EndCapturedSessionForContextGesture(ref e);

        // A hold inside a committed selection is a context gesture for
        // that selection, as a tap or context request inside it already is.
        // Keep the selection and its handles; the release opens its commands.
        if (_selection.IsActive && IsPointInsideSelection(snapshot, point))
        {
            if (_host.IsCurrentScrollTarget(snapshot, snapshotGeneration, layoutRevision))
            {
                _touchSelection.Select();
                _host.UpdateSelectionOverlay();
                SuppressTouchTap(point, e.TouchTapGestureEpoch);
                e.Handled = true;
            }
            return;
        }

        if (_host.IsCurrentScrollTarget(snapshot, snapshotGeneration, layoutRevision) &&
            _document.TrySelectWordForTouchContext(
                point,
                snapshot,
                snapshotGeneration,
                layoutRevision,
                e.HasGestureContact ? e.GestureContact : null))
        {
            SuppressTouchTap(point, e.TouchTapGestureEpoch);
            e.Handled = true;
        }
    }

    /// <summary>
    /// A right-tap opens the context menu. Touch and pen first select the
    /// word under the gesture unless it lands inside the selection.
    /// </summary>
    internal void ProcessRightTapped(ref PointerInput e)
    {
        if (_surface.Canvas is null || e.InteractionSnapshot is not { } snapshot) return;
        long snapshotGeneration = _published.Generation;
        long layoutRevision = snapshot.LayoutRevision;
        var pt = e.GesturePosition;
        if (_host.IsPointOverEmbed(pt) || _host.IsPointOverCodeBlockAction(pt))
            return;

        bool touchOrPen = e.GestureDevice is Microsoft.UI.Input.PointerDeviceType.Touch or
            Microsoft.UI.Input.PointerDeviceType.Pen;
        if (touchOrPen && (!_selection.IsActive || !IsPointInsideSelection(snapshot, pt)))
        {
            _ = _document.TrySelectWordForTouchContext(
                pt,
                snapshot,
                snapshotGeneration,
                layoutRevision,
                e.HasGestureContact ? e.GestureContact : null);
        }
        if (!_host.IsCurrentScrollTarget(snapshot, snapshotGeneration, layoutRevision))
            return;
        e.ShowContextMenuAfterInteraction = true;
        e.TouchSelectionContextMenu = touchOrPen;
        e.Handled = true;
    }

    private bool IsPointInsideSelection(LayoutSnapshot snapshot, Point point)
    {
        _viewport.TryGet(out double top, out double height, out double width);
        double scale = _renderer.XamlRoot?.RasterizationScale ?? 1.0;
        if (!double.IsFinite(scale) || scale <= 0)
            scale = 1.0;

        return _selection.ContainsPointUnderReadLease(
            snapshot,
            point,
            new Rect(0, top, width, height),
            scale);
    }

    private void SuppressTouchTap(Point point, long gestureEpoch)
    {
        _touchTapSuppression.SuppressTouchTap(
            gestureEpoch,
            point,
            Environment.TickCount64);
    }
}
