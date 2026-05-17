using System;
using System.Collections.Generic;
using System.Globalization;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Windows.UI;
using MarkdownRenderer.CodeBlocks;
using MarkdownRenderer.Document;
using MarkdownRenderer.Theming;

namespace MarkdownRenderer.Layout.Boxes;

internal sealed class CodeBlockBox : BlockBox
{
    private const float HeaderHeight = 38f;
    private const float HeaderPaddingX = 12f;
    private const float HeaderGap = 8f;
    private const float CopyButtonWidth = 34f;
    private const float ActionHeight = 28f;
    private const float DiffMarkerWidth = 16f;
    private const float LineNumberPadding = 10f;
    private const float CodeTextPaddingLeft = 12f;

    private readonly MarkdownLayoutContext _context;
    private readonly List<InlineContainerBox> _chunks = new();
    private readonly List<CodeLineInfo> _lines;
    private readonly IReadOnlyList<string> _styleContextKeys;
    private readonly IReadOnlyList<string> _styleAliasKeys;

    public CodeBlockBox(
        MarkdownLayoutContext context,
        CodeBlockMetadata metadata,
        string displayedCodeText,
        bool isCopyButtonEnabled,
        bool showLineNumbers)
    {
        _context = context;
        _styleContextKeys = context.CreateStyleContextSnapshot();
        _styleAliasKeys = context.CreateStyleAliasSnapshot();
        Metadata = metadata;
        CodeLanguage = metadata.Language;
        CodeText = CodeBlockMetadata.CopyPayload(displayedCodeText);
        LanguageDisplay = metadata.LanguageDisplay;
        HeaderText = metadata.HeaderText;
        IsCopyButtonEnabled = isCopyButtonEnabled;
        ShowLineNumbers = showLineNumbers;
        _lines = BuildLines(CodeText, metadata.IsDiff);
        Margin = GetCodeStyle().Margin;
    }

    public IReadOnlyList<InlineContainerBox> Chunks => _chunks;
    public CodeBlockMetadata Metadata { get; }
    public string StableKey => Metadata.StableKey;
    public string? CodeLanguage { get; }
    public string LanguageDisplay { get; }
    public string HeaderText { get; }
    public string CodeText { get; }
    public int LineCount => _lines.Count;
    public bool IsCopyButtonEnabled { get; }
    public bool ShowLineNumbers { get; }
    public Rect CopyButtonBounds { get; private set; }
    internal FrameworkElement? RealizedCopyButton { get; set; }

    public void AddChunk(InlineContainerBox chunk)
    {
        chunk.DrawContainerChrome = false;
        chunk.UseContainerPadding = false;
        chunk.UseContainerMargin = false;
        chunk.Margin = default;
        _chunks.Add(chunk);
    }

    internal void ApplySyntaxHighlighting(IReadOnlyList<CodeBlockHighlightSpan>? spans)
    {
        var allSpans = spans ?? Array.Empty<CodeBlockHighlightSpan>();
        foreach (var chunk in _chunks)
        {
            var local = new List<CodeBlockHighlightSpan>();
            int chunkStart = chunk.CodeBlockTextOffset;
            int chunkEnd = chunkStart + chunk.CodeBlockTextLength;
            foreach (var span in allSpans)
            {
                int spanStart = Math.Max(span.Start, chunkStart);
                int spanEnd = Math.Min(span.Start + span.Length, chunkEnd);
                if (spanEnd > spanStart)
                    local.Add(new CodeBlockHighlightSpan(spanStart - chunkStart, spanEnd - spanStart, span.Foreground));
            }

            chunk.SetForegroundSpans(local);
        }
    }

