using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using MarkdownRenderer.CodeBlocks;
using MarkdownRenderer.CodeBlocks.Internal;
using MarkdownRenderer.Document;
using MarkdownRenderer.Theming;

namespace MarkdownRenderer.Layout.Boxes;

/// <summary>
/// A fenced or indented code block: header chrome, an optional gutter, and
/// the code text split into horizontally scrollable inline chunks.
/// </summary>
/// <remarks>
/// This file is longer than 500 lines because it is the single
/// <see cref="BlockBox"/> and <see cref="IHorizontalOverflowBox"/>
/// implementation for a code block: chunk measure/arrange, horizontal
/// scrolling, chrome geometry and disposal share the same arranged-state
/// fields. Painting, hit testing, the gutter, the visual-line index, private
/// placement and the text-format leases live in their own owners.
/// </remarks>
internal sealed class CodeBlockBox : BlockBox, IHorizontalOverflowBox
{
    private const float EnhancedHeaderHeight = 38f;
    private const float HeaderPaddingX = 12f;
    private const float HeaderGap = 8f;
    private const float CopyButtonWidth = 34f;
    private const float ActionHeight = 28f;
    internal const float DiffMarkerWidth = 16f;
    internal const float LineNumberPadding = 10f;
    private const float CodeTextPaddingLeft = 12f;

    internal readonly MarkdownLayoutContext Context;
    private readonly CodeBlockHitTester _hitTester;
    private readonly CodeBlockPainter _painter;
    private readonly List<InlineContainerBox> _chunks = new();
    private CodeBlockHighlightCacheKey? _appliedHighlightKey;
    private double[] _chunkBottomEdges = Array.Empty<double>();
    private readonly List<CodeLineInfo> _lines;
    private readonly CodeBlockVisualLineIndex _visualLineIndex;
    private readonly CodeBlockGutterPainter _gutter;
    private readonly CodeBlockPrivatePlacement _placement;
    private readonly CodeBlockTextFormatLeases _textFormats;
    private readonly IReadOnlyList<string> _styleContextKeys;
    private readonly IReadOnlyList<string> _styleAliasKeys;
    private ElementStyle? _codeStyle;
    private ElementStyle? _headerStyle;
    private ElementStyle? _languageStyle;
    private ElementStyle? _gutterStyle;
    private ElementStyle? _lineNumberStyle;
    private float _contentWidth;
    private float _bodyViewportWidth;
    private double _horizontalOffset;
    private float _arrangedX;
    private float _arrangedY;
    private float _arrangedWidth;
    private bool _hasArrangement;

    internal float HeaderHeight => Context.CodeBlockChromeMode == CodeBlockChromeMode.PlainMarkdown
        ? 0f
        : EnhancedHeaderHeight;

    public CodeBlockBox(
        MarkdownLayoutContext context,
        CodeBlockMetadata metadata,
        string displayedCodeText,
        bool isCopyButtonEnabled,
        bool showLineNumbers)
    {
        Context = context;
        _styleContextKeys = context.CreateStyleContextSnapshot();
        _styleAliasKeys = context.CreateStyleAliasSnapshot();
        Metadata = metadata;
        CodeLanguage = metadata.Language;
        CodeText = CodeBlockMetadata.CopyPayload(displayedCodeText);
        LanguageDisplay = metadata.LanguageDisplay;
        HeaderText = metadata.HeaderText;
        IsCopyButtonEnabled = isCopyButtonEnabled;
        ShowLineNumbers = showLineNumbers;
        _lines = CodeLineInfo.BuildLines(CodeText, metadata.IsDiff, metadata.StartLine);
        _visualLineIndex = new CodeBlockVisualLineIndex(_chunks, _lines, CodeText);
        _gutter = new CodeBlockGutterPainter(this);
        _hitTester = new CodeBlockHitTester(this);
        _painter = new CodeBlockPainter(this);
        _placement = new CodeBlockPrivatePlacement(this, _chunks);
        _textFormats = new CodeBlockTextFormatLeases(context);
        Margin = GetCodeStyle().Margin;
    }

