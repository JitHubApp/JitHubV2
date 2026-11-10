using System;
using System.Collections.Generic;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Windows.UI;
using MarkdownRenderer.Document;
using MarkdownRenderer.Layout.Internal.Images.Svg;
using MarkdownRenderer.Theming;

namespace MarkdownRenderer.Layout.Internal.Images;

/// <summary>
/// Paints an image box's artwork, caption and selection: the presented whole
/// bitmap, the SVG tile grid, or the placeholder, and the selection tint,
/// outline and endpoint geometry of its single atomic source slot.
/// </summary>
/// <remarks>
/// Paint reads the geometry the last measure committed, so paint always
/// matches layout. It never measures, and it starts loading only through the
/// box's viewport-aware <see cref="ImageBox.EnsureLoading()"/>.
/// </remarks>
internal sealed class ImagePainter
{
    // Selected artwork keeps the same translucent tint as Math and
    // Mermaid scenes instead of an opaque block-wide band.
    private const float SelectionArtworkBackgroundOpacity = 0.25f;

    private readonly ImageBox _box;
    private readonly ImageMeasureCustody _measure;
    private readonly ImagePresentation _presentation;
    private readonly SvgImageState _svg;
    private readonly ImagePlaceholderLayout _placeholder;

    internal ImagePainter(
        ImageBox box,
        ImageMeasureCustody measure,
        ImagePresentation presentation,
        SvgImageState svg,
        ImagePlaceholderLayout placeholder)
    {
        _box = box;
        _measure = measure;
        _presentation = presentation;
        _svg = svg;
        _placeholder = placeholder;
    }

    private Rect Bounds => _box.Bounds;

    /// <summary>Where the artwork is painted, in document coordinates.</summary>
    internal Rect AutomationRenderDestination => _measure.IsInlineLayout
        ? Bounds
        : new Rect(GetContentX(), GetContentY(), _measure.ImageWidth, _measure.ImageHeight);

    /// <summary>Paints the block image and its caption.</summary>
    internal void Paint(CanvasDrawingSession ds, Rect viewport)
    {
        float x = GetContentX();
        float y = GetContentY();

        // Use cached image dimensions from Measure() so paint matches layout.
        var imageRect = new Rect(x, y, _measure.ImageWidth, _measure.ImageHeight);
        PaintImageContent(ds, imageRect, viewport);

        if (_measure.Caption is not null)
        {
            var cs = _box.Context.ThemeSnapshot.GetStyle(MarkdownElementKeys.ImageCaption);
            PlacementAwareTextDrawing.DrawAtRootLocalOrigin(
                _box.Context.PlacementTransform, ds, _measure.Caption, x, GetCaptionY(cs), cs.Foreground);
        }
    }

    /// <summary>Paints this image into an inline cell.</summary>
    internal void PaintInline(CanvasDrawingSession ds, Rect rect, Rect viewport)
    {
        if (rect.Right < viewport.Left || rect.Left > viewport.Right ||
            rect.Bottom < viewport.Top || rect.Top > viewport.Bottom)
        {
            return;
        }

        // Inline images can be nested several containers deep in raw HTML. Painting is the
        // final viewport-aware fallback if a host's lazy-image plan did not discover one.
        _box.EnsureLoading();

        PaintImageContent(ds, rect, viewport);
    }

    /// <summary>Paints a selected inline image and its selection outline.</summary>
    internal void PaintInlineSelectionForeground(CanvasDrawingSession ds, Rect rect, Rect viewport, Color selectionForeground)
    {
        if (rect.Right < viewport.Left || rect.Left > viewport.Right ||
            rect.Bottom < viewport.Top || rect.Top > viewport.Bottom)
        {
            return;
        }

        if (_placeholder.ShouldCollapseFailedDecorativeImage)
        {
            // Keep the image's single source/selection character, but a failed
            // dimensionless decorative image has no visual cell to outline.
            _box.EnsureLoading();
            return;
        }

        NativePaintSessionResources.LayerLease selectionLayer =
            _box.Context.RenderResources.PaintSessions.CreateLayer(ds, 0.82f, rect);
        try
        {
            PaintInline(ds, rect, viewport);
        }
        catch (Exception primary)
        {
            selectionLayer.ClosePreservingPrimary(primary);
            throw;
        }
        selectionLayer.Close();

        ds.DrawRoundedRectangle(rect, 3, 3, selectionForeground, 1.5f);
    }

