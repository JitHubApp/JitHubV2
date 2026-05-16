using Microsoft.Graphics.Canvas;
using Windows.Foundation;
using MarkdownRenderer.Document;
using MarkdownRenderer.Theming;

namespace MarkdownRenderer.Layout.Boxes;

public sealed class ThematicBreakBox : BlockBox
{
    private readonly MarkdownLayoutContext _context;

    public ThematicBreakBox(MarkdownLayoutContext context)
    {
        _context = context;
        Margin = context.ThemeSnapshot.GetStyle(MarkdownElementKeys.ThematicBreak).Margin;
    }

    public override float Measure(float availableWidth)
    {
        float h = (float)(Margin.Top + Margin.Bottom + 1);
        Bounds = new Rect(0, 0, availableWidth, h);
        return h;
    }

    public override void Paint(CanvasDrawingSession ds, Rect viewport)
    {
        var style = _context.ThemeSnapshot.GetStyle(MarkdownElementKeys.ThematicBreak);
        float y = (float)(Bounds.Y + Margin.Top);
        ds.DrawLine(
            (float)(Bounds.X + Margin.Left), y,
            (float)(Bounds.X + Bounds.Width - Margin.Right), y,
            style.Foreground, 1f);
    }

    public override void PaintSelectionForeground(
        CanvasDrawingSession ds,
        DocumentRange range,
        Windows.UI.Color color,
        Rect viewport)
    {
        var n = range.Normalized();
        if (BlockIndex < n.Start.BlockIndex || BlockIndex > n.End.BlockIndex)
            return;

        float y = (float)(Bounds.Y + Margin.Top);
        if (y < viewport.Top || y > viewport.Bottom)
            return;

        ds.DrawLine(
            (float)(Bounds.X + Margin.Left), y,
            (float)(Bounds.X + Bounds.Width - Margin.Right), y,
            color, 1f);
    }
}
