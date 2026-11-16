using System;
using System.Collections.Generic;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Windows.Foundation;
using Windows.UI;
using MarkdownRenderer.Layout.Internal.Inlines;
using MarkdownRenderer.Layout.Internal.Placement;
using MarkdownRenderer.Theming;

namespace MarkdownRenderer.Layout.Internal.CodeBlocks;

/// <summary>
/// Paints a code block's body surfaces: the gutter, diff and highlight line
/// backgrounds, diff markers and line numbers for the visible visual rows.
/// </summary>
internal sealed class CodeBlockGutterPainter
{
    private readonly CodeBlockBox _box;

    internal CodeBlockGutterPainter(CodeBlockBox box) => _box = box;

    internal void DrawBodySurfaces(
        CanvasDrawingSession ds,
        Rect outer,
        ElementStyle codeStyle,
        ElementStyle headerStyle,
        ElementStyle languageStyle,
        Rect viewport)
    {
        var gutterStyle = _box.GetGutterStyle();
        var lineNumberStyle = _box.GetLineNumberStyle();
        double gutterWidth = _box.ComputeGutterWidth(codeStyle);
        var bodyTop = outer.Top + _box.HeaderHeight;
        var bodyBottom = outer.Bottom;

        if (gutterWidth > 0)
        {
            var gutterRect = new Rect(
                outer.Left,
                bodyTop,
                Math.Min(gutterWidth + codeStyle.Padding.Left, outer.Width),
                Math.Max(0, bodyBottom - bodyTop));
            if (gutterStyle.Background is { } gutterBg)
                ds.FillRectangle(gutterRect, gutterBg);

            var sep = codeStyle.BorderBrush ?? headerStyle.BorderBrush ?? languageStyle.Foreground;
            ds.DrawLine(
                (float)gutterRect.Right,
                (float)bodyTop,
                (float)gutterRect.Right,
                (float)bodyBottom,
                _box.Context.ThemeSnapshot.IsHighContrast ? sep : CodeBlockBox.WithAlpha(sep, 0x40),
                1f);
        }

        DrawLineDecorations(ds, outer, codeStyle, lineNumberStyle, viewport);
    }

    private static readonly Color TransparentLineBackground = Color.FromArgb(0x00, 0, 0, 0);
    private static readonly Color AdditionLineBackground = Color.FromArgb(0x24, 0x2E, 0xC2, 0x7E);
    private static readonly Color RemovalLineBackground = Color.FromArgb(0x24, 0xF8, 0x51, 0x49);
    private static readonly Color HighlightLineBackground = Color.FromArgb(0x26, 0xFF, 0xD8, 0x66);

    private void DrawLineDecorations(CanvasDrawingSession ds, Rect outer, ElementStyle codeStyle, ElementStyle lineNumberStyle, Rect viewport)
    {
        double gutterWidth = _box.ComputeGutterWidth(codeStyle);
        if (_box.LineTable.Count == 0)
            return;

        double diffMarkerWidth = _box.Metadata.IsDiff ? CodeBlockBox.DiffMarkerWidth : 0;
        double numberLeft = outer.Left + diffMarkerWidth;
        double numberWidth = Math.Max(0, gutterWidth - diffMarkerWidth - CodeBlockBox.LineNumberPadding);

        if (_box.VisualLineIndex.Lines.Length > 0)
            DrawVisualLineDecorations(ds, outer, lineNumberStyle, viewport, numberLeft, numberWidth);
        else
            DrawChunkLineDecorations(ds, outer, lineNumberStyle, viewport, numberLeft, numberWidth);
    }

    // Wrapped or indexed code: one background per visible visual row and one
    // indicator per logical line, starting from the first intersecting row.
    private void DrawVisualLineDecorations(
        CanvasDrawingSession ds,
        Rect outer,
        ElementStyle lineNumberStyle,
        Rect viewport,
        double numberLeft,
        double numberWidth)
    {
        int firstVisual = VerticalViewportIndex.FindFirstIntersecting(
            _box.VisualLineIndex.BottomEdges,
            viewport.Top);
        if (firstVisual >= _box.VisualLineIndex.Lines.Length || _box.VisualLineIndex.Lines[firstVisual].Top > viewport.Bottom)
            return;

        CanvasTextFormat? lineNumberFormat = GetIndicatorTextFormat(lineNumberStyle);
        int previousLogicalLine = -1;
        for (int visualIndex = firstVisual; visualIndex < _box.VisualLineIndex.Lines.Length; visualIndex++)
        {
            CodeVisualLineInfo visual = _box.VisualLineIndex.Lines[visualIndex];
            if (visual.Top > viewport.Bottom)
                break;

            CodeLineInfo line = _box.LineTable[visual.LogicalLineIndex];
            Color? bg = GetLineBackground(line);
            if (bg is { A: > 0 } lineBg)
            {
                var backgroundRect = new Rect(
                    outer.Left,
                    visual.Top,
                    outer.Right - outer.Left,
                    visual.Height);
                if (CodeBlockBox.IsDrawableRectangle(backgroundRect))
                    ds.FillRectangle(backgroundRect, lineBg);
            }

            if (line.Number == previousLogicalLine)
                continue;
            previousLogicalLine = line.Number;

            if (lineNumberFormat is not null)
            {
                DrawLineIndicators(
                    ds,
                    outer,
                    line,
                    visual.Top,
                    visual.Height,
                    numberLeft,
                    numberWidth,
                    viewport,
                    lineNumberStyle,
                    lineNumberFormat);
            }
        }
    }