    /// <summary>Paints a selected block image, its outline and its caption in the selection color.</summary>
    internal void PaintSelectionForeground(CanvasDrawingSession ds, DocumentRange range, Color color, Rect viewport)
    {
        if (!SelectionIntersectsAtomicSlot(range))
            return;

        if (Bounds.Right < viewport.Left || Bounds.Left > viewport.Right ||
            Bounds.Bottom < viewport.Top || Bounds.Top > viewport.Bottom)
        {
            return;
        }

        float x = GetContentX();
        float y = GetContentY();
        var imageRect = new Rect(x, y, _measure.ImageWidth, _measure.ImageHeight);

        if (imageRect.Width > 0 && imageRect.Height > 0)
        {
            NativePaintSessionResources.LayerLease selectionLayer =
                _box.Context.RenderResources.PaintSessions.CreateLayer(ds, 0.82f, imageRect);
            try
            {
                PaintImageContent(ds, imageRect, viewport);
            }
            catch (Exception primary)
            {
                selectionLayer.ClosePreservingPrimary(primary);
                throw;
            }
            selectionLayer.Close();

            ds.DrawRoundedRectangle(imageRect, 3, 3, color, 2f);
        }

        if (_measure.Caption is not null)
        {
            var cs = _box.Context.ThemeSnapshot.GetStyle(MarkdownElementKeys.ImageCaption);
            PlacementAwareTextDrawing.DrawAtRootLocalOrigin(_box.Context.PlacementTransform, ds, _measure.Caption, x, GetCaptionY(cs), color);
        }
    }

    /// <summary>
    /// Returns paint-only selection geometry: a translucent tint over the
    /// painted artwork and an opaque band behind the caption text, which the
    /// selection foreground repaints in the selection text color. The full
    /// block bounds remain authoritative for endpoints and hit tests.
    /// </summary>
    internal IEnumerable<SelectionBackgroundGeometry> GetSelectionBackgroundGeometry(DocumentRange range)
    {
        if (!SelectionIntersectsAtomicSlot(range))
            yield break;

        // An inline image's arranged bounds are its painted content.
        if (_measure.IsInlineLayout)
        {
            yield return new SelectionBackgroundGeometry(Bounds, 1f);
            yield break;
        }

        float x = GetContentX();
        float y = GetContentY();
        if (_measure.ImageWidth > 0 && _measure.ImageHeight > 0)
        {
            yield return new SelectionBackgroundGeometry(
                new Rect(x, y, _measure.ImageWidth, _measure.ImageHeight),
                SelectionArtworkBackgroundOpacity);
        }

        if (_measure.Caption is { } caption)
        {
            Rect layout = caption.LayoutBounds;
            if (layout.Width > 0 && layout.Height > 0)
            {
                var cs = _box.Context.ThemeSnapshot.GetStyle(MarkdownElementKeys.ImageCaption);
                float cy = GetCaptionY(cs);
                yield return new SelectionBackgroundGeometry(
                    new Rect(x + layout.X, cy + layout.Y, layout.Width, layout.Height),
                    1f);
            }
        }
    }

    /// <summary>True when <paramref name="range"/> covers the image's single source slot.</summary>
    internal bool SelectionIntersectsAtomicSlot(DocumentRange range)
    {
        int blockIndex = _box.BlockIndex;
        DocumentRange normalized = range.Normalized();
        if (normalized.IsEmpty ||
            normalized.End.BlockIndex < blockIndex ||
            normalized.Start.BlockIndex > blockIndex)
        {
            return false;
        }

        var before = new DocumentPosition(blockIndex, 0, 0);
        var after = new DocumentPosition(blockIndex, 0, 1);
        return normalized.Start < after && normalized.End > before;
    }