    public override float Measure(float availableWidth)
    {
        ThrowIfCancellationRequested();
        var style = GetCodeStyle();
        Margin = style.Margin;

        double innerWidth = Math.Max(1, availableWidth - Margin.Left - Margin.Right);
        double gutterWidth = ComputeGutterWidth(style);
        double codeTextPaddingLeft = gutterWidth > 0 ? CodeTextPaddingLeft : 0;
        double bodyViewportWidth = Math.Max(1, innerWidth - style.Padding.Left - style.Padding.Right - gutterWidth - codeTextPaddingLeft);
        double y = Margin.Top + HeaderHeight + style.Padding.Top;

        foreach (var chunk in _chunks)
        {
            chunk.ThrowIfCancellationRequested();
            float h = chunk.Measure((float)bodyViewportWidth);
            chunk.Arrange(
                (float)(Margin.Left + style.Padding.Left + gutterWidth + codeTextPaddingLeft),
                (float)y,
                (float)bodyViewportWidth);
            y += h;
        }

        y += style.Padding.Bottom;
        y += Margin.Bottom;

        Bounds = new Rect(0, 0, availableWidth, Math.Max(0, y));
        UpdateActionBounds();
        return (float)Bounds.Height;
    }

    public override void Arrange(float x, float y, float width)
    {
        float dx = x - (float)Bounds.X;
        float dy = y - (float)Bounds.Y;
        foreach (var chunk in _chunks)
            chunk.Arrange((float)chunk.Bounds.X + dx, (float)chunk.Bounds.Y + dy, (float)chunk.Bounds.Width);

        Bounds = new Rect(x, y, width, Bounds.Height);
        UpdateActionBounds();
        IsDirty = false;
    }

    internal override void ThrowIfCancellationRequested()
    {
        _context.CancellationToken.ThrowIfCancellationRequested();
        foreach (var chunk in _chunks)
            chunk.ThrowIfCancellationRequested();
    }

    public override void Paint(CanvasDrawingSession ds, Rect viewport)
    {
        var codeStyle = GetCodeStyle();
        var headerStyle = GetHeaderStyle();
        var languageStyle = GetLanguageStyle();
        var outer = OuterRect();
        if (outer.Width <= 0 || outer.Height <= 0)
            return;

        float radius = Math.Max(0, codeStyle.CornerRadius);
        if (codeStyle.Background is { } bg)
            ds.FillRoundedRectangle(outer, radius, radius, bg);

        if (headerStyle.Background is { } headerBg)
        {
            ds.FillRoundedRectangle(outer, radius, radius, headerBg);
            if (codeStyle.Background is { } bodyBg)
            {
                ds.FillRectangle(
                    new Rect(outer.X, outer.Y + HeaderHeight, outer.Width, Math.Max(0, outer.Height - HeaderHeight)),
                    bodyBg);
            }
        }

        var separator = codeStyle.BorderBrush ?? headerStyle.BorderBrush ?? languageStyle.Foreground;
        ds.DrawLine(
            (float)outer.Left,
            (float)(outer.Top + HeaderHeight),
            (float)outer.Right,
            (float)(outer.Top + HeaderHeight),
            _context.ThemeSnapshot.IsHighContrast
                ? separator
                : WithAlpha(separator, Math.Min(separator.A, (byte)0x60)),
            1f);

        DrawHeaderText(ds, outer, languageStyle);
        DrawBodySurfaces(ds, outer, codeStyle, headerStyle, languageStyle, viewport);

        var clip = CodeViewportRect(outer, codeStyle);
        using (ds.CreateLayer(1.0f, clip))
        {
            foreach (var chunk in _chunks)
            {
                if (chunk.Bounds.Bottom < viewport.Top || chunk.Bounds.Top > viewport.Bottom)
                    continue;
                chunk.Paint(ds, viewport);
            }
        }

        if (codeStyle.BorderBrush is { } border && codeStyle.BorderThickness > 0)
        {
            float inset = codeStyle.BorderThickness / 2f;
            ds.DrawRoundedRectangle(
                new Rect(
                    outer.X + inset,
                    outer.Y + inset,
                    Math.Max(0, outer.Width - codeStyle.BorderThickness),
                    Math.Max(0, outer.Height - codeStyle.BorderThickness)),
                radius,
                radius,
                border,
                codeStyle.BorderThickness);
        }
    }