    // Fallback before the visual index exists: walk logical lines covered by
    // the visible chunks and decorate each of their measured row rectangles.
    private void DrawChunkLineDecorations(
        CanvasDrawingSession ds,
        Rect outer,
        ElementStyle lineNumberStyle,
        Rect viewport,
        double numberLeft,
        double numberWidth)
    {
        int firstChunk = _box.FindFirstVisibleChunk(viewport.Top);
        if (firstChunk >= _box.Chunks.Count)
            return;

        CanvasTextFormat? fallbackLineNumberFormat = GetIndicatorTextFormat(lineNumberStyle);

        int lastChunk = firstChunk;
        while (lastChunk + 1 < _box.Chunks.Count &&
               _box.Chunks[lastChunk + 1].Bounds.Top <= viewport.Bottom)
        {
            lastChunk++;
        }

        int visibleTextStart = _box.Chunks[firstChunk].CodeBlockTextOffset;
        InlineContainerBox finalChunk = _box.Chunks[lastChunk];
        int visibleTextEnd = finalChunk.CodeBlockTextOffset + finalChunk.CodeBlockTextLength;
        int firstLine = _box.VisualLineIndex.FindFirstLineEndingAtOrAfter(visibleTextStart);
        for (int lineIndex = firstLine; lineIndex < _box.LineTable.Count; lineIndex++)
        {
            CodeLineInfo line = _box.LineTable[lineIndex];
            if (line.Start >= visibleTextEnd)
                break;
            Rect first = FillLineRowBackgrounds(ds, outer, line, viewport);

            if (!first.IsEmpty && fallbackLineNumberFormat is not null)
                DrawLineIndicators(
                    ds,
                    outer,
                    line,
                    first.Top,
                    Math.Max(1, first.Height),
                    numberLeft,
                    numberWidth,
                    viewport,
                    lineNumberStyle,
                    fallbackLineNumberFormat);
        }
    }

    // Fills the background of each visible row rectangle of one logical line
    // and returns the first visible rectangle, or Rect.Empty.
    private Rect FillLineRowBackgrounds(CanvasDrawingSession ds, Rect outer, CodeLineInfo line, Rect viewport)
    {
        Color? bg = GetLineBackground(line);
        Rect first = Rect.Empty;
        foreach (var rect in GetLineRects(line))
        {
            if (!CodeBlockBox.IsDrawableRectangle(rect))
                continue;

            double top = rect.Top;
            double bottom = rect.Bottom;
            if (bottom < viewport.Top || top > viewport.Bottom)
                continue;
            if (first.IsEmpty)
                first = rect;

            if (bg is { A: > 0 } lineBg)
            {
                var backgroundRect = new Rect(
                    outer.Left,
                    top,
                    outer.Right - outer.Left,
                    Math.Max(1, bottom - top));
                if (CodeBlockBox.IsDrawableRectangle(backgroundRect))
                    ds.FillRectangle(backgroundRect, lineBg);
            }
        }

        return first;
    }

    private CanvasTextFormat? GetIndicatorTextFormat(ElementStyle lineNumberStyle) =>
        _box.ShowLineNumbers || _box.Metadata.IsDiff
            ? _box.GetLineNumberTextFormat(lineNumberStyle)
            : null;

    // High Contrast keeps diff and highlight rows unfilled; the markers and
    // line numbers carry the state instead.
    private Color? GetLineBackground(CodeLineInfo line)
    {
        bool highContrast = _box.Context.ThemeSnapshot.IsHighContrast;
        return line.DiffKind switch
        {
            CodeLineDiffKind.Added => highContrast ? TransparentLineBackground : AdditionLineBackground,
            CodeLineDiffKind.Removed => highContrast ? TransparentLineBackground : RemovalLineBackground,
            _ => _box.Metadata.HighlightedLines.Contains(line.Number)
                ? highContrast ? TransparentLineBackground : HighlightLineBackground
                : null,
        };
    }

