using System;
using MarkdownRenderer.Controls.Internal.Input;
using MarkdownRenderer.Controls.Internal.Selection;
using MarkdownRenderer.Controls.Internal.View;
using MarkdownRenderer.Selection;
using MarkdownRenderer.Selection.Internal;
using Microsoft.UI.Xaml;
using Windows.Foundation;

namespace MarkdownRenderer.Controls.Internal.Rendering;

/// <summary>
/// Sizes the canvas, its root and the overlay to a committed snapshot.
/// </summary>
internal sealed class SnapshotExtent
{
    private readonly FrameworkElement _renderer;
    private readonly MarkdownViewSurface _surface;
    private readonly SelectionDragShield _dragShield;
    private readonly DocumentPointerInteraction _document;

    internal SnapshotExtent(
        FrameworkElement renderer,
        MarkdownViewSurface surface,
        SelectionDragShield dragShield,
        DocumentPointerInteraction document)
    {
        _renderer = renderer;
        _surface = surface;
        _dragShield = dragShield;
        _document = document;
    }

    internal void Apply(Size committedSize)
    {
        if (_surface.Canvas is null || _surface.Root is null || _surface.Overlay is null) return;
        _surface.Canvas.Width = Math.Max(1, committedSize.Width);
        // Round the height up to whole device pixels so layout rounding never
        // clips the document's last partial pixel row. Width stays
        // as committed: rounding it up could add a horizontal scroll range.
        _surface.Canvas.Height = Math.Max(1, SelectionController.SnapExtentUpToPixelGrid(
            committedSize.Height,
            _renderer.XamlRoot?.RasterizationScale ?? 1.0));
        _surface.Root.Width = _surface.Canvas.Width;
        _surface.Root.Height = _surface.Canvas.Height;
        _surface.Overlay.Width = _surface.Canvas.Width;
        _surface.Overlay.Height = _surface.Canvas.Height;
        _dragShield.SetActive(_document.IsDragSelecting);
    }
}