    public override bool HitTest(Point point, out DocumentPosition position)
    {
        var outer = OuterRect();
        if (!outer.Contains(point))
        {
            position = CodeStartPosition();
            return false;
        }

        if (TryHitTestChunks(point, out position))
            return true;

        var codeViewport = CodeViewportRect(outer, GetCodeStyle());
        if (point.Y < codeViewport.Top)
        {
            position = CodeStartPosition();
            return true;
        }

        if (point.Y > codeViewport.Bottom)
        {
            position = CodeEndPosition();
            return true;
        }

        if (_chunks.Count > 0)
        {
            var first = _chunks[0];
            var last = _chunks[_chunks.Count - 1];
            if (point.Y < first.Bounds.Top)
            {
                position = CodeStartPosition();
                return true;
            }

            if (point.Y > last.Bounds.Bottom)
            {
                position = CodeEndPosition();
                return true;
            }

            double clampedX = Clamp(point.X, codeViewport.Left, codeViewport.Right);
            foreach (var chunk in _chunks)
            {
                if (point.Y < chunk.Bounds.Top || point.Y > chunk.Bounds.Bottom)
                    continue;

                if (chunk.HitTest(new Point(clampedX, point.Y), out position))
                    return true;
            }
        }

        position = CodeStartPosition();
        return true;
    }

    public override IEnumerable<Rect> GetSelectionRects(DocumentRange range)
    {
        var clip = CodeViewportRect(OuterRect(), GetCodeStyle());
        foreach (var chunk in _chunks)
        {
            foreach (var rect in chunk.GetSelectionRects(range))
            {
                var clipped = Intersect(rect, clip);
                if (clipped.Width > 0 && clipped.Height > 0)
                    yield return clipped;
            }
        }
    }

    public override void PaintSelectionForeground(CanvasDrawingSession ds, DocumentRange range, Windows.UI.Color color, Rect viewport)
    {
        var clip = CodeViewportRect(OuterRect(), GetCodeStyle());
        using (ds.CreateLayer(1.0f, clip))
        {
            foreach (var chunk in _chunks)
            {
                if (chunk.Bounds.Bottom < viewport.Top || chunk.Bounds.Top > viewport.Bottom)
                    continue;
                chunk.PaintSelectionForeground(ds, range, color, viewport);
            }
        }
    }

    public override void Dispose()
    {
        foreach (var chunk in _chunks)
            chunk.Dispose();
    }

    private ElementStyle GetCodeStyle()
        => _context.ThemeSnapshot.GetStyle(MarkdownElementKeys.CodeBlock, _styleContextKeys, _styleAliasKeys);

    private ElementStyle GetHeaderStyle()
        => _context.ThemeSnapshot.GetStyle(MarkdownElementKeys.CodeBlockHeader, _styleContextKeys, _styleAliasKeys);

    private ElementStyle GetLanguageStyle()
        => _context.ThemeSnapshot.GetStyle(MarkdownElementKeys.CodeBlockLanguage, _styleContextKeys, _styleAliasKeys);

    private ElementStyle GetGutterStyle()
        => _context.ThemeSnapshot.GetStyle(MarkdownElementKeys.CodeBlockGutter, _styleContextKeys, _styleAliasKeys);

    private ElementStyle GetLineNumberStyle()
        => _context.ThemeSnapshot.GetStyle(MarkdownElementKeys.CodeBlockLineNumber, _styleContextKeys, _styleAliasKeys);

    private Rect OuterRect()
        => new(
            Bounds.X + Margin.Left,
            Bounds.Y + Margin.Top,
            Math.Max(0, Bounds.Width - Margin.Left - Margin.Right),
            Math.Max(0, Bounds.Height - Margin.Top - Margin.Bottom));