    private CodeBlockBox(MarkdownLayoutContext targetContext, CodeBlockBox source)
    {
        Context = targetContext;
        _styleContextKeys = CodeBlockPrivatePlacement.CopyStyleKeys(source._styleContextKeys);
        _styleAliasKeys = CodeBlockPrivatePlacement.CopyStyleKeys(source._styleAliasKeys);
        Metadata = source.Metadata;
        CodeLanguage = source.CodeLanguage;
        LanguageDisplay = source.LanguageDisplay;
        HeaderText = source.HeaderText;
        CodeText = source.CodeText;
        IsCopyButtonEnabled = source.IsCopyButtonEnabled;
        ShowLineNumbers = source.ShowLineNumbers;
        // Lines are bounded by preflight and contain immutable scalar/string
        // descriptors. Keep a fresh private list so no mutable collection is
        // shared across independently retired snapshots.
        _lines = new List<CodeLineInfo>(source._lines);
        _visualLineIndex = new CodeBlockVisualLineIndex(_chunks, _lines, CodeText);
        _gutter = new CodeBlockGutterPainter(this);
        _hitTester = new CodeBlockHitTester(this);
        _painter = new CodeBlockPainter(this);
        _appliedHighlightKey = source._appliedHighlightKey;
        _horizontalOffset = source._horizontalOffset;
        _placement = new CodeBlockPrivatePlacement(
            this, _chunks, source.PrivatePlacementRevision, source._chunks.Count);
        _textFormats = new CodeBlockTextFormatLeases(targetContext);
        BlockIndex = source.BlockIndex;
        SafeHtmlInlineFlowScopeId = source.SafeHtmlInlineFlowScopeId;
        CopyPlacementMarginsFrom(source);
    }

    public IReadOnlyList<InlineContainerBox> Chunks => _chunks;
    public readonly CodeBlockMetadata Metadata;
    public string StableKey => Metadata.StableKey;
    public readonly string? CodeLanguage;
    public readonly string LanguageDisplay;
    public readonly string HeaderText;
    public readonly string CodeText;
    public int LineCount => _lines.Count;
    internal int IndexedVisualLineCount => _visualLineIndex.Count;
    internal int VisualLineCacheBuildCount => _visualLineIndex.CacheBuildCount;
    internal CodeBlockVisualLineIndex VisualLineIndex => _visualLineIndex;
    internal List<CodeLineInfo> LineTable => _lines;
    internal CodeBlockGutterPainter Gutter => _gutter;
    internal float MeasuredContentWidth => _contentWidth;
    internal float BodyViewportWidth => _bodyViewportWidth;

    /// <summary>The style-context keys captured when this block was built; a clone shell holds its own copy.</summary>
    internal IReadOnlyList<string> StyleContextKeys => _styleContextKeys;

    /// <summary>The style-alias keys captured when this block was built; a clone shell holds its own copy.</summary>
    internal IReadOnlyList<string> StyleAliasKeys => _styleAliasKeys;

    /// <summary>The header and gutter text-format leases this block paints with.</summary>
    internal CodeBlockTextFormatLeases TextFormats => _textFormats;
    public readonly bool IsCopyButtonEnabled;
    internal bool HasCopyButtonAction =>
        IsCopyButtonEnabled && Context.CodeBlockChromeMode != CodeBlockChromeMode.PlainMarkdown;
    public readonly bool ShowLineNumbers;
    public Rect CopyButtonBounds { get; private set; }
    internal FrameworkElement? RealizedCopyButton { get; set; }
    public double HorizontalOffset => _horizontalOffset;
    public double HorizontalExtent => Math.Max(_contentWidth, _bodyViewportWidth);
    public double HorizontalViewport => _bodyViewportWidth;
    public bool CanScrollHorizontally => HorizontalExtent > HorizontalViewport + 0.5;
    public bool IsRightToLeft => Context.FlowDirection == FlowDirection.RightToLeft;

    internal long PrivatePlacementRevision => _placement.Revision;

    internal int PrivatePlacementForegroundSpanCount => _placement.ForegroundSpanCount;

    internal bool IsCurrentPrivatePlacement(long revision) => _placement.IsCurrent(revision);

    /// <summary>
    /// Allocation-free eligibility and budget scan for the immutable shell
    /// inputs owned by a code block; see <see cref="CodeBlockPrivatePlacement.TryGetCounts"/>.
    /// </summary>
    internal bool TryGetPrivatePlacementCounts(
        MarkdownLayoutContext targetContext,
        bool allowUnmeasured,
        out int copiedLineCount,
        out long styleKeyEntries)
        => _placement.TryGetCounts(targetContext, allowUnmeasured, out copiedLineCount, out styleKeyEntries);

