using System;
using System.Collections.Generic;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Windows.UI;
using MarkdownRenderer.Document;
using MarkdownRenderer.Theming;

namespace MarkdownRenderer.Layout.Boxes;

/// <summary>
/// Block hosting a sequence of inline runs (paragraph or heading body).
/// Owns a <see cref="CanvasTextLayout"/> built from a single concatenated buffer
/// with per-run style spans.
/// </summary>
public sealed class InlineContainerBox : BlockBox
{
    private readonly List<InlineRun> _runs = new();
    private readonly string _elementKey;
    private CanvasTextLayout? _layout;
    private string _buffer = string.Empty;
    private bool _bufferDirty;
    private float _lastWidth;
    private readonly MarkdownLayoutContext _context;

    /// <summary>Run currently being hovered by the pointer (for link hover effect).</summary>
    public InlineRun? HoveredRun
    {
        get => _hoveredRun;
        set
        {
            if (!ReferenceEquals(_hoveredRun, value))
            {
                _hoveredRun = value;
                _hoverColorsDirty = true;
            }
        }
    }
    private InlineRun? _hoveredRun;
    // True whenever hover state changed since last ApplyHoverColor call.
    // Starts true so the first paint bakes link colors correctly.
    private bool _hoverColorsDirty = true;

    public IReadOnlyList<InlineRun> Runs => _runs;
    public string ElementKey => _elementKey;

    public InlineContainerBox(MarkdownLayoutContext context, string elementKey)
    {
        _context = context;
        _elementKey = elementKey;
        Margin = context.ThemeSnapshot.GetStyle(elementKey).Margin;
    }

    public void Add(InlineRun run)
    {
        System.Diagnostics.Debug.Assert(BlockIndex != 0 || _runs.Count == 0,
            "BlockIndex must be assigned before calling Add(); source-map entries will be registered under block 0 otherwise.");
        run.InlineIndex = _runs.Count;
        _runs.Add(run);
        _context.SourceMap.Add(BlockIndex, run.InlineIndex, run.RenderedLength, run.SourceSpan);
        _bufferDirty = true; // buffer is stale until next BuildBuffer()
    }

    /// <summary>
    /// Converts a <see cref="DocumentPosition"/> (which must target this box) to an
    /// absolute character index within the concatenated inline buffer.
    /// </summary>
    public int GetBufferCharOffset(DocumentPosition pos)
    {
        EnsureBuffer();
        int offset = 0;
        for (int i = 0; i < _runs.Count; i++)
        {
            if (i == pos.InlineIndex) return offset + Math.Clamp(pos.CharacterOffset, 0, _runs[i].Text.Length);
            offset += _runs[i].Text.Length;
        }
        return offset;
    }

    /// <summary>
    /// Converts an absolute character index within the buffer back to a
    /// <see cref="DocumentPosition"/> targeting this box.
    /// </summary>
    public DocumentPosition GetPositionFromBufferOffset(int bufOffset)
    {
        EnsureBuffer();
        bufOffset = Math.Max(0, bufOffset);
        int offset = 0;
        for (int i = 0; i < _runs.Count; i++)
        {
            int len = _runs[i].Text.Length;
            if (bufOffset < offset + len)
                return new DocumentPosition(BlockIndex, _runs[i].InlineIndex, bufOffset - offset);
            // At exact run boundary: prefer start of next run (continue loop).
            offset += len;
        }
        // bufOffset >= total length: clamp to end of last run.
        if (_runs.Count == 0) return new DocumentPosition(BlockIndex, 0, 0);
        int last = _runs.Count - 1;
        return new DocumentPosition(BlockIndex, last, _runs[last].Text.Length);
    }

    private void EnsureBuffer()
    {
        if (_runs.Count == 0) { _buffer = string.Empty; _bufferDirty = false; return; }
        // Rely solely on _bufferDirty (set by Add()) — the _buffer.Length == 0 fallback
        // would cause repeated BuildBuffer() calls for boxes whose runs all produce empty
        // text (e.g. embed-placeholder runs), burning CPU without changing anything.
        if (_bufferDirty) BuildBuffer();
    }