    private Rect CodeViewportRect(Rect outer, ElementStyle style)
    {
        double gutterWidth = ComputeGutterWidth(style);
        double codeTextPaddingLeft = gutterWidth > 0 ? CodeTextPaddingLeft : 0;
        double left = outer.Left + style.Padding.Left + gutterWidth + codeTextPaddingLeft;
        double top = outer.Top + HeaderHeight + style.Padding.Top;
        double height = Math.Max(0, outer.Height - HeaderHeight - style.Padding.Top - style.Padding.Bottom);
        double width = Math.Max(0, outer.Width - style.Padding.Left - style.Padding.Right - gutterWidth - codeTextPaddingLeft);
        return new Rect(left, top, width, height);
    }

    private double ComputeGutterWidth(ElementStyle style)
    {
        double diffWidth = Metadata.IsDiff ? DiffMarkerWidth : 0;
        if (!ShowLineNumbers)
            return diffWidth;

        int lastLine = Math.Max(1, Metadata.StartLine + Math.Max(0, LineCount - 1));
        int digits = lastLine.ToString(CultureInfo.InvariantCulture).Length;
        return diffWidth + LineNumberPadding + Math.Max(2, digits) * Math.Max(6, style.FontSize * 0.56f) + LineNumberPadding;
    }

    private bool TryHitTestChunks(Point point, out DocumentPosition position)
    {
        foreach (var chunk in _chunks)
        {
            if (chunk.HitTest(point, out position))
                return true;
        }

        position = CodeStartPosition();
        return false;
    }

    private DocumentPosition CodeStartPosition()
    {
        if (_chunks.Count == 0)
            return new DocumentPosition(BlockIndex, 0, 0);

        var first = _chunks[0];
        if (first.Runs.Count == 0)
            return new DocumentPosition(first.BlockIndex, 0, 0);

        return new DocumentPosition(first.BlockIndex, first.Runs[0].InlineIndex, 0);
    }

    private DocumentPosition CodeEndPosition()
    {
        if (_chunks.Count == 0)
            return new DocumentPosition(BlockIndex, 0, 0);

        var last = _chunks[_chunks.Count - 1];
        if (last.Runs.Count == 0)
            return new DocumentPosition(last.BlockIndex, 0, 0);

        var run = last.Runs[last.Runs.Count - 1];
        return new DocumentPosition(last.BlockIndex, run.InlineIndex, run.Text.Length);
    }

    private static double Clamp(double value, double min, double max)
    {
        if (max <= min)
            return min;

        return Math.Min(Math.Max(value, min), max - 0.5);
    }

    private void UpdateActionBounds()
    {
        var outer = OuterRect();
        double top = outer.Top + Math.Max(0, (HeaderHeight - ActionHeight) / 2.0);
        bool rtl = _context.FlowDirection == FlowDirection.RightToLeft;

        CopyButtonBounds = Rect.Empty;
        if (rtl)
        {
            double x = outer.Left + HeaderPaddingX;
            if (IsCopyButtonEnabled)
            {
                CopyButtonBounds = new Rect(x, top, CopyButtonWidth, ActionHeight);
            }
        }
        else
        {
            double x = outer.Right - HeaderPaddingX;
            if (IsCopyButtonEnabled)
            {
                x -= CopyButtonWidth;
                CopyButtonBounds = new Rect(x, top, CopyButtonWidth, ActionHeight);
            }
        }
    }

