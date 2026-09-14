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

internal sealed class CodeBlockBox : BlockBox, IHorizontalOverflowBox
{
    private const float HeaderHeight = 38f;
    private const float HeaderPaddingX = 12f;
    private const float HeaderGap = 8f;
    private const float CopyButtonWidth = 34f;
    private const float ActionHeight = 28f;
    private const float DiffMarkerWidth = 16f;
    private const float LineNumberPadding = 10f;
    private const float CodeTextPaddingLeft = 12f;
    private const float HorizontalScrollbarHeight = 12f;
    private const float MinimumScrollbarThumbWidth = 24f;

    private readonly MarkdownLayoutContext _context;
    private readonly List<InlineContainerBox> _chunks = new();
    private double[] _chunkBottomEdges = Array.Empty<double>();
    private CodeVisualLineInfo[] _visualLines = Array.Empty<CodeVisualLineInfo>();
    private double[] _visualLineBottomEdges = Array.Empty<double>();
    private readonly List<CodeLineInfo> _lines;
    private readonly IReadOnlyList<string> _styleContextKeys;
    private readonly IReadOnlyList<string> _styleAliasKeys;
    private ElementStyle? _codeStyle;
    private ElementStyle? _headerStyle;
    private ElementStyle? _languageStyle;
    private ElementStyle? _gutterStyle;
    private ElementStyle? _lineNumberStyle;
    private CanvasTextFormat? _headerTextFormat;
    private CanvasTextFormat? _lineNumberTextFormat;
    private float _contentWidth;
    private float _bodyViewportWidth;
    private double _horizontalOffset;
    private float _arrangedX;
    private float _arrangedY;
    private float _arrangedWidth;
    private bool _hasArrangement;

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
        _lines = BuildLines(CodeText, metadata.IsDiff, metadata.StartLine);
        Margin = GetCodeStyle().Margin;
    }

    public IReadOnlyList<InlineContainerBox> Chunks => _chunks;
    internal MarkdownLayoutContext Context => _context;
    public CodeBlockMetadata Metadata { get; }
    public string StableKey => Metadata.StableKey;
    public string? CodeLanguage { get; }
    public string LanguageDisplay { get; }
    public string HeaderText { get; }
    public string CodeText { get; }
    public int LineCount => _lines.Count;
    internal int IndexedVisualLineCount => _visualLines.Length;
    public bool IsCopyButtonEnabled { get; }
    public bool ShowLineNumbers { get; }
    public Rect CopyButtonBounds { get; private set; }
    internal FrameworkElement? RealizedCopyButton { get; set; }
    public double HorizontalOffset => _horizontalOffset;
    public double HorizontalExtent => Math.Max(_contentWidth, _bodyViewportWidth);
    public double HorizontalViewport => _bodyViewportWidth;
    public bool CanScrollHorizontally => HorizontalExtent > HorizontalViewport + 0.5;
    public bool IsRightToLeft => _context.FlowDirection == FlowDirection.RightToLeft;

    public Rect HorizontalViewportBounds
        => CodeViewportRect(OuterRect(), GetCodeStyle());

    public Rect HorizontalScrollTrackBounds
    {
        get
        {
            if (!CanScrollHorizontally)
                return Rect.Empty;

            Rect outer = OuterRect();
            var style = GetCodeStyle();
            double gutterWidth = ComputeGutterWidth(style);
            double codeTextPaddingLeft = gutterWidth > 0 ? CodeTextPaddingLeft : 0;
            double left = outer.Left + style.Padding.Left + gutterWidth + codeTextPaddingLeft;
            double width = Math.Max(0, outer.Width - style.Padding.Left - style.Padding.Right - gutterWidth - codeTextPaddingLeft);
            double y = outer.Bottom - style.Padding.Bottom - HorizontalScrollbarHeight;
            return new Rect(left, y, width, HorizontalScrollbarHeight);
        }
    }

    public Rect HorizontalScrollThumbBounds
    {
        get
        {
            Rect track = HorizontalScrollTrackBounds;
            if (track.IsEmpty || HorizontalExtent <= 0)
                return Rect.Empty;

            double thumbWidth = Math.Clamp(
                track.Width * HorizontalViewport / HorizontalExtent,
                Math.Min(MinimumScrollbarThumbWidth, track.Width),
                track.Width);
            double travel = Math.Max(0, track.Width - thumbWidth);
            double maximum = Math.Max(0, HorizontalExtent - HorizontalViewport);
            double fraction = maximum <= 0 ? 0 : HorizontalOffset / maximum;
            bool rtl = _context.FlowDirection == FlowDirection.RightToLeft;
            double x = rtl
                ? track.Right - thumbWidth - travel * fraction
                : track.Left + travel * fraction;
            return new Rect(x, track.Y, thumbWidth, track.Height);
        }
    }

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
        _bodyViewportWidth = (float)Math.Max(1, innerWidth - style.Padding.Left - style.Padding.Right - gutterWidth - codeTextPaddingLeft);
        double y = Margin.Top + HeaderHeight + style.Padding.Top;
        _contentWidth = 0;

        foreach (var chunk in _chunks)
        {
            chunk.ThrowIfCancellationRequested();
            float h = chunk.Measure(_bodyViewportWidth);
            chunk.Arrange(
                (float)(Margin.Left + style.Padding.Left + gutterWidth + codeTextPaddingLeft),
                (float)y,
                _bodyViewportWidth);
            _contentWidth = Math.Max(_contentWidth, chunk.ContentWidth);
            y += h;
        }

        _horizontalOffset = Math.Clamp(
            _horizontalOffset,
            0,
            Math.Max(0, HorizontalExtent - HorizontalViewport));
        y += style.Padding.Bottom;
        if (CanScrollHorizontally)
            y += HorizontalScrollbarHeight;
        y += Margin.Bottom;

        Bounds = new Rect(0, 0, availableWidth, Math.Max(0, y));
        UpdateActionBounds();
        _arrangedX = 0;
        _arrangedY = 0;
        _arrangedWidth = availableWidth;
        _hasArrangement = true;
        ArrangeChunks();
        return (float)Bounds.Height;
    }

    public override void Arrange(float x, float y, float width)
    {
        Bounds = new Rect(x, y, width, Bounds.Height);
        _arrangedX = x;
        _arrangedY = y;
        _arrangedWidth = width;
        _hasArrangement = true;
        ArrangeChunks();
        UpdateActionBounds();
        IsDirty = false;
    }

    private void ArrangeChunks()
    {
        if (!_hasArrangement)
            return;

        var style = GetCodeStyle();
        double gutterWidth = ComputeGutterWidth(style);
        double codeTextPaddingLeft = gutterWidth > 0 ? CodeTextPaddingLeft : 0;
        float viewportLeft = (float)(_arrangedX + Margin.Left + style.Padding.Left + gutterWidth + codeTextPaddingLeft);
        float contentLeft = _context.FlowDirection == FlowDirection.RightToLeft
            ? viewportLeft + _bodyViewportWidth - _contentWidth + (float)_horizontalOffset
            : viewportLeft - (float)_horizontalOffset;
        float y = (float)(_arrangedY + Margin.Top + HeaderHeight + style.Padding.Top);
        foreach (var chunk in _chunks)
        {
            chunk.Arrange(contentLeft, y, _bodyViewportWidth);
            y += (float)chunk.Bounds.Height;
        }
        RefreshChunkBottomEdges();
        RebuildVisualLineIndex();
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
        // A CanvasActiveLayer is a comparatively expensive projected/native
        // allocation. The tile already clips vertically and measured chunks
        // remain inside the body when the code fits horizontally, so create a
        // local clip only for a genuinely overflowing no-wrap block.
        if (_contentWidth > _bodyViewportWidth + 0.5f)
        {
            using (ds.CreateLayer(1.0f, clip))
            {
                PaintVisibleChunks(ds, viewport);
            }
        }
        else
        {
            PaintVisibleChunks(ds, viewport);
        }

        PaintHorizontalScrollbar(ds, codeStyle);

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

    private void PaintVisibleChunks(CanvasDrawingSession ds, Rect viewport)
    {
        int first = FindFirstVisibleChunk(viewport.Top);
        for (int index = first; index < _chunks.Count; index++)
        {
            InlineContainerBox chunk = _chunks[index];
            if (chunk.Bounds.Top > viewport.Bottom)
                break;
            if (chunk.Bounds.Bottom < viewport.Top || chunk.Bounds.Top > viewport.Bottom)
                continue;
            chunk.Paint(ds, viewport);
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
            int firstVisible = FindFirstVisibleChunk(point.Y);
            for (int index = firstVisible; index < _chunks.Count; index++)
            {
                InlineContainerBox chunk = _chunks[index];
                if (chunk.Bounds.Top > point.Y)
                    break;
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
            int first = FindFirstVisibleChunk(viewport.Top);
            for (int index = first; index < _chunks.Count; index++)
            {
                InlineContainerBox chunk = _chunks[index];
                if (chunk.Bounds.Top > viewport.Bottom)
                    break;
                if (chunk.Bounds.Bottom < viewport.Top || chunk.Bounds.Top > viewport.Bottom)
                    continue;
                chunk.PaintSelectionForeground(ds, range, color, viewport);
            }
        }
    }

    public override void Dispose()
    {
        _headerTextFormat?.Dispose();
        _headerTextFormat = null;
        _lineNumberTextFormat?.Dispose();
        _lineNumberTextFormat = null;
        foreach (var chunk in _chunks)
            chunk.Dispose();
    }

    private ElementStyle GetCodeStyle()
        => _codeStyle ??= GetStyle(MarkdownElementKeys.CodeBlock);

    private ElementStyle GetHeaderStyle()
        => _headerStyle ??= GetStyle(MarkdownElementKeys.CodeBlockHeader);

    private ElementStyle GetLanguageStyle()
        => _languageStyle ??= GetStyle(MarkdownElementKeys.CodeBlockLanguage);

    private ElementStyle GetGutterStyle()
        => _gutterStyle ??= GetStyle(MarkdownElementKeys.CodeBlockGutter);

    private ElementStyle GetLineNumberStyle()
        => _lineNumberStyle ??= GetStyle(MarkdownElementKeys.CodeBlockLineNumber);

    private ElementStyle GetStyle(string elementKey)
        => _context.ThemeSnapshot.GetStyle(
            elementKey,
            _styleContextKeys,
            _styleAliasKeys,
            CodeLanguage,
            Metadata.IsDiff ? "diff" : null);

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
        double scrollbarHeight = CanScrollHorizontally ? HorizontalScrollbarHeight : 0;
        double height = Math.Max(0, outer.Height - HeaderHeight - style.Padding.Top - style.Padding.Bottom - scrollbarHeight);
        double width = Math.Max(0, outer.Width - style.Padding.Left - style.Padding.Right - gutterWidth - codeTextPaddingLeft);
        return new Rect(left, top, width, height);
    }

    public bool ScrollHorizontal(double delta)
        => SetHorizontalOffset(HorizontalOffset + delta);

    public bool SetHorizontalOffset(double offset)
    {
        double maximum = Math.Max(0, HorizontalExtent - HorizontalViewport);
        double next = Math.Clamp(offset, 0, maximum);
        if (Math.Abs(next - _horizontalOffset) <= 0.1)
            return false;

        _horizontalOffset = next;
        ArrangeChunks();
        return true;
    }

    private void PaintHorizontalScrollbar(CanvasDrawingSession ds, ElementStyle style)
    {
        HorizontalOverflowVisual.Paint(
            ds,
            HorizontalScrollTrackBounds,
            HorizontalScrollThumbBounds,
            _context.ThemeSnapshot,
            style.Foreground);
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
        int first = FindFirstVisibleChunk(point.Y);
        for (int index = first; index < _chunks.Count; index++)
        {
            InlineContainerBox chunk = _chunks[index];
            if (chunk.Bounds.Top > point.Y)
                break;
            if (chunk.HitTest(point, out position))
                return true;
        }

        position = CodeStartPosition();
        return false;
    }

    private int FindFirstVisibleChunk(double viewportTop)
        => VerticalViewportIndex.FindFirstIntersecting(_chunkBottomEdges, viewportTop);

    internal (int First, int EndExclusive) GetVisibleChunkRange(
        double viewportTop,
        double viewportBottom)
    {
        int first = FindFirstVisibleChunk(viewportTop);
        int end = first;
        while (end < _chunks.Count && _chunks[end].Bounds.Top <= viewportBottom)
            end++;
        return (first, end);
    }

    private int FindFirstChunkEndingAtOrAfter(int textOffset)
    {
        int low = 0;
        int high = _chunks.Count;
        while (low < high)
        {
            int middle = low + ((high - low) >> 1);
            InlineContainerBox chunk = _chunks[middle];
            int end = chunk.CodeBlockTextOffset + chunk.CodeBlockTextLength;
            if (end <= textOffset)
                low = middle + 1;
            else
                high = middle;
        }

        return low;
    }

    private int FindFirstLineEndingAtOrAfter(int textOffset)
    {
        int low = 0;
        int high = _lines.Count;
        while (low < high)
        {
            int middle = low + ((high - low) >> 1);
            CodeLineInfo line = _lines[middle];
            int end = line.Start + Math.Max(1, line.Length);
            if (end <= textOffset)
                low = middle + 1;
            else
                high = middle;
        }

        return low;
    }

    private void RefreshChunkBottomEdges()
    {
        if (_chunkBottomEdges.Length != _chunks.Count)
            _chunkBottomEdges = new double[_chunks.Count];
        for (int index = 0; index < _chunks.Count; index++)
            _chunkBottomEdges[index] = _chunks[index].Bounds.Bottom;
    }

    private void RebuildVisualLineIndex()
    {
        if (_chunks.Count == 0 || _lines.Count == 0)
        {
            _visualLines = Array.Empty<CodeVisualLineInfo>();
            _visualLineBottomEdges = Array.Empty<double>();
            return;
        }

        var visualLines = new List<CodeVisualLineInfo>(_lines.Count);
        foreach (InlineContainerBox chunk in _chunks)
        {
            CanvasLineMetrics[]? metrics = chunk.GetLineMetricsSnapshot();
            if (metrics is null || metrics.Length == 0)
            {
                _visualLines = Array.Empty<CodeVisualLineInfo>();
                _visualLineBottomEdges = Array.Empty<double>();
                return;
            }

            int localOffset = 0;
            double top = chunk.GetTextOriginY();
            foreach (CanvasLineMetrics metric in metrics)
            {
                int globalOffset = chunk.CodeBlockTextOffset + localOffset;
                int logicalLine = FindFirstLineEndingAtOrAfter(globalOffset);
                if (logicalLine >= _lines.Count)
                    logicalLine = _lines.Count - 1;
                double height = Math.Max(1, metric.Height);
                visualLines.Add(new CodeVisualLineInfo(logicalLine, top, height));
                top += metric.Height;
                localOffset += Math.Max(0, metric.CharacterCount);
            }
        }

        _visualLines = visualLines.ToArray();
        _visualLineBottomEdges = new double[_visualLines.Length];
        for (int index = 0; index < _visualLines.Length; index++)
            _visualLineBottomEdges[index] = _visualLines[index].Top + _visualLines[index].Height;
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

        CanvasTextFormat format = _headerTextFormat ??= new CanvasTextFormat
        {
            FontFamily = style.FontFamily,
            FontSize = style.FontSize,
            FontWeight = style.FontWeight,
            FontStyle = style.FontStyle,
            LocaleName = _context.Language,
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

        CanvasTextFormat lineNumberFormat = _lineNumberTextFormat ??= new CanvasTextFormat
        {
            FontFamily = lineNumberStyle.FontFamily,
            FontSize = lineNumberStyle.FontSize,
            FontWeight = lineNumberStyle.FontWeight,
            FontStyle = lineNumberStyle.FontStyle,
            LocaleName = _context.Language,
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

        if (_visualLines.Length > 0)
        {
            int firstVisual = VerticalViewportIndex.FindFirstIntersecting(
                _visualLineBottomEdges,
                viewport.Top);
            int previousLogicalLine = -1;
            for (int visualIndex = firstVisual; visualIndex < _visualLines.Length; visualIndex++)
            {
                CodeVisualLineInfo visual = _visualLines[visualIndex];
                if (visual.Top > viewport.Bottom)
                    break;

                CodeLineInfo line = _lines[visual.LogicalLineIndex];
                Color? bg = line.DiffKind switch
                {
                    CodeLineDiffKind.Added => additionBg,
                    CodeLineDiffKind.Removed => removalBg,
                    _ => Metadata.HighlightedLines.Contains(line.Number) ? highlightBg : null,
                };
                if (bg is { A: > 0 } lineBg)
                {
                    ds.FillRectangle(
                        new Rect(fullLeft, visual.Top, fullRight - fullLeft, visual.Height),
                        lineBg);
                }

                if (line.Number == previousLogicalLine)
                    continue;
                previousLogicalLine = line.Number;

                if (ShowLineNumbers)
                {
                    ds.DrawText(
                        line.Label,
                        new Rect(numberLeft, visual.Top, numberWidth, visual.Height),
                        lineNumberStyle.Foreground,
                        lineNumberFormat);
                }

                if (Metadata.IsDiff && line.DiffKind is not CodeLineDiffKind.None)
                {
                    string marker = line.DiffKind == CodeLineDiffKind.Added ? "+" : "-";
                    Color markerColor = line.DiffKind == CodeLineDiffKind.Added
                        ? Color.FromArgb(0xFF, 0x2E, 0xC2, 0x7E)
                        : Color.FromArgb(0xFF, 0xF8, 0x51, 0x49);
                    ds.DrawText(
                        marker,
                        new Rect(outer.Left + 4, visual.Top, DiffMarkerWidth - 4, visual.Height),
                        _context.ThemeSnapshot.IsHighContrast ? lineNumberStyle.Foreground : markerColor,
                        lineNumberFormat);
                }
            }

            return;
        }

        int firstChunk = FindFirstVisibleChunk(viewport.Top);
        if (firstChunk >= _chunks.Count)
            return;

        int lastChunk = firstChunk;
        while (lastChunk + 1 < _chunks.Count &&
               _chunks[lastChunk + 1].Bounds.Top <= viewport.Bottom)
        {
            lastChunk++;
        }

        int visibleTextStart = _chunks[firstChunk].CodeBlockTextOffset;
        InlineContainerBox finalChunk = _chunks[lastChunk];
        int visibleTextEnd = finalChunk.CodeBlockTextOffset + finalChunk.CodeBlockTextLength;
        int firstLine = FindFirstLineEndingAtOrAfter(visibleTextStart);
        for (int lineIndex = firstLine; lineIndex < _lines.Count; lineIndex++)
        {
            CodeLineInfo line = _lines[lineIndex];
            if (line.Start >= visibleTextEnd)
                break;
            var rects = GetLineRects(line);
            Rect first = Rect.Empty;
            foreach (var rect in rects)
            {
                double top = rect.Top;
                double bottom = rect.Bottom;
                if (bottom < viewport.Top || top > viewport.Bottom)
                    continue;
                if (first.IsEmpty)
                    first = rect;

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
                ds.DrawText(
                    line.Label,
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
        int firstChunk = FindFirstChunkEndingAtOrAfter(lineStart);
        for (int chunkIndex = firstChunk; chunkIndex < _chunks.Count; chunkIndex++)
        {
            InlineContainerBox chunk = _chunks[chunkIndex];
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

    private static List<CodeLineInfo> BuildLines(string code, bool isDiff, int startLine)
    {
        var lines = new List<CodeLineInfo>();
        if (code.Length == 0)
        {
            lines.Add(new CodeLineInfo(
                1,
                0,
                0,
                CodeLineDiffKind.None,
                startLine.ToString(CultureInfo.InvariantCulture)));
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

            lines.Add(new CodeLineInfo(
                number,
                start,
                contentLength,
                diffKind,
                (startLine + number - 1).ToString(CultureInfo.InvariantCulture)));
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

    private readonly record struct CodeLineInfo(
        int Number,
        int Start,
        int Length,
        CodeLineDiffKind DiffKind,
        string Label);

    private readonly record struct CodeVisualLineInfo(
        int LogicalLineIndex,
        double Top,
        double Height);

    private enum CodeLineDiffKind
    {
        None,
        Added,
        Removed,
    }
}
