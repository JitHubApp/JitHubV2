using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
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
    private const float EnhancedHeaderHeight = 38f;
    private const float HeaderPaddingX = 12f;
    private const float HeaderGap = 8f;
    private const float CopyButtonWidth = 34f;
    private const float ActionHeight = 28f;
    private const float DiffMarkerWidth = 16f;
    private const float LineNumberPadding = 10f;
    private const float CodeTextPaddingLeft = 12f;
    private const float HorizontalScrollbarHeight = 12f;
    private const float MinimumScrollbarThumbWidth = 24f;

    internal readonly MarkdownLayoutContext Context;
    private readonly List<InlineContainerBox> _chunks = new();
    private CodeBlockHighlightCacheKey? _appliedHighlightKey;
    private double[] _chunkBottomEdges = Array.Empty<double>();
    private float[] _suppressedTrailingEmptyLineHeights = Array.Empty<float>();
    private CodeVisualLineInfo[] _visualLines = Array.Empty<CodeVisualLineInfo>();
    private double[] _visualLineBottomEdges = Array.Empty<double>();
    private VisualLineChunkCache[] _visualLineChunkCache = Array.Empty<VisualLineChunkCache>();
    private bool _visualLineTemplateValid;
    private int _visualLineCacheBuildCount;
    private readonly List<CodeLineInfo> _lines;
    private readonly IReadOnlyList<string> _styleContextKeys;
    private readonly IReadOnlyList<string> _styleAliasKeys;
    private ElementStyle? _codeStyle;
    private ElementStyle? _headerStyle;
    private ElementStyle? _languageStyle;
    private ElementStyle? _gutterStyle;
    private ElementStyle? _lineNumberStyle;
    private SharedCanvasTextFormatCache.Lease? _headerTextFormatLease;
    private SharedCanvasTextFormatCache.Lease? _lineNumberTextFormatLease;
    private float _contentWidth;
    private float _bodyViewportWidth;
    private double _horizontalOffset;
    private float _arrangedX;
    private float _arrangedY;
    private float _arrangedWidth;
    private bool _hasArrangement;
    private long _placementRevision;
    private int _placementDisposed;
    private bool _isPrivatePlacementCloneShell;
    private bool _privatePlacementCloneComplete;
    private int _privatePlacementExpectedChunkCount;

    private float HeaderHeight => Context.CodeBlockChromeMode == CodeBlockChromeMode.PlainMarkdown
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
        _lines = BuildLines(CodeText, metadata.IsDiff, metadata.StartLine);
        Margin = GetCodeStyle().Margin;
    }

    private CodeBlockBox(MarkdownLayoutContext targetContext, CodeBlockBox source)
    {
        Context = targetContext;
        _styleContextKeys = CopyStyleKeys(source._styleContextKeys);
        _styleAliasKeys = CopyStyleKeys(source._styleAliasKeys);
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
        _appliedHighlightKey = source._appliedHighlightKey;
        _horizontalOffset = source._horizontalOffset;
        _placementRevision = source.PrivatePlacementRevision;
        _isPrivatePlacementCloneShell = true;
        _privatePlacementExpectedChunkCount = source._chunks.Count;
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
    internal int IndexedVisualLineCount => _visualLines.Length;
    internal int VisualLineCacheBuildCount => _visualLineCacheBuildCount;
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

    internal long PrivatePlacementRevision => Interlocked.Read(ref _placementRevision);

    internal int PrivatePlacementForegroundSpanCount
    {
        get
        {
            int count = 0;
            foreach (InlineContainerBox chunk in _chunks)
            {
                int chunkCount = chunk.PrivateRasterPlacementForegroundSpanCount;
                if (chunkCount > int.MaxValue - count)
                    return int.MaxValue;
                count += chunkCount;
            }
            return count;
        }
    }

    internal bool IsCurrentPrivatePlacement(long revision)
        => Volatile.Read(ref _placementDisposed) == 0 && PrivatePlacementRevision == revision;

    /// <summary>
    /// Allocation-free eligibility and budget scan for the immutable shell
    /// inputs owned by a code block. Chunk run/image descriptors are scanned
    /// recursively by PrivateRasterPlacementTree and charged only there.
    /// </summary>
    internal bool TryGetPrivatePlacementCounts(
        MarkdownLayoutContext targetContext,
        bool allowUnmeasured,
        out int copiedLineCount,
        out long styleKeyEntries)
    {
        ArgumentNullException.ThrowIfNull(targetContext);
        copiedLineCount = 0;
        styleKeyEntries = 0;
        targetContext.CancellationToken.ThrowIfCancellationRequested();
        if (!IsPrivatePlacementShellEligible(targetContext) ||
            (!allowUnmeasured && IsDirty) ||
            BlockIndex <= 0 ||
            _lines.Count is 0 or > PrivateRasterPlacementTree.MaximumNodes ||
            _chunks.Count is 0 or > PrivateRasterPlacementTree.MaximumNodes ||
            !TryAccumulatePrivateStyleKeys(ref styleKeyEntries, _styleContextKeys) ||
            !TryAccumulatePrivateStyleKeys(ref styleKeyEntries, _styleAliasKeys))
            return false;

        int expectedOffset = 0;
        foreach (InlineContainerBox chunk in _chunks)
        {
            targetContext.CancellationToken.ThrowIfCancellationRequested();
            if (chunk is null ||
                !ReferenceEquals(chunk.Context, Context) ||
                chunk.ElementKey != MarkdownElementKeys.CodeBlock ||
                chunk.SafeHtmlInlineFlowScopeId is not null ||
                chunk.CodeBlockTextOffset != expectedOffset ||
                chunk.CodeBlockTextLength < 0 ||
                chunk.CodeBlockTextLength > CodeText.Length - expectedOffset ||
                chunk.CodeLanguage != CodeLanguage ||
                chunk.BlockIndex <= 0 ||
                chunk.Runs.Count != 1 ||
                chunk.Runs[0] is not TextRun textRun ||
                textRun.Text.Length != chunk.CodeBlockTextLength ||
                !CodeText.AsSpan(expectedOffset, chunk.CodeBlockTextLength)
                    .SequenceEqual(textRun.Text.AsSpan()) ||
                !chunk.TryGetPrivateRasterPlacementCounts(
                    targetContext,
                    Math.Max(1f, (float)chunk.Bounds.Width),
                    out _, out _, out _,
                    allowUnmeasuredTextOnly: allowUnmeasured,
                    allowIdleImagePlacements: false,
                    allowCodeForegroundSpans: true))
                return false;

            expectedOffset += chunk.CodeBlockTextLength;
        }

        if (expectedOffset != CodeText.Length ||
            !ContextMatchesCodePlacementTarget(targetContext))
            return false;

        copiedLineCount = _lines.Count;
        return true;
    }

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
        int expectedOffset = _chunks.Count == 0
            ? 0
            : checked(_chunks[^1].CodeBlockTextOffset + _chunks[^1].CodeBlockTextLength);
        if (_chunks.Count >= PrivateRasterPlacementTree.MaximumNodes ||
            !_isPrivatePlacementCloneShell || _privatePlacementCloneComplete ||
            _chunks.Count >= _privatePlacementExpectedChunkCount ||
            !ReferenceEquals(chunk.Context, Context) ||
            chunk.ElementKey != MarkdownElementKeys.CodeBlock ||
            chunk.SafeHtmlInlineFlowScopeId is not null ||
            chunk.CodeBlockTextOffset != expectedOffset ||
            chunk.CodeBlockTextLength < 0 ||
            chunk.CodeBlockTextLength > CodeText.Length - expectedOffset ||
            chunk.CodeLanguage != CodeLanguage ||
            chunk.BlockIndex <= 0 ||
            chunk.Runs.Count != 1 ||
            chunk.Runs[0] is not TextRun textRun ||
            textRun.Text.Length != chunk.CodeBlockTextLength ||
            !CodeText.AsSpan(expectedOffset, chunk.CodeBlockTextLength)
                .SequenceEqual(textRun.Text.AsSpan()))
            throw new InvalidOperationException("A code chunk does not match its private code block shell.");

        if (_chunks.Count + 1 == _privatePlacementExpectedChunkCount &&
            expectedOffset + chunk.CodeBlockTextLength != CodeText.Length)
            throw new InvalidOperationException("The private code chunks do not cover their complete text payload.");

        _chunks.Add(chunk);
        _privatePlacementCloneComplete = _chunks.Count == _privatePlacementExpectedChunkCount;
        _visualLineTemplateValid = false;
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
            bool rtl = Context.FlowDirection == FlowDirection.RightToLeft;
            double x = rtl
                ? track.Right - thumbWidth - travel * fraction
                : track.Left + travel * fraction;
            return new Rect(x, track.Y, thumbWidth, track.Height);
        }
    }

    public void AddChunk(InlineContainerBox chunk)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        AdvancePrivatePlacementRevision();
        _appliedHighlightKey = null;
        chunk.DrawContainerChrome = false;
        chunk.UseContainerPadding = false;
        chunk.UseContainerMargin = false;
        chunk.Margin = default;
        _chunks.Add(chunk);
        _visualLineTemplateValid = false;
    }

    internal bool HasAppliedSyntaxHighlighting(CodeBlockHighlightCacheKey key)
        => _appliedHighlightKey == key;

    internal bool ApplySyntaxHighlighting(
        CodeBlockHighlightCacheKey key,
        IReadOnlyList<CodeBlockHighlightSpan>? spans)
    {
        if (_appliedHighlightKey == key)
            return false;

        AdvancePrivatePlacementRevision();
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
        if (_isPrivatePlacementCloneShell && !_privatePlacementCloneComplete)
            throw new InvalidOperationException("A private code block cannot be measured before all chunks are adopted.");
        ThrowIfCancellationRequested();
        var style = GetCodeStyle();
        Margin = style.Margin;

        double innerWidth = Math.Max(1, availableWidth - Margin.Left - Margin.Right);
        double gutterWidth = ComputeGutterWidth(style);
        double codeTextPaddingLeft = gutterWidth > 0 ? CodeTextPaddingLeft : 0;
        _bodyViewportWidth = (float)Math.Max(1, innerWidth - style.Padding.Left - style.Padding.Right - gutterWidth - codeTextPaddingLeft);
        double y = Margin.Top + HeaderHeight + style.Padding.Top;
        _contentWidth = 0;
        if (_suppressedTrailingEmptyLineHeights.Length != _chunks.Count)
            _suppressedTrailingEmptyLineHeights = new float[_chunks.Count];
        else
            Array.Clear(_suppressedTrailingEmptyLineHeights);

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
            if (TryGetInterChunkTrailingEmptyLineHeight(
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
                    _suppressedTrailingEmptyLineHeights[chunkIndex] = duplicateLineHeight;
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
            AdvancePrivatePlacementRevision();
            _horizontalOffset = clampedOffset;
        }
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
        RebuildVisualLineIndex();
    }

    internal override void ThrowIfCancellationRequested()
    {
        Context.CancellationToken.ThrowIfCancellationRequested();
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

        bool hasEnhancedChrome = Context.CodeBlockChromeMode != CodeBlockChromeMode.PlainMarkdown;
        if (hasEnhancedChrome && headerStyle.Background is { } headerBg)
        {
            ds.FillRoundedRectangle(outer, radius, radius, headerBg);
            if (codeStyle.Background is { } bodyBg)
            {
                ds.FillRectangle(
                    new Rect(outer.X, outer.Y + HeaderHeight, outer.Width, Math.Max(0, outer.Height - HeaderHeight)),
                    bodyBg);
            }
        }

        if (hasEnhancedChrome)
        {
            var separator = codeStyle.BorderBrush ?? headerStyle.BorderBrush ?? languageStyle.Foreground;
            ds.DrawLine(
                (float)outer.Left,
                (float)(outer.Top + HeaderHeight),
                (float)outer.Right,
                (float)(outer.Top + HeaderHeight),
                Context.ThemeSnapshot.IsHighContrast
                    ? separator
                    : WithAlpha(separator, Math.Min(separator.A, (byte)0x60)),
                1f);

            if (outer.Left < viewport.Right && outer.Right > viewport.Left &&
                outer.Top < viewport.Bottom && outer.Top + HeaderHeight > viewport.Top)
            {
                DrawHeaderText(ds, outer, languageStyle);
            }
        }
        DrawBodySurfaces(ds, outer, codeStyle, headerStyle, languageStyle, viewport);

        var clip = CodeViewportRect(outer, codeStyle);
        // A CanvasActiveLayer is a comparatively expensive projected/native
        // allocation. The tile already clips vertically and measured chunks
        // remain inside the body when the code fits horizontally, so create a
        // local clip only for a genuinely overflowing no-wrap block.
        if (_contentWidth > _bodyViewportWidth + 0.5f)
        {
            NativePaintSessionResources.LayerLease layer = NativePaintSessionResources.CreateLayer(ds, 1.0f, clip);
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
            Point clippedPoint = new(clampedX, point.Y);
            bool clippedHorizontalHit = codeViewport.Contains(clippedPoint) &&
                _contentWidth > _bodyViewportWidth + 0.5f;
            int firstVisible = FindFirstVisibleChunk(point.Y);
            for (int index = firstVisible; index < _chunks.Count; index++)
            {
                InlineContainerBox chunk = _chunks[index];
                if (chunk.Bounds.Top > point.Y)
                    break;
                if (point.Y < chunk.Bounds.Top || point.Y > chunk.Bounds.Bottom)
                    continue;

                if (chunk.HitTest(clippedPoint, out position))
                    return true;

                // A no-wrap chunk may be translated beyond its logical bounds
                // by RTL horizontal scrolling while its text is still visible
                // through CodeViewportRect. Only this parent-owned clipped
                // viewport may bypass the child container's X bounds check.
                if (clippedHorizontalHit &&
                    chunk.HitTestWithinHorizontalViewport(clippedPoint, out position))
                {
                    return true;
                }
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
        NativePaintSessionResources.LayerLease layer = NativePaintSessionResources.CreateLayer(ds, 1.0f, clip);
        try
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
        catch (Exception primary)
        {
            layer.ClosePreservingPrimary(primary);
            throw;
        }
        layer.Close();
    }

    public override void Dispose()
    {
        if (Interlocked.Exchange(ref _placementDisposed, 1) != 0)
            return;

        ClearVisualLineIndex();
        _visualLineChunkCache = Array.Empty<VisualLineChunkCache>();

        // Formats are shared immutable descriptors, so release our leases
        // rather than disposing the underlying native formats directly. Keep
        // cleanup failure reporting bounded and still attempt every chunk.
        BlockBoxCleanupScope cleanup = default;
        cleanup.TryDisposeAndClear(ref _headerTextFormatLease);
        cleanup.TryDisposeAndClear(ref _lineNumberTextFormatLease);
        foreach (InlineContainerBox chunk in _chunks)
            cleanup.TryDispose(chunk);
        cleanup.ThrowIfAny("disposing a code block");
    }

    internal override void CollectRetirementTasks(List<Task> pendingTasks)
    {
        foreach (InlineContainerBox chunk in _chunks)
            chunk.CollectRetirementTasks(pendingTasks);
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
        => Context.ThemeSnapshot.GetStyle(
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

        AdvancePrivatePlacementRevision();
        _horizontalOffset = next;
        ArrangeChunks();
        return true;
    }

    private bool IsPrivatePlacementShellEligible(MarkdownLayoutContext targetContext)
        => SafeHtmlInlineFlowScopeId is null &&
           InlineContainerBox.ArePrivateRasterPlacementContextsCompatible(Context, targetContext) &&
           IsCopyButtonEnabled == targetContext.IsCodeBlockCopyEnabled;

    private bool ContextMatchesCodePlacementTarget(MarkdownLayoutContext targetContext)
        => IsPrivatePlacementShellEligible(targetContext) &&
           Context.IsCodeBlockCopyEnabled == targetContext.IsCodeBlockCopyEnabled &&
           Context.CodeBlockChromeMode == targetContext.CodeBlockChromeMode &&
           Context.CodeBlockLineNumberMode == targetContext.CodeBlockLineNumberMode &&
           Context.CodeBlockWrappingMode == targetContext.CodeBlockWrappingMode;

    private static bool TryAccumulatePrivateStyleKeys(ref long total, IReadOnlyList<string>? keys)
    {
        if (keys is null || keys.Count < 0 ||
            total > InlineContainerBox.MaximumPrivateRasterPlacementStyleKeyCopyEntries - keys.Count)
            return false;
        total += keys.Count;
        return true;
    }

    private static string[] CopyStyleKeys(IReadOnlyList<string> source)
    {
        var copy = new string[source.Count];
        for (int index = 0; index < copy.Length; index++)
            copy[index] = source[index];
        return copy;
    }

    private void AdvancePrivatePlacementRevision()
    {
        while (true)
        {
            long current = Interlocked.Read(ref _placementRevision);
            if (current == long.MaxValue)
                throw new InvalidOperationException("The code-block placement revision is exhausted.");
            if (Interlocked.CompareExchange(ref _placementRevision, current + 1, current) == current)
                return;
        }
    }

    private void PaintHorizontalScrollbar(CanvasDrawingSession ds, ElementStyle style)
    {
        HorizontalOverflowVisual.Paint(
            ds,
            HorizontalScrollTrackBounds,
            HorizontalScrollThumbBounds,
            Context.ThemeSnapshot,
            style.Foreground);
    }

    private double ComputeGutterWidth(ElementStyle style)
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
            ClearVisualLineIndex();
            return;
        }

        if (_visualLineChunkCache.Length != _chunks.Count)
        {
            _visualLineChunkCache = new VisualLineChunkCache[_chunks.Count];
            _visualLineTemplateValid = false;
        }

        bool previousTemplateValid = _visualLineTemplateValid &&
                                     _visualLines.Length == _visualLineBottomEdges.Length;
        _visualLineTemplateValid = false;
        bool inputsMatch = previousTemplateValid;
        bool allOriginsUnchanged = previousTemplateValid;

        for (int index = 0; index < _chunks.Count; index++)
        {
            InlineContainerBox chunk = _chunks[index];
            CanvasLineMetrics[]? metrics = chunk.GetLineMetricsSnapshot();
            if (metrics is null || metrics.Length == 0)
            {
                ClearVisualLineIndex();
                return;
            }
            double originY = chunk.GetTextOriginY();
            if (!double.IsFinite(originY))
            {
                ClearVisualLineIndex();
                return;
            }

            VisualLineChunkCache previous = _visualLineChunkCache[index];
            int lineCount = GetEffectiveNativeLineCount(index, chunk, metrics);
            bool inputMatches = previousTemplateValid &&
                                ReferenceEquals(previous.Chunk, chunk) &&
                                ReferenceEquals(previous.Metrics, metrics) &&
                                previous.TextOffset == chunk.CodeBlockTextOffset &&
                                previous.TextLength == chunk.CodeBlockTextLength &&
                                previous.LineCount == lineCount;
            bool originUnchanged = inputMatches && previous.OriginY == originY;
            inputsMatch &= inputMatches;
            allOriginsUnchanged &= originUnchanged;

            _visualLineChunkCache[index] = new VisualLineChunkCache
            {
                Chunk = chunk,
                Metrics = metrics,
                TextOffset = chunk.CodeBlockTextOffset,
                TextLength = chunk.CodeBlockTextLength,
                OriginY = originY,
                StartIndex = previous.StartIndex,
                LineCount = lineCount,
                OriginUnchanged = originUnchanged,
            };
        }

        if (inputsMatch && _visualLines.Length > 0)
        {
            if (allOriginsUnchanged)
            {
                _visualLineTemplateValid = true;
                return;
            }

            if (!TryRefreshVisualLineOrigins())
            {
                ClearVisualLineIndex();
                return;
            }

            _visualLineTemplateValid = true;
            return;
        }

        if (!TryBuildVisualLineIndex())
        {
            ClearVisualLineIndex();
            return;
        }

        _visualLineTemplateValid = true;
        if (_visualLineCacheBuildCount < int.MaxValue)
            _visualLineCacheBuildCount++;
    }

    private bool TryBuildVisualLineIndex()
    {
        int totalLineCount = 0;
        for (int chunkIndex = 0; chunkIndex < _visualLineChunkCache.Length; chunkIndex++)
        {
            VisualLineChunkCache chunk = _visualLineChunkCache[chunkIndex];
            CanvasLineMetrics[] metrics = chunk.Metrics!;
            if (chunk.LineCount > int.MaxValue - totalLineCount)
                return false;
            totalLineCount += chunk.LineCount;

            double top = chunk.OriginY;
            long localOffset = 0;
            for (int metricIndex = 0; metricIndex < chunk.LineCount; metricIndex++)
            {
                CanvasLineMetrics metric = metrics[metricIndex];
                if (!double.IsFinite(metric.Height))
                    return false;

                double height = Math.Max(1, metric.Height);
                if (!double.IsFinite(top + height))
                    return false;
                top += height;

                int characterCount = Math.Max(0, metric.CharacterCount);
                if (characterCount > int.MaxValue - localOffset ||
                    (long)chunk.TextOffset + localOffset > int.MaxValue)
                    return false;
                localOffset += characterCount;
            }
        }

        if (totalLineCount == 0)
            return false;

        var visualLines = new CodeVisualLineInfo[totalLineCount];
        var bottomEdges = new double[totalLineCount];
        int visualIndex = 0;
        for (int chunkIndex = 0; chunkIndex < _visualLineChunkCache.Length; chunkIndex++)
        {
            VisualLineChunkCache chunk = _visualLineChunkCache[chunkIndex];
            CanvasLineMetrics[] metrics = chunk.Metrics!;
            double top = chunk.OriginY;
            long localOffset = 0;
            int startIndex = visualIndex;
            for (int metricIndex = 0; metricIndex < chunk.LineCount; metricIndex++)
            {
                CanvasLineMetrics metric = metrics[metricIndex];
                int globalOffset = (int)((long)chunk.TextOffset + localOffset);
                int logicalLine = FindFirstLineEndingAtOrAfter(globalOffset);
                if (logicalLine >= _lines.Count)
                    logicalLine = _lines.Count - 1;

                double height = Math.Max(1, metric.Height);
                visualLines[visualIndex] = new CodeVisualLineInfo(logicalLine, top, height);
                bottomEdges[visualIndex] = top + height;
                visualIndex++;
                top += height;
                localOffset += Math.Max(0, metric.CharacterCount);
            }

            chunk.StartIndex = startIndex;
            _visualLineChunkCache[chunkIndex] = chunk;
        }

        _visualLines = visualLines;
        _visualLineBottomEdges = bottomEdges;
        return true;
    }

    private int GetEffectiveNativeLineCount(
        int chunkIndex,
        InlineContainerBox chunk,
        CanvasLineMetrics[] metrics)
    {
        if (!TryGetInterChunkTrailingEmptyLineHeight(chunkIndex, chunk, metrics, out float lineHeight) ||
            chunkIndex >= _suppressedTrailingEmptyLineHeights.Length ||
            _suppressedTrailingEmptyLineHeights[chunkIndex] != lineHeight)
        {
            return metrics.Length;
        }

        return metrics.Length - 1;
    }

    private bool TryGetInterChunkTrailingEmptyLineHeight(
        int chunkIndex,
        InlineContainerBox chunk,
        CanvasLineMetrics[]? metrics,
        out float height)
    {
        height = 0;
        if (chunkIndex < 0 ||
            chunkIndex >= _chunks.Count - 1 ||
            chunk.CodeBlockTextOffset < 0 ||
            chunk.CodeBlockTextLength <= 0 ||
            chunk.CodeBlockTextLength > CodeText.Length - chunk.CodeBlockTextOffset ||
            chunk.CodeBlockTextOffset + chunk.CodeBlockTextLength !=
                _chunks[chunkIndex + 1].CodeBlockTextOffset ||
            _chunks[chunkIndex + 1].CodeBlockTextLength <= 0 ||
            metrics is not { Length: > 1 } ||
            CodeText[chunk.CodeBlockTextOffset + chunk.CodeBlockTextLength - 1] != '\n')
        {
            return false;
        }

        CanvasLineMetrics trailing = metrics[^1];
        if (trailing.CharacterCount != 0 ||
            !float.IsFinite(trailing.Height) ||
            trailing.Height < 1f)
        {
            return false;
        }

        height = trailing.Height;
        return true;
    }

    private bool TryRefreshVisualLineOrigins()
    {
        for (int chunkIndex = 0; chunkIndex < _visualLineChunkCache.Length; chunkIndex++)
        {
            VisualLineChunkCache chunk = _visualLineChunkCache[chunkIndex];
            if (chunk.OriginUnchanged)
                continue;

            double top = chunk.OriginY;
            if (chunk.StartIndex < 0 || chunk.StartIndex > _visualLines.Length ||
                chunk.LineCount > _visualLines.Length - chunk.StartIndex)
                return false;
            int endIndex = chunk.StartIndex + chunk.LineCount;
            for (int index = chunk.StartIndex; index < endIndex; index++)
            {
                double height = _visualLines[index].Height;
                if (!double.IsFinite(top + height))
                    return false;
                top += height;
            }
        }

        for (int chunkIndex = 0; chunkIndex < _visualLineChunkCache.Length; chunkIndex++)
        {
            VisualLineChunkCache chunk = _visualLineChunkCache[chunkIndex];
            if (chunk.OriginUnchanged)
                continue;

            double top = chunk.OriginY;
            int endIndex = chunk.StartIndex + chunk.LineCount;
            for (int index = chunk.StartIndex; index < endIndex; index++)
            {
                CodeVisualLineInfo old = _visualLines[index];
                _visualLines[index] = new CodeVisualLineInfo(old.LogicalLineIndex, top, old.Height);
                _visualLineBottomEdges[index] = top + old.Height;
                top += old.Height;
            }
        }

        return true;
    }

    private void ClearVisualLineIndex()
    {
        _visualLineTemplateValid = false;
        _visualLines = Array.Empty<CodeVisualLineInfo>();
        _visualLineBottomEdges = Array.Empty<double>();
        Array.Clear(_visualLineChunkCache, 0, _visualLineChunkCache.Length);
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

    private void DrawHeaderText(CanvasDrawingSession ds, Rect outer, ElementStyle style)
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

        CanvasTextFormat format = (_headerTextFormatLease ??= SharedCanvasTextFormatCache.Acquire(
            style,
            CanvasWordWrapping.NoWrap,
            rtl ? CanvasHorizontalAlignment.Right : CanvasHorizontalAlignment.Left,
            Context.FlowDirection,
            Context.Language,
            useContentAwareLineSpacing: true,
            verticalAlignment: CanvasVerticalAlignment.Center)).Format;

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
                Context.ThemeSnapshot.IsHighContrast ? sep : WithAlpha(sep, 0x40),
                1f);
        }

        DrawLineDecorations(ds, outer, codeStyle, lineNumberStyle, viewport);
    }

    private void DrawLineDecorations(CanvasDrawingSession ds, Rect outer, ElementStyle codeStyle, ElementStyle lineNumberStyle, Rect viewport)
    {
        double gutterWidth = ComputeGutterWidth(codeStyle);
        if (_lines.Count == 0)
            return;

        var textViewport = CodeViewportRect(outer, codeStyle);
        double fullLeft = outer.Left;
        double fullRight = outer.Right;
        double numberLeft = outer.Left + (Metadata.IsDiff ? DiffMarkerWidth : 0);
        double numberWidth = Math.Max(0, gutterWidth - (Metadata.IsDiff ? DiffMarkerWidth : 0) - LineNumberPadding);
        var additionBg = Context.ThemeSnapshot.IsHighContrast
            ? Color.FromArgb(0x00, 0, 0, 0)
            : Color.FromArgb(0x24, 0x2E, 0xC2, 0x7E);
        var removalBg = Context.ThemeSnapshot.IsHighContrast
            ? Color.FromArgb(0x00, 0, 0, 0)
            : Color.FromArgb(0x24, 0xF8, 0x51, 0x49);
        var highlightBg = Context.ThemeSnapshot.IsHighContrast
            ? Color.FromArgb(0x00, 0, 0, 0)
            : Color.FromArgb(0x26, 0xFF, 0xD8, 0x66);

        if (_visualLines.Length > 0)
        {
            int firstVisual = VerticalViewportIndex.FindFirstIntersecting(
                _visualLineBottomEdges,
                viewport.Top);
            if (firstVisual >= _visualLines.Length || _visualLines[firstVisual].Top > viewport.Bottom)
                return;

            CanvasTextFormat? lineNumberFormat = ShowLineNumbers || Metadata.IsDiff
                ? GetLineNumberTextFormat(lineNumberStyle)
                : null;
            int previousLogicalLine = -1;
            for (int visualIndex = firstVisual; visualIndex < _visualLines.Length; visualIndex++)
            {
                CodeVisualLineInfo visual = _visualLines[visualIndex];
                if (visual.Top > viewport.Bottom)
                    break;

                CodeLineInfo line = _lines[visual.LogicalLineIndex];
                Color? bg = GetLineBackground(line, additionBg, removalBg, highlightBg);
                if (bg is { A: > 0 } lineBg)
                {
                    var backgroundRect = new Rect(
                        fullLeft,
                        visual.Top,
                        fullRight - fullLeft,
                        visual.Height);
                    if (IsDrawableRectangle(backgroundRect))
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

            return;
        }

        int firstChunk = FindFirstVisibleChunk(viewport.Top);
        if (firstChunk >= _chunks.Count)
            return;

        CanvasTextFormat? fallbackLineNumberFormat = ShowLineNumbers || Metadata.IsDiff
            ? GetLineNumberTextFormat(lineNumberStyle)
            : null;

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
            Color? bg = GetLineBackground(line, additionBg, removalBg, highlightBg);
            var rects = GetLineRects(line);
            Rect first = Rect.Empty;
            foreach (var rect in rects)
            {
                if (!IsDrawableRectangle(rect))
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
                        fullLeft,
                        top,
                        fullRight - fullLeft,
                        Math.Max(1, bottom - top));
                    if (IsDrawableRectangle(backgroundRect))
                        ds.FillRectangle(backgroundRect, lineBg);
                }
            }

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

    private CanvasTextFormat GetLineNumberTextFormat(ElementStyle style)
        => (_lineNumberTextFormatLease ??= SharedCanvasTextFormatCache.Acquire(
            style,
            CanvasWordWrapping.NoWrap,
            CanvasHorizontalAlignment.Right,
            FlowDirection.LeftToRight,
            Context.Language,
            useContentAwareLineSpacing: true,
            verticalAlignment: CanvasVerticalAlignment.Top)).Format;

    private Color? GetLineBackground(
        CodeLineInfo line,
        Color addition,
        Color removal,
        Color highlight) => line.DiffKind switch
    {
        CodeLineDiffKind.Added => addition,
        CodeLineDiffKind.Removed => removal,
        _ => Metadata.HighlightedLines.Contains(line.Number) ? highlight : null,
    };

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
        if (ShowLineNumbers)
        {
            var numberRect = new Rect(numberLeft, top, numberWidth, height);
            if (IsDrawableRectangle(numberRect))
                DrawLineNumber(ds, line, numberRect, viewport, lineNumberStyle, lineNumberFormat);
        }

        if (!Metadata.IsDiff || line.DiffKind is CodeLineDiffKind.None)
            return;

        string marker = line.DiffKind == CodeLineDiffKind.Added ? "+" : "-";
        Color markerColor = line.DiffKind == CodeLineDiffKind.Added
            ? Color.FromArgb(0xFF, 0x2E, 0xC2, 0x7E)
            : Color.FromArgb(0xFF, 0xF8, 0x51, 0x49);
        var markerRect = new Rect(outer.Left + 4, top, DiffMarkerWidth - 4, height);
        if (IsDrawableRectangle(markerRect))
        {
            DrawGutterText(
                ds, marker, line.Number, "diff marker", markerRect, viewport,
                Context.ThemeSnapshot.IsHighContrast ? lineNumberStyle.Foreground : markerColor,
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
                IsDrawableRectangle(rectangle))
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

    private static Windows.UI.Color WithAlpha(Windows.UI.Color color, byte alpha)
        => Windows.UI.Color.FromArgb(alpha, color.R, color.G, color.B);

    private readonly struct CodeLineInfo(
        int number,
        int start,
        int length,
        CodeLineDiffKind diffKind,
        string label)
    {
        public readonly int Number = number;
        public readonly int Start = start;
        public readonly int Length = length;
        public readonly CodeLineDiffKind DiffKind = diffKind;
        public readonly string Label = label;
    }

    private readonly struct CodeVisualLineInfo(
        int logicalLineIndex,
        double top,
        double height)
    {
        public readonly int LogicalLineIndex = logicalLineIndex;
        public readonly double Top = top;
        public readonly double Height = height;
    }

    private struct VisualLineChunkCache
    {
        public InlineContainerBox? Chunk;
        public CanvasLineMetrics[]? Metrics;
        public int TextOffset;
        public int TextLength;
        public double OriginY;
        public int StartIndex;
        public int LineCount;
        public bool OriginUnchanged;
    }

    private enum CodeLineDiffKind
    {
        None,
        Added,
        Removed,
    }
}