    /// <summary>
    /// A promoted Markdown image occupies one atomic source slot. While an
    /// existing selection is being extended, points on its visible artwork
    /// use the artwork's halves for the before/after boundary. Bounds also
    /// includes the wrapped alt caption and outer margins; using its center
    /// can put the after boundary below the entire painted image. Caption and
    /// margin points keep the full-block fallback, and ordinary hit testing
    /// stays start-only so touching an image does not change tap behavior.
    /// </summary>
    internal bool HitTestSelectionEndpoint(Point point, out DocumentPosition position)
    {
        if (!Bounds.Contains(point))
        {
            position = DocumentPosition.Zero;
            return false;
        }

        Rect artwork = AutomationRenderDestination;
        double midpointTop = artwork.Contains(point) ? artwork.Top : Bounds.Top;
        double midpointHeight = artwork.Contains(point) ? artwork.Height : Bounds.Height;
        bool afterMidpoint = point.Y >= midpointTop + midpointHeight / 2.0;
        position = new DocumentPosition(_box.BlockIndex, 0, afterMidpoint ? 1 : 0);
        return true;
    }

    /// <summary>Paints the presented whole bitmap at its intended scale.</summary>
    internal void PaintWholeBitmap(CanvasDrawingSession ds, CanvasBitmap bitmap, Rect destination)
    {
        if (_presentation.PhysicalViewport is { } physical)
        {
            // The provider has already rasterized the fractional viewport's
            // edge coverage. A fractional source rectangle would clip that
            // coverage a second time. Keep the complete pixel allocation at
            // its intended scale, including its partially covered last pixel.
            destination.Width *= bitmap.SizeInPixels.Width / physical.Width;
            destination.Height *= bitmap.SizeInPixels.Height / physical.Height;
        }
        ds.DrawImage(bitmap, destination);
    }

    private void PaintImageContent(CanvasDrawingSession ds, Rect destination, Rect viewport)
    {
        if (_placeholder.ShouldCollapseFailedDecorativeImage)
            return;

        if (!_svg.UsesTiles)
        {
            if (_presentation.Bitmap is { } bitmap)
                PaintWholeBitmap(ds, bitmap, destination);
            else
                _placeholder.PaintPlaceholder(ds, destination);
            return;
        }

        if (_svg.BitmapWidthPixels <= 0 ||
            _svg.BitmapHeightPixels <= 0 ||
            destination.Width <= 0 ||
            destination.Height <= 0)
        {
            return;
        }

        _box.SvgTileCache.PaintTiles(ds, _box.Context.RenderResources.PaintSessions, destination, viewport);
        _box.SvgTileRenderer.QueueVisibleSvgTiles(destination, viewport);
    }

    private float GetCaptionY(ElementStyle captionStyle) =>
        (float)(Bounds.Y + _box.Margin.Top) +
        Math.Max(_measure.ImageHeight, _box.ParagraphLineHeight) + (float)captionStyle.Margin.Top;

    private float GetContentX()
    {
        Thickness margin = _box.Margin;
        float left = (float)(Bounds.X + margin.Left);
        float available = Math.Max(0, (float)Bounds.Width - (float)(margin.Left + margin.Right));
        return _box.ContentAlignment switch
        {
            CanvasHorizontalAlignment.Center => left + Math.Max(0, (available - _measure.ImageWidth) / 2f),
            CanvasHorizontalAlignment.Right => left + Math.Max(0, available - _measure.ImageWidth),
            _ => left,
        };
    }

    private float GetContentY()
        => (float)(Bounds.Y + _box.Margin.Top) +
           Math.Max(0f, (_box.ParagraphLineHeight - _measure.ImageHeight) / 2f);
}