    /// <summary>
    /// Returns the start and end positions that span the word containing
    /// <paramref name="pos"/>.  A "word" is a maximal sequence of non-whitespace
    /// characters in the buffer.
    /// </summary>
    public (DocumentPosition Start, DocumentPosition End) GetWordBoundaries(DocumentPosition pos)
    {
        EnsureBuffer();
        if (_buffer.Length == 0) return (pos, pos);
        int idx = Math.Clamp(GetBufferCharOffset(pos), 0, Math.Max(0, _buffer.Length - 1));
        var (start, end) = TextBoundaryHelper.FindWordBoundaries(_buffer, idx);
        return (GetPositionFromBufferOffset(start), GetPositionFromBufferOffset(end));
    }

    /// <summary>Returns positions for the very start and end of this inline container.</summary>
    public (DocumentPosition Start, DocumentPosition End) GetBlockBoundaries()
    {
        var start = new DocumentPosition(BlockIndex, 0, 0);
        DocumentPosition end;
        if (_runs.Count == 0)
        {
            end = start;
        }
        else
        {
            int last = _runs.Count - 1;
            end = new DocumentPosition(BlockIndex, last, _runs[last].Text.Length);
        }
        return (start, end);
    }

    public override float Measure(float availableWidth)
    {
        var style = _context.ThemeSnapshot.GetStyle(_elementKey);

        if (_layout is null || _bufferDirty || Math.Abs(_lastWidth - availableWidth) > 0.5f)
        {
            // Only rebuild the run-content string when runs changed; skip on pure
            // width-change reflow so repeated window resizes don't re-allocate.
            if (_bufferDirty) BuildBuffer();
            _layout?.Dispose();
            _layout = null; // null immediately so a layout-creation exception leaves _layout=null (safe for next Measure)
            using var format = new CanvasTextFormat
            {
                FontFamily = style.FontFamily,
                FontSize = style.FontSize,
                FontWeight = style.FontWeight,
                FontStyle = style.FontStyle,
                WordWrapping = CanvasWordWrapping.Wrap,
                LineSpacingMode = CanvasLineSpacingMode.Default,
                Direction = _context.FlowDirection == FlowDirection.RightToLeft
                    ? CanvasTextDirection.RightToLeftThenTopToBottom
                    : CanvasTextDirection.LeftToRightThenTopToBottom,
            };
            float horizontalPadding = (float)(style.Padding.Left + style.Padding.Right);
            _layout = new CanvasTextLayout(
                _context.ResourceCreator,
                _buffer,
                format,
                Math.Max(1f, availableWidth - horizontalPadding),
                float.MaxValue);
            try
            {
                // Enable DirectWrite color font path (Segoe UI Emoji / COLR-CPAL glyphs).
                _layout.Options = CanvasDrawTextOptions.EnableColorFont;
                ApplyRunStyles(_layout);
                ApplyEmbedSpacing(_layout);
            }
            catch
            {
                _layout.Dispose();
                _layout = null;
                throw;
            }
            _hoverColorsDirty = true; // new layout needs hover colors re-applied
            _lastWidth = availableWidth;
        }

        var bounds = _layout!.LayoutBounds;
        float top = (float)(style.Margin.Top + style.Padding.Top);
        float bottom = (float)(style.Margin.Bottom + style.Padding.Bottom);
        float height = (float)bounds.Height + top + bottom;
        Bounds = new Rect(0, 0, availableWidth, height);
        return height;
    }

