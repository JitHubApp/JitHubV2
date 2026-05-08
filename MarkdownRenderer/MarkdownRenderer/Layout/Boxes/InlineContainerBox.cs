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
    private float _lastWidth;
    private readonly MarkdownLayoutContext _context;

    /// <summary>Run currently being hovered by the pointer (for link hover effect).</summary>
    public InlineRun? HoveredRun { get; set; }

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
        run.InlineIndex = _runs.Count;
        _runs.Add(run);
        _context.SourceMap.Add(BlockIndex, run.InlineIndex, run.RenderedLength, run.SourceSpan);
    }

    public override float Measure(float availableWidth)
    {
        var style = _context.ThemeSnapshot.GetStyle(_elementKey);

        if (_layout is null || Math.Abs(_lastWidth - availableWidth) > 0.5f)
        {
            BuildBuffer();
            _layout?.Dispose();
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
            // Enable DirectWrite color font path (Segoe UI Emoji / COLR-CPAL glyphs).
            _layout.Options = CanvasDrawTextOptions.EnableColorFont;
            ApplyRunStyles(_layout);
            ApplyEmbedSpacing(_layout);
            _lastWidth = availableWidth;
        }

        var bounds = _layout!.LayoutBounds;
        float top = (float)(style.Margin.Top + style.Padding.Top);
        float bottom = (float)(style.Margin.Bottom + style.Padding.Bottom);
        float height = (float)bounds.Height + top + bottom;
        Bounds = new Rect(0, 0, availableWidth, height);
        return height;
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

        float x = (float)(Bounds.X + style.Margin.Left + style.Padding.Left);
        float y = (float)(Bounds.Y + style.Margin.Top + style.Padding.Top);

        ApplyHoverColor(_layout);
        ds.DrawTextLayout(_layout, x, y, style.Foreground);

        DrawDecorations(ds, x, y);
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
        float x = (float)(Bounds.X + style.Margin.Left + style.Padding.Left);
        float y = (float)(Bounds.Y + style.Margin.Top + style.Padding.Top);
        _layout.HitTest((float)point.X - x, (float)point.Y - y, out var hit);
        int charIndex = (int)hit.CharacterIndex;

        int cumulative = 0;
        foreach (var run in _runs)
        {
            int len = run.Text.Length;
            if (charIndex <= cumulative + len)
            {
                position = new DocumentPosition(BlockIndex, run.InlineIndex, charIndex - cumulative);
                return true;
            }
            cumulative += len;
        }
        position = new DocumentPosition(BlockIndex, _runs.Count, 0);
        return true;
    }

    public IEnumerable<Rect> GetRangeRects(DocumentRange range)
    {
        if (_layout is null) yield break;
        var style = _context.ThemeSnapshot.GetStyle(_elementKey);
        float baseX = (float)(Bounds.X + style.Margin.Left + style.Padding.Left);
        float baseY = (float)(Bounds.Y + style.Margin.Top + style.Padding.Top);

        int from = ToBufferIndex(range.Start);
        int to = ToBufferIndex(range.End);
        if (to <= from) yield break;
        var regions = _layout.GetCharacterRegions(from, to - from);
        foreach (var r in regions)
        {
            yield return new Rect(baseX + r.LayoutBounds.X, baseY + r.LayoutBounds.Y,
                                  r.LayoutBounds.Width, r.LayoutBounds.Height);
        }
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
        float baseX = (float)(Bounds.X + style.Margin.Left + style.Padding.Left);
        float baseY = (float)(Bounds.Y + style.Margin.Top + style.Padding.Top);

        int cumulative = 0;
        foreach (var run in _runs)
        {
            int len = run.Text.Length;
            if (run is InlineEmbedRun emb && len > 0)
            {
                var regions = _layout.GetCharacterRegions(cumulative, len);
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
        float x = (float)(Bounds.X + style.Margin.Left + style.Padding.Left);
        float y = (float)(Bounds.Y + style.Margin.Top + style.Padding.Top);
        _layout.HitTest((float)point.X - x, (float)point.Y - y, out var hitRegion);
        int charIndex = (int)hitRegion.CharacterIndex;
        int cumulative = 0;
        foreach (var run in _runs)
        {
            int len = run.Text.Length;
            if (charIndex < cumulative + len) return run;
            cumulative += len;
        }
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

    private void BuildBuffer()
    {
        var sb = new System.Text.StringBuilder();
        foreach (var run in _runs) sb.Append(run.Text);
        _buffer = sb.ToString();
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
        if (hover is null) return;
        if (hover is not LinkRun) return;
        int cumulative = 0;
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

            var rs = string.IsNullOrEmpty(run.ElementKey)
                ? _context.ThemeSnapshot.GetStyle(_elementKey)
                : _context.ThemeSnapshot.GetStyle(run.ElementKey);

            bool drawRunBg = rs.Background is not null
                && !string.IsNullOrEmpty(run.ElementKey)
                && run.ElementKey != _elementKey;

            if (drawRunBg || rs.Underline || rs.Strikethrough)
            {
                var regions = _layout.GetCharacterRegions(cumulative, len);
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