    private void DrawHeaderText(CanvasDrawingSession ds, Rect outer, ElementStyle style)
    {
        bool rtl = _context.FlowDirection == FlowDirection.RightToLeft;
        double actionLeft = outer.Right - HeaderPaddingX;
        double actionRight = outer.Left + HeaderPaddingX;
        if (!CopyButtonBounds.IsEmpty)
        {
            actionLeft = Math.Min(actionLeft, CopyButtonBounds.Left);
            actionRight = Math.Max(actionRight, CopyButtonBounds.Right);
        }
        double left = rtl ? actionRight + HeaderGap : outer.Left + HeaderPaddingX;
        double right = rtl ? outer.Right - HeaderPaddingX : actionLeft - HeaderGap;
        if (right <= left)
            return;

        using var format = new CanvasTextFormat
        {
            FontFamily = style.FontFamily,
            FontSize = style.FontSize,
            FontWeight = style.FontWeight,
            FontStyle = style.FontStyle,
            WordWrapping = CanvasWordWrapping.NoWrap,
            Direction = rtl
                ? CanvasTextDirection.RightToLeftThenTopToBottom
                : CanvasTextDirection.LeftToRightThenTopToBottom,
            HorizontalAlignment = rtl ? CanvasHorizontalAlignment.Right : CanvasHorizontalAlignment.Left,
            VerticalAlignment = CanvasVerticalAlignment.Center,
        };

        ds.DrawText(HeaderText, new Rect(left, outer.Top, right - left, HeaderHeight), style.Foreground, format);
    }