    /// <summary>
    /// Returns the integer-pixel-snapped origin of the text layout in
    /// document coordinates.  Snapping eliminates fractional sub-pixel
    /// positioning that otherwise causes DirectWrite glyphs straddling
    /// CanvasVirtualControl tile / dirty-rect boundaries to be re-rasterised
    /// at slightly different sub-pixel offsets when the dirty rect changes
    /// shape (e.g. as a selection-drag extends a highlight rect across the
    /// text), which reads as visible "shake" of the rendered glyphs.
    /// All paint / hit-test / selection-rect / embed-rect computations use
    /// this single snapped origin so they stay in agreement.
    /// </summary>
    private (float X, float Y) GetSnappedOrigin(Theming.ElementStyle style)
    {
        float x = MathF.Round((float)(Bounds.X + style.Margin.Left + style.Padding.Left));
        float y = MathF.Round((float)(Bounds.Y + style.Margin.Top + style.Padding.Top));
        return (x, y);
    }

    public override void Paint(CanvasDrawingSession ds, Rect viewport)
    {
        if (_layout is null) return;
        var style = _context.ThemeSnapshot.GetStyle(_elementKey);

        if (style.Background is { } bg)
        {
            var rect = new Rect(
                Bounds.X + style.Margin.Left,
                Bounds.Y + style.Margin.Top,
                Bounds.Width - style.Margin.Left - style.Margin.Right,
                Bounds.Height - style.Margin.Top - style.Margin.Bottom);
            ds.FillRoundedRectangle(rect, 4, 4, bg);
        }

        // Only mutate the CanvasTextLayout when hover state has changed.
        // Calling SetColor on every frame (even with the same value) forces
        // DirectWrite to invalidate cached glyph-run metrics which causes the
        // character regions to shift slightly, producing visible text vibration.
        if (_hoverColorsDirty)
        {
            ApplyHoverColor(_layout);
            _hoverColorsDirty = false;
        }
        // Snap to integer pixels at the draw site.  See GetSnappedOrigin
        // for the rationale; the same snapped origin is used for hit-test,
        // selection rects and embed placement so they stay in sync.
        var (sx, sy) = GetSnappedOrigin(style);
        MarkdownRenderer.Diagnostics.ShakeLogger.LogPaint(
            "inline-paint",
            BlockIndex,
            sx,
            sy,
            _layout.LayoutBounds.Width,
            _layout.LayoutBounds.Height);
        ds.DrawTextLayout(_layout, sx, sy, style.Foreground);

        DrawDecorations(ds, sx, sy);
    }

    public override bool HitTest(Point point, out DocumentPosition position)
    {
        if (!Bounds.Contains(point))
        {
            position = new DocumentPosition(BlockIndex, 0, 0);
            return false;
        }
        if (_layout is null)
        {
            position = new DocumentPosition(BlockIndex, 0, 0);
            return true;
        }

        var style = _context.ThemeSnapshot.GetStyle(_elementKey);
        var (x, y) = GetSnappedOrigin(style);
        _layout.HitTest((float)point.X - x, (float)point.Y - y, out var hit);
        int charIndex = (int)hit.CharacterIndex;

        int cumulative = 0;
        foreach (var run in _runs)
        {
            int len = run.Text.Length;
            // Use strict '<' to match RunAt: character at index `cumulative+len`
            // belongs to the *next* run, not this one.  Prior `<=` pulled boundary
            // hits backward into the wrong run, leaving HitTest and RunAt
            // disagreeing about which run owns the boundary character.
            if (charIndex < cumulative + len)
            {
                position = new DocumentPosition(BlockIndex, run.InlineIndex, charIndex - cumulative);
                return true;
            }
            cumulative += len;
        }
        // Past last char: position at end of last run (or block end if empty).
        if (_runs.Count > 0)
        {
            var last = _runs[_runs.Count - 1];
            position = new DocumentPosition(BlockIndex, last.InlineIndex, last.Text.Length);
        }
        else
        {
            position = new DocumentPosition(BlockIndex, 0, 0);
        }
        return true;
    }

