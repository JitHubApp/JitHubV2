using System.Collections.Generic;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using MarkdownRenderer.Document;

namespace MarkdownRenderer.Layout.Boxes;

/// <summary>
/// Stacks child blocks vertically with optional left indent &amp; accent bar (used
/// for blockquotes &amp; list items).
/// </summary>
public class StackBox : BlockBox
{
    private readonly List<BlockBox> _children = new();
    public IReadOnlyList<BlockBox> Children => _children;

    public Thickness ContentPadding { get; set; }
    public Windows.UI.Color? AccentBar { get; set; }
    public Windows.UI.Color? Background { get; set; }
    public float CornerRadius { get; set; } = 0;

    /// <summary>
    /// Flow direction for this stack. When RightToLeft, the accent bar (used
    /// for blockquotes / GFM alerts) is drawn on the right edge instead of
    /// the left, matching RTL reading order.
    /// </summary>
    public FlowDirection FlowDirection { get; set; } = FlowDirection.LeftToRight;

    public void Add(BlockBox child) => _children.Add(child);

    public override float Measure(float availableWidth)
    {
        float innerWidth = availableWidth - (float)(ContentPadding.Left + ContentPadding.Right) - (float)(Margin.Left + Margin.Right);
        float y = (float)(Margin.Top + ContentPadding.Top);
        // In RTL, the accent bar is on the right edge, so indent content from the
        // RIGHT (ContentPadding.Left is the bar-side indent). Use ContentPadding.Right
        // as the child's left-edge offset, leaving ContentPadding.Left space on the right.
        float childStartX = FlowDirection == FlowDirection.RightToLeft
            ? (float)(Margin.Left + ContentPadding.Right)
            : (float)(Margin.Left + ContentPadding.Left);
        foreach (var child in _children)
        {
            float h = child.Measure(innerWidth);
            child.Arrange(childStartX, y, innerWidth);
            y += h;
        }
        y += (float)(ContentPadding.Bottom + Margin.Bottom);
        Bounds = new Rect(0, 0, availableWidth, y);
        return y;
    }

    public override void Arrange(float x, float y, float width)
    {
        float dx = x - (float)Bounds.X;
        float dy = y - (float)Bounds.Y;
        foreach (var c in _children)
            c.Arrange((float)c.Bounds.X + dx, (float)c.Bounds.Y + dy, (float)c.Bounds.Width);
        Bounds = new Rect(x, y, width, Bounds.Height);
    }

    public override void Paint(CanvasDrawingSession ds, Rect viewport)
    {
        if (Background is { } bg)
        {
            var rect = new Rect(Bounds.X + Margin.Left, Bounds.Y + Margin.Top,
                                Bounds.Width - Margin.Left - Margin.Right,
                                Bounds.Height - Margin.Top - Margin.Bottom);
            ds.FillRoundedRectangle(rect, CornerRadius, CornerRadius, bg);
        }
        if (AccentBar is { } bar)
        {
            double barX = FlowDirection == FlowDirection.RightToLeft
                ? Bounds.X + Bounds.Width - Margin.Right - 3
                : Bounds.X + Margin.Left;
            var rect = new Rect(barX, Bounds.Y + Margin.Top,
                                3, Bounds.Height - Margin.Top - Margin.Bottom);
            ds.FillRectangle(rect, bar);
        }
        foreach (var c in _children)
        {
            if (c.Bounds.Bottom < viewport.Top || c.Bounds.Top > viewport.Bottom) continue;
            c.Paint(ds, viewport);
        }
    }

    public override void PaintSelectionForeground(
        CanvasDrawingSession ds,
        DocumentRange range,
        Windows.UI.Color color,
        Rect viewport)
    {
        foreach (var c in _children)
        {
            if (c.Bounds.Bottom < viewport.Top || c.Bounds.Top > viewport.Bottom) continue;
            c.PaintSelectionForeground(ds, range, color, viewport);
        }
    }

    public override bool HitTest(Point point, out DocumentPosition position)
    {
        foreach (var c in _children)
        {
            if (c.HitTest(point, out position)) return true;
        }
        // Padding-area hits return false — the StackBox itself has no source-map
        // entry, so reporting (BlockIndex, 0, 0) for these would produce
        // incorrect copy ranges in MarkdownSourceMap.Slice.
        position = new DocumentPosition(BlockIndex, 0, 0);
        return false;
    }

    public override void Dispose()
    {
        foreach (var c in _children) c.Dispose();
    }
}