    /// <summary>
    /// Creates an unmeasured shell only after bounded source preflight. Native
    /// formats, visual indexes, button elements, chunk owners, and measured
    /// geometry are intentionally fresh on the private candidate.
    /// </summary>
    internal CodeBlockBox CreatePrivatePlacementCloneShell(MarkdownLayoutContext targetContext)
    {
        ArgumentNullException.ThrowIfNull(targetContext);
        targetContext.CancellationToken.ThrowIfCancellationRequested();
        if (!TryGetPrivatePlacementCounts(targetContext, allowUnmeasured: true,
                out _, out _))
            throw new InvalidOperationException("This code block is not eligible for a private placement clone.");
        targetContext.CancellationToken.ThrowIfCancellationRequested();
        return new CodeBlockBox(targetContext, this);
    }

    /// <summary>
    /// Adopts a freshly cloned code-text chunk without resetting the copied
    /// highlight identity or sharing any mutable source chunk state.
    /// </summary>
    internal void AddPrivatePlacementChunk(InlineContainerBox chunk)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        Context.CancellationToken.ThrowIfCancellationRequested();
        _placement.ValidateChunkAdoption(chunk);
        _chunks.Add(chunk);
        _placement.RecordChunkAdopted();
        _visualLineIndex.InvalidateTemplate();
    }

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
            double y = outer.Bottom - style.Padding.Bottom - HorizontalOverflowScrollbar.Height;
            return new Rect(left, y, width, HorizontalOverflowScrollbar.Height);
        }
    }

    public Rect HorizontalScrollThumbBounds => HorizontalOverflowScrollbar.GetThumbBounds(this);

    public void AddChunk(InlineContainerBox chunk)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        _placement.Advance();
        _appliedHighlightKey = null;
        chunk.DrawContainerChrome = false;
        chunk.UseContainerPadding = false;
        chunk.UseContainerMargin = false;
        chunk.Margin = default;
        _chunks.Add(chunk);
        _visualLineIndex.InvalidateTemplate();
    }

    internal bool HasAppliedSyntaxHighlighting(CodeBlockHighlightCacheKey key)
        => _appliedHighlightKey == key;

    internal bool ApplySyntaxHighlighting(
        CodeBlockHighlightCacheKey key,
        IReadOnlyList<CodeBlockHighlightSpan>? spans)
    {
        if (_appliedHighlightKey == key)
            return false;

        _placement.Advance();
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

        _appliedHighlightKey = key;
        return true;
    }

    public override float Measure(float availableWidth)
    {
        _placement.ThrowIfIncompleteCloneShell();
        ThrowIfCancellationRequested();
        var style = GetCodeStyle();
        Margin = style.Margin;

        double innerWidth = Math.Max(1, availableWidth - Margin.Left - Margin.Right);
        double gutterWidth = ComputeGutterWidth(style);
        double codeTextPaddingLeft = gutterWidth > 0 ? CodeTextPaddingLeft : 0;
        _bodyViewportWidth = (float)Math.Max(1, innerWidth - style.Padding.Left - style.Padding.Right - gutterWidth - codeTextPaddingLeft);
        double y = Margin.Top + HeaderHeight + style.Padding.Top;
        _contentWidth = 0;
        _visualLineIndex.ResetSuppressedTrailingEmptyLines();

        for (int chunkIndex = 0; chunkIndex < _chunks.Count; chunkIndex++)
        {
            InlineContainerBox chunk = _chunks[chunkIndex];
            chunk.ThrowIfCancellationRequested();
            float h = chunk.Measure(_bodyViewportWidth);
            chunk.Arrange(
                (float)(Margin.Left + style.Padding.Left + gutterWidth + codeTextPaddingLeft),
                (float)y,
                _bodyViewportWidth);

            // A nonfinal text chunk ending at an authored line break has a
            // zero-character DirectWrite metric for the empty line after that
            // break. The following chunk starts on that same logical row, so
            // retain the raw native layout but do not allocate its duplicate
            // row in the code block's vertical geometry.
            if (_visualLineIndex.TryGetInterChunkTrailingEmptyLineHeight(
                    chunkIndex,
                    chunk,
                    chunk.GetLineMetricsSnapshot(),
                    out float duplicateLineHeight) &&
                duplicateLineHeight <= h &&
                duplicateLineHeight <= chunk.Bounds.Height)
            {
                Rect chunkBounds = chunk.Bounds;
                double effectiveHeight = Math.Max(0, chunkBounds.Height - duplicateLineHeight);
                if (chunk.TrySetMeasuredCodeBlockChunkEffectiveHeight(effectiveHeight))
                {
                    _visualLineIndex.SuppressTrailingEmptyLine(chunkIndex, duplicateLineHeight);
                    h = (float)effectiveHeight;
                }
            }

            _contentWidth = Math.Max(_contentWidth, chunk.ContentWidth);
            y += h;
        }

        double clampedOffset = Math.Clamp(
            _horizontalOffset,
            0,
            Math.Max(0, HorizontalExtent - HorizontalViewport));
        if (clampedOffset != _horizontalOffset)
        {
            _placement.Advance();
            _horizontalOffset = clampedOffset;
        }
        y += style.Padding.Bottom;
        if (CanScrollHorizontally)
            y += HorizontalOverflowScrollbar.Height;
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
        // DirectWrite already positions RTL overflow relative to the native
        // layout's requested viewport width. Applying the content-width shift
        // here as well moves the text twice and can put every glyph outside
        // the code clip. Only translate the native origin by the scroll offset.
        float contentLeft = Context.FlowDirection == FlowDirection.RightToLeft
            ? viewportLeft + (float)_horizontalOffset
            : viewportLeft - (float)_horizontalOffset;
        float y = (float)(_arrangedY + Margin.Top + HeaderHeight + style.Padding.Top);
        foreach (var chunk in _chunks)
        {
            chunk.Arrange(contentLeft, y, _bodyViewportWidth);
            y += (float)chunk.Bounds.Height;
        }
        RefreshChunkBottomEdges();
        _visualLineIndex.RebuildVisualLineIndex();
    }

    internal override void ThrowIfCancellationRequested()
    {
        Context.CancellationToken.ThrowIfCancellationRequested();
        foreach (var chunk in _chunks)
            chunk.ThrowIfCancellationRequested();
    }

    public override void Paint(CanvasDrawingSession ds, Rect viewport)
        => _painter.Paint(ds, viewport);

    public override bool HitTest(Point point, out DocumentPosition position)
        => _hitTester.HitTest(point, out position);

    public override IEnumerable<Rect> GetSelectionRects(DocumentRange range)
        => _hitTester.GetSelectionRects(range);

    public override void PaintSelectionForeground(CanvasDrawingSession ds, DocumentRange range, Windows.UI.Color color, Rect viewport)
        => _painter.PaintSelectionForeground(ds, range, color, viewport);

    public override void Dispose()
    {
        if (!_placement.TryMarkDisposed())
            return;

        _visualLineIndex.Release();

        // Formats are shared immutable descriptors, so release our leases
        // rather than disposing the underlying native formats directly. Keep
        // cleanup failure reporting bounded and still attempt every chunk.
        BlockBoxCleanupScope cleanup = default;
        _textFormats.Release(ref cleanup);
        foreach (InlineContainerBox chunk in _chunks)
            cleanup.TryDispose(chunk);
        cleanup.ThrowIfAny("disposing a code block");
    }

    internal override void CollectRetirementTasks(List<Task> pendingTasks)
    {
        foreach (InlineContainerBox chunk in _chunks)
            chunk.CollectRetirementTasks(pendingTasks);
    }

    internal ElementStyle GetCodeStyle()
        => _codeStyle ??= GetStyle(MarkdownElementKeys.CodeBlock);

    internal ElementStyle GetHeaderStyle()
        => _headerStyle ??= GetStyle(MarkdownElementKeys.CodeBlockHeader);

    internal ElementStyle GetLanguageStyle()
        => _languageStyle ??= GetStyle(MarkdownElementKeys.CodeBlockLanguage);

    internal ElementStyle GetGutterStyle()
        => _gutterStyle ??= GetStyle(MarkdownElementKeys.CodeBlockGutter);

    internal ElementStyle GetLineNumberStyle()
        => _lineNumberStyle ??= GetStyle(MarkdownElementKeys.CodeBlockLineNumber);

    private ElementStyle GetStyle(string elementKey)
        => Context.ThemeSnapshot.GetStyle(
            elementKey,
            _styleContextKeys,
            _styleAliasKeys,
            CodeLanguage,
            Metadata.IsDiff ? "diff" : null);

    internal Rect OuterRect()
        => new(
            Bounds.X + Margin.Left,
            Bounds.Y + Margin.Top,
            Math.Max(0, Bounds.Width - Margin.Left - Margin.Right),
            Math.Max(0, Bounds.Height - Margin.Top - Margin.Bottom));

    internal Rect CodeViewportRect(Rect outer, ElementStyle style)
    {
        double gutterWidth = ComputeGutterWidth(style);
        double codeTextPaddingLeft = gutterWidth > 0 ? CodeTextPaddingLeft : 0;
        double left = outer.Left + style.Padding.Left + gutterWidth + codeTextPaddingLeft;
        double top = outer.Top + HeaderHeight + style.Padding.Top;
        double scrollbarHeight = CanScrollHorizontally ? HorizontalOverflowScrollbar.Height : 0;
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

        _placement.Advance();
        _horizontalOffset = next;
        ArrangeChunks();
        return true;
    }

    internal double ComputeGutterWidth(ElementStyle style)
    {
        double diffWidth = Metadata.IsDiff ? DiffMarkerWidth : 0;
        if (!ShowLineNumbers)
            return diffWidth;

        int lastLine = Math.Max(1, Metadata.StartLine + Math.Max(0, LineCount - 1));
        int digits = 1;
        for (int remaining = lastLine; remaining >= 10; remaining /= 10)
            digits++;
        return diffWidth + LineNumberPadding + Math.Max(2, digits) * Math.Max(6, style.FontSize * 0.56f) + LineNumberPadding;
    }

    internal int FindFirstVisibleChunk(double viewportTop)
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

    internal int FindFirstChunkEndingAtOrAfter(int textOffset)
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

    private void RefreshChunkBottomEdges()
    {
        if (_chunkBottomEdges.Length != _chunks.Count)
            _chunkBottomEdges = new double[_chunks.Count];
        for (int index = 0; index < _chunks.Count; index++)
            _chunkBottomEdges[index] = _chunks[index].Bounds.Bottom;
    }

    internal static bool IsDrawableRectangle(Rect rectangle)
    {
        double right = rectangle.X + rectangle.Width;
        double bottom = rectangle.Y + rectangle.Height;
        return IsCanvasCoordinate(rectangle.X) &&
               IsCanvasCoordinate(rectangle.Y) &&
               IsCanvasCoordinate(rectangle.Width) &&
               IsCanvasCoordinate(rectangle.Height) &&
               IsCanvasCoordinate(right) &&
               IsCanvasCoordinate(bottom) &&
               rectangle.Width > 0 &&
               rectangle.Height > 0;
    }

    private static bool IsCanvasCoordinate(double value) =>
        double.IsFinite(value) && Math.Abs(value) <= float.MaxValue;

    private void UpdateActionBounds()
    {
        var outer = OuterRect();
        if (!HasCopyButtonAction)
        {
            CopyButtonBounds = Rect.Empty;
            return;
        }

        double top = outer.Top + Math.Max(0, (HeaderHeight - ActionHeight) / 2.0);
        bool rtl = Context.FlowDirection == FlowDirection.RightToLeft;
        double x = rtl
            ? outer.Left + HeaderPaddingX
            : outer.Right - HeaderPaddingX - CopyButtonWidth;
        CopyButtonBounds = new Rect(x, top, CopyButtonWidth, ActionHeight);
    }

    internal void DrawHeaderText(CanvasDrawingSession ds, Rect outer, ElementStyle style)
    {
        bool rtl = Context.FlowDirection == FlowDirection.RightToLeft;
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

        CanvasTextFormat format = _textFormats.GetHeaderFormat(style);

        ds.DrawText(HeaderText, new Rect(left, outer.Top, right - left, HeaderHeight), style.Foreground, format);
    }

    internal CanvasTextFormat GetLineNumberTextFormat(ElementStyle style)
        => _textFormats.GetLineNumberFormat(style);

    internal static Rect Intersect(Rect a, Rect b)
    {
        double left = Math.Max(a.Left, b.Left);
        double top = Math.Max(a.Top, b.Top);
        double right = Math.Min(a.Right, b.Right);
        double bottom = Math.Min(a.Bottom, b.Bottom);
        if (right <= left || bottom <= top)
            return Rect.Empty;

        // A horizontal-only clip must not perturb DirectWrite's original
        // vertical extent (and vice versa). Reconstructing an untouched axis
        // from its absolute edges can lose low bits when the origin is large.
        double width = left == a.Left && right == a.Right
            ? a.Width
            : right - left;
        double height = top == a.Top && bottom == a.Bottom
            ? a.Height
            : bottom - top;
        return new Rect(left, top, width, height);
    }

    internal static Windows.UI.Color WithAlpha(Windows.UI.Color color, byte alpha)
        => Windows.UI.Color.FromArgb(alpha, color.R, color.G, color.B);

}