    public IEnumerable<Rect> GetRangeRects(DocumentRange range)
    {
        if (_layout is null) yield break;
        EnsureBuffer(); // buffer must be current for ToBufferIndex to return correct offsets
        var style = _context.ThemeSnapshot.GetStyle(_elementKey);
        var (baseX, baseY) = GetSnappedOrigin(style);

        int from = ToBufferIndex(range.Start);
        int to = ToBufferIndex(range.End);
        if (to <= from) yield break;
        var regions = _layout.GetCharacterRegions(from, to - from);
        if (regions is null) yield break;
        foreach (var r in regions)
        {
            yield return new Rect(baseX + r.LayoutBounds.X, baseY + r.LayoutBounds.Y,
                                  r.LayoutBounds.Width, r.LayoutBounds.Height);
        }
    }

    public override IEnumerable<Rect> GetSelectionRects(DocumentRange range)
        => GetRangeRects(range);

    /// <summary>
    /// Returns the bounding rectangle in document coordinates for the run at
    /// <paramref name="inlineIndex"/>. Used to position the keyboard-focus ring.
    /// Returns an empty rect if the run is not found or the layout is not built.
    /// </summary>
    public Rect GetRunRect(int inlineIndex)
    {
        if (_layout is null) return default;
        var style = _context.ThemeSnapshot.GetStyle(_elementKey);
        var (baseX, baseY) = GetSnappedOrigin(style);
        int cumulative = 0;
        foreach (var run in _runs)
        {
            int len = run.Text.Length;
            if (run.InlineIndex == inlineIndex && len > 0)
            {
                var regions = _layout.GetCharacterRegions(cumulative, len);
                if (regions is not null && regions.Length > 0)
                {
                    // Union all regions for multi-line runs.
                    double x1 = double.MaxValue, y1 = double.MaxValue;
                    double x2 = double.MinValue, y2 = double.MinValue;
                    foreach (var r in regions)
                    {
                        var lb = r.LayoutBounds;
                        if (lb.X < x1) x1 = lb.X;
                        if (lb.Y < y1) y1 = lb.Y;
                        if (lb.X + lb.Width  > x2) x2 = lb.X + lb.Width;
                        if (lb.Y + lb.Height > y2) y2 = lb.Y + lb.Height;
                    }
                    return new Rect(baseX + x1, baseY + y1, x2 - x1, y2 - y1);
                }
                return default;
            }
            cumulative += len;
        }
        return default;
    }

    /// <summary>
    /// For each <see cref="InlineEmbedRun"/>, returns the rectangle in document
    /// coordinates where its hosted WinUI element should be placed on the
    /// overlay canvas.
    /// </summary>
    public IEnumerable<(InlineEmbedRun Run, Rect Rect)> EnumerateEmbedRects()
    {
        if (_layout is null) yield break;
        var style = _context.ThemeSnapshot.GetStyle(_elementKey);
        var (baseX, baseY) = GetSnappedOrigin(style);

        int cumulative = 0;
        foreach (var run in _runs)
        {
            int len = run.Text.Length;
            if (run is InlineEmbedRun emb && len > 0)
            {
                var regions = _layout.GetCharacterRegions(cumulative, len);
                if (regions is null) { cumulative += len; continue; }
                foreach (var r in regions)
                {
                    var lb = r.LayoutBounds;
                    double cellY = lb.Y + (lb.Height - emb.DesiredHeight) / 2.0;
                    if (cellY < lb.Y) cellY = lb.Y;
                    var rect = new Rect(baseX + lb.X, baseY + cellY,
                        Math.Min(emb.DesiredWidth, lb.Width), emb.DesiredHeight);
                    yield return (emb, rect);
                    break;
                }
            }
            cumulative += len;
        }
    }

