using System;
using System.Collections.Generic;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
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
        // Source map registration for this inline run.
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
                LineSpacingMode = CanvasLineSpacingMode.Proportional,
                LineSpacing = style.LineHeightMultiplier,
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
            ApplyRunStyles(_layout);
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
        ds.DrawTextLayout(_layout, x, y, style.Foreground);

        // Decorations: underline / strikethrough per run not natively supported on
        // CanvasTextLayout style ranges — drawn manually.
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

        // Map buffer index back to (inline run, char offset).
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

    /// <summary>
    /// Returns axis-aligned rectangles in document coordinates for the part of
    /// this block intersecting the given range. Used for selection highlighting.
    /// </summary>
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
        int cumulative = 0;
        foreach (var run in _runs)
        {
            int len = run.Text.Length;
            if (len == 0) continue;
            if (run.ElementKey != _elementKey)
            {
                var rs = _context.ThemeSnapshot.GetStyle(run.ElementKey);
                layout.SetFontFamily(cumulative, len, rs.FontFamily);
                layout.SetFontSize(cumulative, len, rs.FontSize);
                layout.SetFontWeight(cumulative, len, rs.FontWeight);
                layout.SetFontStyle(cumulative, len, rs.FontStyle);
                layout.SetColor(cumulative, len, rs.Foreground);
            }
            cumulative += len;
        }
    }

    private void DrawDecorations(CanvasDrawingSession ds, float baseX, float baseY)
    {
        if (_layout is null) return;
        int cumulative = 0;
        foreach (var run in _runs)
        {
            int len = run.Text.Length;
            if (len == 0) { continue; }
            var rs = _context.ThemeSnapshot.GetStyle(run.ElementKey);
            if (rs.Underline || rs.Strikethrough || rs.Background is not null)
            {
                var regions = _layout.GetCharacterRegions(cumulative, len);
                foreach (var r in regions)
                {
                    var lb = r.LayoutBounds;
                    if (rs.Background is { } bg)
                    {
                        ds.FillRoundedRectangle(
                            new Rect(baseX + lb.X - 2, baseY + lb.Y, lb.Width + 4, lb.Height),
                            3, 3, bg);
                    }
                    if (rs.Underline)
                    {
                        float yLine = (float)(baseY + lb.Y + lb.Height - 1.5f);
                        ds.DrawLine((float)(baseX + lb.X), yLine, (float)(baseX + lb.X + lb.Width), yLine, rs.Foreground, 1.0f);
                    }
                    if (rs.Strikethrough)
                    {
                        float yLine = (float)(baseY + lb.Y + lb.Height * 0.55f);
                        ds.DrawLine((float)(baseX + lb.X), yLine, (float)(baseX + lb.X + lb.Width), yLine, rs.Foreground, 1.0f);
                    }
                }
            }
            cumulative += len;
        }
    }
}