    private void DrawBodySurfaces(
        CanvasDrawingSession ds,
        Rect outer,
        ElementStyle codeStyle,
        ElementStyle headerStyle,
        ElementStyle languageStyle,
        Rect viewport)
    {
        var gutterStyle = GetGutterStyle();
        var lineNumberStyle = GetLineNumberStyle();
        double gutterWidth = ComputeGutterWidth(codeStyle);
        var bodyTop = outer.Top + HeaderHeight;
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
                _context.ThemeSnapshot.IsHighContrast ? sep : WithAlpha(sep, 0x40),
                1f);
        }

        DrawLineDecorations(ds, outer, codeStyle, lineNumberStyle, viewport);
    }

    private void DrawLineDecorations(CanvasDrawingSession ds, Rect outer, ElementStyle codeStyle, ElementStyle lineNumberStyle, Rect viewport)
    {
        double gutterWidth = ComputeGutterWidth(codeStyle);
        if (_lines.Count == 0)
            return;

        using var lineNumberFormat = new CanvasTextFormat
        {
            FontFamily = lineNumberStyle.FontFamily,
            FontSize = lineNumberStyle.FontSize,
            FontWeight = lineNumberStyle.FontWeight,
            FontStyle = lineNumberStyle.FontStyle,
            WordWrapping = CanvasWordWrapping.NoWrap,
            HorizontalAlignment = CanvasHorizontalAlignment.Right,
            VerticalAlignment = CanvasVerticalAlignment.Top,
        };

        var textViewport = CodeViewportRect(outer, codeStyle);
        double fullLeft = outer.Left;
        double fullRight = outer.Right;
        double numberLeft = outer.Left + (Metadata.IsDiff ? DiffMarkerWidth : 0);
        double numberWidth = Math.Max(0, gutterWidth - (Metadata.IsDiff ? DiffMarkerWidth : 0) - LineNumberPadding);
        var additionBg = _context.ThemeSnapshot.IsHighContrast
            ? Color.FromArgb(0x00, 0, 0, 0)
            : Color.FromArgb(0x24, 0x2E, 0xC2, 0x7E);
        var removalBg = _context.ThemeSnapshot.IsHighContrast
            ? Color.FromArgb(0x00, 0, 0, 0)
            : Color.FromArgb(0x24, 0xF8, 0x51, 0x49);
        var highlightBg = _context.ThemeSnapshot.IsHighContrast
            ? Color.FromArgb(0x00, 0, 0, 0)
            : Color.FromArgb(0x26, 0xFF, 0xD8, 0x66);

        foreach (var line in _lines)
        {
            var rects = GetLineRects(line);
            Rect first = Rect.Empty;
            foreach (var rect in rects)
            {
                if (first.IsEmpty)
                    first = rect;
                double top = rect.Top;
                double bottom = rect.Bottom;
                if (bottom < viewport.Top || top > viewport.Bottom)
                    continue;

                Color? bg = line.DiffKind switch
                {
                    CodeLineDiffKind.Added => additionBg,
                    CodeLineDiffKind.Removed => removalBg,
                    _ => Metadata.HighlightedLines.Contains(line.Number) ? highlightBg : null,
                };
                if (bg is { A: > 0 } lineBg)
                    ds.FillRectangle(new Rect(fullLeft, top, fullRight - fullLeft, Math.Max(1, bottom - top)), lineBg);
            }

            if (!first.IsEmpty && ShowLineNumbers)
            {
                var label = (Metadata.StartLine + line.Number - 1).ToString(CultureInfo.InvariantCulture);
                ds.DrawText(
                    label,
                    new Rect(numberLeft, first.Top, numberWidth, Math.Max(1, first.Height)),
                    lineNumberStyle.Foreground,
                    lineNumberFormat);
            }

            if (!first.IsEmpty && Metadata.IsDiff && line.DiffKind is not CodeLineDiffKind.None)
            {
                var marker = line.DiffKind == CodeLineDiffKind.Added ? "+" : "-";
                var markerColor = line.DiffKind == CodeLineDiffKind.Added
                    ? Color.FromArgb(0xFF, 0x2E, 0xC2, 0x7E)
                    : Color.FromArgb(0xFF, 0xF8, 0x51, 0x49);
                ds.DrawText(
                    marker,
                    new Rect(outer.Left + 4, first.Top, DiffMarkerWidth - 4, Math.Max(1, first.Height)),
                    _context.ThemeSnapshot.IsHighContrast ? lineNumberStyle.Foreground : markerColor,
                    lineNumberFormat);
            }
        }
    }

    private IEnumerable<Rect> GetLineRects(CodeLineInfo line)
    {
        int lineStart = line.Start;
        int lineEnd = line.Start + Math.Max(1, line.Length);
        foreach (var chunk in _chunks)
        {
            int chunkStart = chunk.CodeBlockTextOffset;
            int chunkEnd = chunkStart + chunk.CodeBlockTextLength;
            int overlapStart = Math.Max(lineStart, chunkStart);
            int overlapEnd = Math.Min(lineEnd, chunkEnd);
            if (overlapEnd <= overlapStart)
                continue;

            foreach (var rect in chunk.GetBufferRangeLineRects(overlapStart - chunkStart, overlapEnd - overlapStart))
                yield return rect;
        }
    }

    private static List<CodeLineInfo> BuildLines(string code, bool isDiff)
    {
        var lines = new List<CodeLineInfo>();
        if (code.Length == 0)
        {
            lines.Add(new CodeLineInfo(1, 0, 0, CodeLineDiffKind.None));
            return lines;
        }

        int start = 0;
        int number = 1;
        for (int i = 0; i <= code.Length; i++)
        {
            bool atEnd = i == code.Length;
            if (!atEnd && code[i] != '\n')
                continue;

            int end = atEnd ? i : i + 1;
            int contentLength = end - start;
            var diffKind = CodeLineDiffKind.None;
            if (isDiff && contentLength > 0)
            {
                char first = code[start];
                if (first == '+')
                    diffKind = CodeLineDiffKind.Added;
                else if (first == '-')
                    diffKind = CodeLineDiffKind.Removed;
            }

            lines.Add(new CodeLineInfo(number, start, contentLength, diffKind));
            number++;
            start = end;
        }

        return lines;
    }

    private static Rect Intersect(Rect a, Rect b)
    {
        double left = Math.Max(a.Left, b.Left);
        double top = Math.Max(a.Top, b.Top);
        double right = Math.Min(a.Right, b.Right);
        double bottom = Math.Min(a.Bottom, b.Bottom);
        return right > left && bottom > top
            ? new Rect(left, top, right - left, bottom - top)
            : Rect.Empty;
    }

    private static Windows.UI.Color WithAlpha(Windows.UI.Color color, byte alpha)
        => Windows.UI.Color.FromArgb(alpha, color.R, color.G, color.B);

    private readonly record struct CodeLineInfo(int Number, int Start, int Length, CodeLineDiffKind DiffKind);

    private enum CodeLineDiffKind
    {
        None,
        Added,
        Removed,
    }
}