    /// <summary>
    /// Returns the run hovered for the given document-coordinate point, or
    /// null if no run is hovered. Used by the control to drive link hover.
    /// </summary>
    public InlineRun? RunAt(Point point)
    {
        if (_layout is null) return null;
        if (!Bounds.Contains(point)) return null;
        var style = _context.ThemeSnapshot.GetStyle(_elementKey);
        var (x, y) = GetSnappedOrigin(style);
        _layout.HitTest((float)point.X - x, (float)point.Y - y, out var hitRegion);
        int charIndex = (int)hitRegion.CharacterIndex;
        int cumulative = 0;
        foreach (var run in _runs)
        {
            int len = run.Text.Length;
            if (charIndex < cumulative + len) return run;
            cumulative += len;
        }
        // charIndex >= total buffer length (trailing-edge of last run): return last run
        // so hover-cursor and highlight match the hit-test behaviour that maps this
        // position to the last run's DocumentPosition.
        if (_runs.Count > 0) return _runs[_runs.Count - 1];
        return null;
    }

    private int ToBufferIndex(DocumentPosition pos)
    {
        if (pos.BlockIndex < BlockIndex) return 0;
        if (pos.BlockIndex > BlockIndex) return _buffer.Length;
        int cumulative = 0;
        foreach (var run in _runs)
        {
            if (run.InlineIndex == pos.InlineIndex)
                return cumulative + Math.Clamp(pos.CharacterOffset, 0, run.Text.Length);
            cumulative += run.Text.Length;
        }
        return _buffer.Length;
    }

    public override void Dispose()
    {
        _layout?.Dispose();
        _layout = null;
    }

    private void BuildBuffer()
    {
        var sb = new System.Text.StringBuilder();
        foreach (var run in _runs) sb.Append(run.Text);
        _buffer = sb.ToString();
        _bufferDirty = false;
    }

    private void ApplyRunStyles(CanvasTextLayout layout)
    {
        var containerStyle = _context.ThemeSnapshot.GetStyle(_elementKey);
        int cumulative = 0;
        foreach (var run in _runs)
        {
            int len = run.Text.Length;
            if (len == 0) continue;
            if (!string.IsNullOrEmpty(run.ElementKey) && run.ElementKey != _elementKey)
            {
                var rs = _context.ThemeSnapshot.GetStyle(run.ElementKey);
                if (rs.FontFamily != containerStyle.FontFamily)
                    layout.SetFontFamily(cumulative, len, rs.FontFamily);
                if (rs.FontWeight.Weight != containerStyle.FontWeight.Weight)
                    layout.SetFontWeight(cumulative, len, rs.FontWeight);
                if (rs.FontStyle != containerStyle.FontStyle)
                    layout.SetFontStyle(cumulative, len, rs.FontStyle);
                if (rs.FontSize != containerStyle.FontSize)
                    layout.SetFontSize(cumulative, len, rs.FontSize);
                if (rs.Foreground != containerStyle.Foreground)
                    layout.SetColor(cumulative, len, rs.Foreground);
            }
            cumulative += len;
        }
    }

    private void ApplyEmbedSpacing(CanvasTextLayout layout)
    {
        int cumulative = 0;
        foreach (var run in _runs)
        {
            int len = run.Text.Length;
            if (run is InlineEmbedRun emb && len > 0)
            {
                try
                {
                    layout.SetCharacterSpacing(cumulative, len, 0, 0, emb.DesiredWidth);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[InlineContainerBox] SetCharacterSpacing failed: {ex.Message}");
                }
                layout.SetColor(cumulative, len, Color.FromArgb(0, 0, 0, 0));
            }
            cumulative += len;
        }
    }

    private void ApplyHoverColor(CanvasTextLayout layout)
    {
        var hover = HoveredRun;
        // First: reset every LinkRun back to its theme foreground so that an
        // unhover event restores the original color. SetColor must be called
        // even when not hovering because a previous frame may have brightened it.
        int cumulative = 0;
        foreach (var run in _runs)
        {
            int len = run.Text.Length;
            if (len > 0 && run is LinkRun)
            {
                var rs = _context.ThemeSnapshot.GetStyle(run.ElementKey);
                layout.SetColor(cumulative, len, rs.Foreground);
            }
            cumulative += len;
        }

        if (hover is not LinkRun) return;
        cumulative = 0;
        foreach (var run in _runs)
        {
            int len = run.Text.Length;
            if (len == 0) continue;
            if (ReferenceEquals(run, hover))
            {
                var rs = _context.ThemeSnapshot.GetStyle(run.ElementKey);
                var c = rs.Foreground;
                var hot = Color.FromArgb(c.A,
                    (byte)Math.Min(255, c.R + 40),
                    (byte)Math.Min(255, c.G + 40),
                    (byte)Math.Min(255, c.B + 40));
                layout.SetColor(cumulative, len, hot);
                break;
            }
            cumulative += len;
        }
    }