    private void DrawLineIndicators(
        CanvasDrawingSession ds,
        Rect outer,
        CodeLineInfo line,
        double top,
        double height,
        double numberLeft,
        double numberWidth,
        Rect viewport,
        ElementStyle lineNumberStyle,
        CanvasTextFormat lineNumberFormat)
    {
        if (_box.ShowLineNumbers)
        {
            var numberRect = new Rect(numberLeft, top, numberWidth, height);
            if (CodeBlockBox.IsDrawableRectangle(numberRect))
                DrawLineNumber(ds, line, numberRect, viewport, lineNumberStyle, lineNumberFormat);
        }

        if (!_box.Metadata.IsDiff || line.DiffKind is CodeLineDiffKind.None)
            return;

        string marker = line.DiffKind == CodeLineDiffKind.Added ? "+" : "-";
        Color markerColor = line.DiffKind == CodeLineDiffKind.Added
            ? Color.FromArgb(0xFF, 0x2E, 0xC2, 0x7E)
            : Color.FromArgb(0xFF, 0xF8, 0x51, 0x49);
        var markerRect = new Rect(outer.Left + 4, top, CodeBlockBox.DiffMarkerWidth - 4, height);
        if (CodeBlockBox.IsDrawableRectangle(markerRect))
        {
            DrawGutterText(
                ds, marker, line.Number, "diff marker", markerRect, viewport,
                _box.Context.ThemeSnapshot.IsHighContrast ? lineNumberStyle.Foreground : markerColor,
                lineNumberStyle, lineNumberFormat);
        }
    }

    private static void DrawLineNumber(
        CanvasDrawingSession ds,
        CodeLineInfo line,
        Rect rectangle,
        Rect viewport,
        ElementStyle style,
        CanvasTextFormat format)
        => DrawGutterText(
            ds, line.Label, line.Number, "line number", rectangle, viewport,
            style.Foreground, style, format);

    private static void DrawGutterText(
        CanvasDrawingSession ds,
        string text,
        int lineNumber,
        string kind,
        Rect rectangle,
        Rect viewport,
        Color foreground,
        ElementStyle style,
        CanvasTextFormat format)
    {
        try
        {
            ds.DrawText(text, rectangle, foreground, format);
        }
        catch (ArgumentException exception)
        {
            Exception failure = exception;
            if (exception.HResult == unchecked((int)0x80070057) &&
                CodeBlockBox.IsDrawableRectangle(rectangle))
            {
                // Win2D has intermittently rejected valid gutter geometry
                // with an app-packaged font during offscreen capture. Keep the
                // authored format on the normal path; retry only this
                // nonessential gutter label with a system monospace family.
                // A broken device or geometry still fails the second draw,
                // preserving both exceptions for diagnosis.
                try
                {
                    using var fallbackFormat = new CanvasTextFormat
                    {
                        FontFamily = "Consolas",
                        FontSize = style.FontSize,
                        FontWeight = style.FontWeight,
                        FontStyle = style.FontStyle,
                        LocaleName = format.LocaleName,
                        WordWrapping = CanvasWordWrapping.NoWrap,
                        HorizontalAlignment = CanvasHorizontalAlignment.Right,
                        VerticalAlignment = CanvasVerticalAlignment.Top,
                    };
                    ds.DrawText(text, rectangle, foreground, fallbackFormat);
                    return;
                }
                catch (ArgumentException fallbackException)
                {
                    failure = new AggregateException(exception, fallbackException);
                }
            }

            // Win2D can reject a rectangle or text-format value even after the
            // generic finite-coordinate check. Preserve the failure while
            // recording geometry (never code text) for a reproducible fix.
            string detail = FormattableString.Invariant(
                $"line={lineNumber}, kind={kind}, labelLength={text.Length}, ") +
                FormattableString.Invariant(
                    $"rectangle={rectangle.X:R},{rectangle.Y:R},{rectangle.Width:R},{rectangle.Height:R}, ") +
                FormattableString.Invariant(
                    $"viewport={viewport.X:R},{viewport.Y:R},{viewport.Width:R},{viewport.Height:R}, ") +
                FormattableString.Invariant(
                    $"fontSize={style.FontSize:R}, fontFamily={style.FontFamily}.");
            throw new InvalidOperationException(
                "Code gutter DrawText rejected: " + detail,
                failure);
        }
    }

    private IEnumerable<Rect> GetLineRects(CodeLineInfo line)
    {
        int lineStart = line.Start;
        int lineEnd = line.Start + Math.Max(1, line.Length);
        int firstChunk = _box.FindFirstChunkEndingAtOrAfter(lineStart);
        for (int chunkIndex = firstChunk; chunkIndex < _box.Chunks.Count; chunkIndex++)
        {
            InlineContainerBox chunk = _box.Chunks[chunkIndex];
            int chunkStart = chunk.CodeBlockTextOffset;
            if (chunkStart >= lineEnd)
                break;
            int chunkEnd = chunkStart + chunk.CodeBlockTextLength;
            int overlapStart = Math.Max(lineStart, chunkStart);
            int overlapEnd = Math.Min(lineEnd, chunkEnd);
            if (overlapEnd <= overlapStart)
                continue;

            foreach (var rect in chunk.GetBufferRangeLineRects(overlapStart - chunkStart, overlapEnd - overlapStart))
                yield return rect;
        }
    }
}
