using Microsoft.Graphics.Canvas;
using Windows.Foundation;
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
}
