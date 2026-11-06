using System;
using Microsoft.Graphics.Canvas;
using Windows.Foundation;
using Windows.UI;
using MarkdownRenderer.CodeBlocks;
using MarkdownRenderer.Document;
using MarkdownRenderer.Theming;

namespace MarkdownRenderer.Layout.Boxes;

/// <summary>
/// Paints a code block: surface, header, visible chunks, horizontal scrollbar
/// and the selection foreground pass, delegating the gutter to
/// <see cref="CodeBlockGutterPainter"/>.
/// </summary>
internal sealed class CodeBlockPainter
{
    private readonly CodeBlockBox _host;

    internal CodeBlockPainter(CodeBlockBox host) => _host = host;

    internal void Paint(CanvasDrawingSession ds, Rect viewport)
    {
        var codeStyle = _host.GetCodeStyle();
        var headerStyle = _host.GetHeaderStyle();
        var languageStyle = _host.GetLanguageStyle();
        var outer = _host.OuterRect();
        if (outer.Width <= 0 || outer.Height <= 0)
            return;

        float radius = Math.Max(0, codeStyle.CornerRadius);
        if (codeStyle.Background is { } bg)
            ds.FillRoundedRectangle(outer, radius, radius, bg);

        if (_host.Context.CodeBlockChromeMode != CodeBlockChromeMode.PlainMarkdown)
            PaintEnhancedChrome(ds, outer, radius, codeStyle, headerStyle, languageStyle, viewport);
        _host.Gutter.DrawBodySurfaces(ds, outer, codeStyle, headerStyle, languageStyle, viewport);

        var clip = _host.CodeViewportRect(outer, codeStyle);
        // A CanvasActiveLayer is a comparatively expensive projected/native
        // allocation. The tile already clips vertically and measured chunks
        // remain inside the body when the code fits horizontally, so create a
        // local clip only for a genuinely overflowing no-wrap block.
        if (_host.MeasuredContentWidth > _host.BodyViewportWidth + 0.5f)
        {
            NativePaintSessionResources.LayerLease layer = _host.Context.RenderResources.PaintSessions.CreateLayer(ds, 1.0f, clip);
            try
            {
                PaintVisibleChunks(ds, viewport);
            }
            catch (Exception primary)
            {
                layer.ClosePreservingPrimary(primary);
                throw;
            }
            layer.Close();
        }
        else
        {
            PaintVisibleChunks(ds, viewport);
        }

        PaintHorizontalScrollbar(ds, codeStyle);

        if (codeStyle.BorderBrush is { } border && codeStyle.BorderThickness > 0)
            PaintBorder(ds, outer, radius, border, codeStyle.BorderThickness);
    }

    // Header background, header/body separator and the header text when the
    // header intersects the viewport.
    private void PaintEnhancedChrome(
        CanvasDrawingSession ds,
        Rect outer,
        float radius,
        ElementStyle codeStyle,
        ElementStyle headerStyle,
        ElementStyle languageStyle,
        Rect viewport)
    {
        if (headerStyle.Background is { } headerBg)
        {
            ds.FillRoundedRectangle(outer, radius, radius, headerBg);
            if (codeStyle.Background is { } bodyBg)
            {
                ds.FillRectangle(
                    new Rect(outer.X, outer.Y + _host.HeaderHeight, outer.Width, Math.Max(0, outer.Height - _host.HeaderHeight)),
                    bodyBg);
            }
        }

        var separator = codeStyle.BorderBrush ?? headerStyle.BorderBrush ?? languageStyle.Foreground;
        ds.DrawLine(
            (float)outer.Left,
            (float)(outer.Top + _host.HeaderHeight),
            (float)outer.Right,
            (float)(outer.Top + _host.HeaderHeight),
            _host.Context.ThemeSnapshot.IsHighContrast
                ? separator
                : CodeBlockBox.WithAlpha(separator, Math.Min(separator.A, (byte)0x60)),
            1f);

        if (outer.Left < viewport.Right && outer.Right > viewport.Left &&
            outer.Top < viewport.Bottom && outer.Top + _host.HeaderHeight > viewport.Top)
        {
            _host.DrawHeaderText(ds, outer, languageStyle);
        }
    }

    private static void PaintBorder(CanvasDrawingSession ds, Rect outer, float radius, Color border, float thickness)
    {
        float inset = thickness / 2f;
        ds.DrawRoundedRectangle(
            new Rect(
                outer.X + inset,
                outer.Y + inset,
                Math.Max(0, outer.Width - thickness),
                Math.Max(0, outer.Height - thickness)),
            radius,
            radius,
            border,
            thickness);
    }

    private void PaintVisibleChunks(CanvasDrawingSession ds, Rect viewport)
    {
        int first = _host.FindFirstVisibleChunk(viewport.Top);
        for (int index = first; index < _host.Chunks.Count; index++)
        {
            InlineContainerBox chunk = _host.Chunks[index];
            if (chunk.Bounds.Top > viewport.Bottom)
                break;
            if (chunk.Bounds.Bottom < viewport.Top || chunk.Bounds.Top > viewport.Bottom)
                continue;
            chunk.Paint(ds, viewport);
        }
    }

    internal void PaintSelectionForeground(CanvasDrawingSession ds, DocumentRange range, Windows.UI.Color color, Rect viewport)
    {
        var clip = _host.CodeViewportRect(_host.OuterRect(), _host.GetCodeStyle());
        NativePaintSessionResources.LayerLease layer = _host.Context.RenderResources.PaintSessions.CreateLayer(ds, 1.0f, clip);
        try
        {
            int first = _host.FindFirstVisibleChunk(viewport.Top);
            for (int index = first; index < _host.Chunks.Count; index++)
            {
                InlineContainerBox chunk = _host.Chunks[index];
                if (chunk.Bounds.Top > viewport.Bottom)
                    break;
                if (chunk.Bounds.Bottom < viewport.Top || chunk.Bounds.Top > viewport.Bottom)
                    continue;
                chunk.PaintSelectionForeground(ds, range, color, viewport);
            }
        }
        catch (Exception primary)
        {
            layer.ClosePreservingPrimary(primary);
            throw;
        }
        layer.Close();
    }

    private void PaintHorizontalScrollbar(CanvasDrawingSession ds, ElementStyle style)
    {
        HorizontalOverflowVisual.Paint(
            ds,
            _host.HorizontalScrollTrackBounds,
            _host.HorizontalScrollThumbBounds,
            _host.Context.ThemeSnapshot,
            style.Foreground);
    }
}