    private void DrawDecorations(CanvasDrawingSession ds, float baseX, float baseY)
    {
        if (_layout is null) return;
        var lineMetrics = _layout.LineMetrics;

        int cumulative = 0;
        foreach (var run in _runs)
        {
            int len = run.Text.Length;
            if (len == 0) { continue; }
            if (run is InlineEmbedRun) { cumulative += len; continue; }
            // Superscript link runs (footnote citation markers ¹²³…) should not
            // have an underline drawn at the normal line baseline — the small
            // Unicode glyphs sit high in the line and the baseline underline ends
            // up drawn ~10 px below them, looking completely detached.  Skip
            // decorations for these runs entirely; their link appearance is already
            // communicated by the accent foreground color.
            if (run is LinkRun { IsSuperscript: true }) { cumulative += len; continue; }

            var rs = string.IsNullOrEmpty(run.ElementKey)
                ? _context.ThemeSnapshot.GetStyle(_elementKey)
                : _context.ThemeSnapshot.GetStyle(run.ElementKey);

            bool drawRunBg = rs.Background is not null
                && !string.IsNullOrEmpty(run.ElementKey)
                && run.ElementKey != _elementKey;

            if (drawRunBg || rs.Underline || rs.Strikethrough)
            {
                var regions = _layout.GetCharacterRegions(cumulative, len);
                if (regions is null) { cumulative += len; continue; } // Win2D can return null on DirectWrite errors
                foreach (var r in regions)
                {
                    var lb = r.LayoutBounds;
                    if (drawRunBg && rs.Background is { } bg)
                    {
                        double bgTop = lb.Y + lb.Height * 0.05;
                        double bgH = lb.Height * 0.90;
                        ds.FillRoundedRectangle(
                            new Rect(baseX + lb.X - 2, baseY + bgTop, lb.Width + 4, bgH),
                            3, 3, bg);
                    }

                    var lm = FindLineMetrics(lineMetrics, r);
                    // CanvasLineMetrics has Baseline (distance from line top to baseline).
                    // x-height ≈ 50% of baseline. Place strikethrough at baseline - xHeight/2.
                    float baselineFromTop = lm.Baseline > 0 ? lm.Baseline : (float)lb.Height * 0.80f;
                    float xHeight = baselineFromTop * 0.50f;
                    float strikeY = (float)(baseY + lb.Y + (baselineFromTop - xHeight * 0.5f));
                    float underlineY = (float)(baseY + lb.Y + baselineFromTop + 1.5f);

                    if (rs.Underline)
                    {
                        ds.DrawLine((float)(baseX + lb.X), underlineY, (float)(baseX + lb.X + lb.Width),
                            underlineY, rs.Foreground, 1.0f);
                    }
                    if (rs.Strikethrough)
                    {
                        ds.DrawLine((float)(baseX + lb.X), strikeY, (float)(baseX + lb.X + lb.Width),
                            strikeY, rs.Foreground, 1.0f);
                    }
                }
            }
            cumulative += len;
        }
    }

    private static CanvasLineMetrics FindLineMetrics(CanvasLineMetrics[] metrics, CanvasTextLayoutRegion region)
    {
        int idx = (int)region.CharacterIndex;
        int run = 0;
        foreach (var m in metrics)
        {
            int next = run + m.CharacterCount;
            if (idx < next) return m;
            run = next;
        }
        return metrics.Length > 0 ? metrics[metrics.Length - 1] : default;
    }
}
