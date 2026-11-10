using System;
using System.Collections.Generic;
using Microsoft.Graphics.Canvas;
using Windows.Foundation;
using Windows.UI;
using MarkdownRenderer.Document;
using MarkdownRenderer.Layout.Internal.Boxes;
using MarkdownRenderer.Layout.Internal.RenderResources;
using MarkdownRenderer.Layout.Internal.Vectors.Drawing;

namespace MarkdownRenderer.Layout.Internal.Vectors;

/// <summary>
/// Selection behavior of a vector scene box, which is one atomic source-map
/// slot: drag endpoints, selection rectangles, the paint-only background
/// geometry and the full-opacity selected repaint.
/// </summary>
internal sealed class VectorSceneSelectionPainter
{
    private readonly VectorSceneBox _host;

    internal VectorSceneSelectionPainter(VectorSceneBox host) => _host = host;

    private const float NormalSelectionBackgroundOpacity = 0.25f;

    internal bool HitTestSelectionEndpoint(Point point, out DocumentPosition position)
    {
        if (!_host.Bounds.Contains(point))
        {
            position = DocumentPosition.Zero;
            return false;
        }

        // A vector scene is one atomic source-map slot. During an active drag,
        // its upper and lower halves represent the boundary before and after the
        // scene respectively. This makes a final diagram selectable without
        // requiring a non-existent following block, while regular HitTest keeps
        // non-selectable diagram background inert for taps and drag starts.
        bool afterMidpoint = point.Y >= _host.Bounds.Top + _host.Bounds.Height / 2.0;
        position = new DocumentPosition(_host.BlockIndex, 0, afterMidpoint ? 1 : 0);
        return true;
    }

    internal IEnumerable<Rect> GetSelectionRects(DocumentRange range)
    {
        if ((_host.Drawing.IsPrepared || _host.IsGraphDetached || _host.IsCapacityFallbackPaintable) &&
            SelectionIntersectsAtomicSlot(range))
            yield return _host.Bounds;
    }

    /// <summary>
    /// Returns paint-only selection geometry. The full block bounds remain
    /// authoritative for selection endpoints and point-in-selection tests;
    /// the adorner background follows only visible scene content.
    /// </summary>
    internal IEnumerable<SelectionBackgroundGeometry> GetSelectionBackgroundGeometry(
        DocumentRange range)
    {
        if ((!_host.Drawing.IsPrepared && !_host.IsGraphDetached && !_host.IsCapacityFallbackPaintable) ||
            !SelectionIntersectsAtomicSlot(range))
        {
            yield break;
        }

        Rect visibleContent = _host.VisibleContentBounds;
        if (visibleContent.Width > 0 && visibleContent.Height > 0)
        {
            yield return new SelectionBackgroundGeometry(
                visibleContent,
                NormalSelectionBackgroundOpacity);
        }
    }

    internal void PaintSelectionForeground(
        CanvasDrawingSession ds,
        DocumentRange range,
        Color color,
        Rect viewport)
    {
        bool detached = _host.IsGraphDetached;
        bool capacityFallback = !detached && _host.IsCapacityFallbackPaintable;
        if ((!_host.Drawing.IsPrepared && !detached && !capacityFallback) ||
            !SelectionIntersectsAtomicSlot(range) ||
            _host.Bounds.Right < viewport.Left || _host.Bounds.Left > viewport.Right ||
            _host.Bounds.Bottom < viewport.Top || _host.Bounds.Top > viewport.Bottom)
        {
            return;
        }

        Rect sceneViewport = _host.HorizontalViewportBounds;
        if (sceneViewport.Width <= 0 || sceneViewport.Height <= 0)
            return;

        // The normal selection adorner supplies a translucent fill clipped to
        // VisibleContentBounds. Repaint the native scene at full opacity so
        // opaque glyphs and authored colors remain crisp while transparent
        // equation areas retain the underlying document. High Contrast keeps
        // the system Highlight/HighlightText pair at full opacity.
        bool highContrast = _host.LayoutContext.ThemeSnapshot.IsHighContrast;
        Color fallbackForeground = highContrast ? color : _host.Style.Foreground;
        VectorScenePaintPalette? selectedPalette = highContrast
            ? new VectorScenePaintPalette(
                color,
                _host.LayoutContext.ThemeSnapshot.SelectionHighlightColor,
                color)
            : null;
        NativePaintSessionResources.LayerLease sceneLayer =
            _host.LayoutContext.RenderResources.PaintSessions.CreateLayer(ds, 1f, sceneViewport);
        try
        {
            if (_host.Drawing.IsPrepared)
                _host.PaintScene(ds, selectedPalette);
            else if (capacityFallback)
                _host.FallbackPainter.PaintCapacityFallback(ds, fallbackForeground, clipAlreadyApplied: true);
            else if (_host.HasDetachedFallback)
                _host.FallbackPainter.PaintDetachedFallback(ds, fallbackForeground);
        }
        catch (Exception primary)
        {
            sceneLayer.ClosePreservingPrimary(primary);
            throw;
        }
        sceneLayer.Close();

        if (_host.CanScrollHorizontally)
        {
            NativePaintSessionResources.LayerLease scrollbarLayer =
                _host.LayoutContext.RenderResources.PaintSessions.CreateLayer(ds, 0.82f, _host.HorizontalScrollTrackBounds);
            try
            {
                _host.PaintHorizontalScrollbar(ds);
            }
            catch (Exception primary)
            {
                scrollbarLayer.ClosePreservingPrimary(primary);
                throw;
            }
            scrollbarLayer.Close();
        }

        Rect outline = _host.VisibleContentBounds;
        if (outline.Width > 0 && outline.Height > 0)
        {
            float radius = Math.Max(0, _host.Style.CornerRadius);
            Color outlineColor = highContrast
                ? color
                : _host.LayoutContext.ThemeSnapshot.SelectionHighlightColor;
            ds.DrawRoundedRectangle(outline, radius, radius, outlineColor, 2f);
        }
    }

    private bool SelectionIntersectsAtomicSlot(DocumentRange range)
    {
        DocumentRange normalized = range.Normalized();
        if (normalized.IsEmpty ||
            normalized.End.BlockIndex < _host.BlockIndex ||
            normalized.Start.BlockIndex > _host.BlockIndex)
        {
            return false;
        }

        var before = new DocumentPosition(_host.BlockIndex, 0, 0);
        var after = new DocumentPosition(_host.BlockIndex, 0, 1);
        if (normalized.Start.BlockIndex < _host.BlockIndex)
            return normalized.End.BlockIndex > _host.BlockIndex || normalized.End != before;
        if (normalized.End.BlockIndex > _host.BlockIndex)
            return normalized.Start != after;

        // Vector semantic children use their semantic index as InlineIndex so
        // TextPattern can expose individual labels and nodes. Any non-empty
        // selection wholly inside this block therefore intersects the atomic
        // visual even when neither endpoint uses its source-map slot (0, 0..1).
        return true;
    }
}
