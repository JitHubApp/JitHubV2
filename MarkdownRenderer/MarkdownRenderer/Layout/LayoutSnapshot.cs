using System.Collections.Generic;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using MarkdownRenderer.Document;

namespace MarkdownRenderer.Layout;

/// <summary>
/// Holds the laid-out block tree for a single markdown source. Computed off the
/// UI thread; published atomically to <see cref="MarkdownRenderer.Controls.MarkdownRendererControl"/>.
/// </summary>
public sealed class LayoutSnapshot
{
    public LayoutSnapshot(IReadOnlyList<BlockBox> blocks, MarkdownSourceMap sourceMap, float width, float height)
    {
        Blocks = blocks;
        SourceMap = sourceMap;
        Size = new Size(width, height);
    }

    public IReadOnlyList<BlockBox> Blocks { get; }
    public MarkdownSourceMap SourceMap { get; }
    public Size Size { get; }

    public void Paint(CanvasDrawingSession ds, Rect viewport)
    {
        foreach (var b in Blocks)
        {
            if (b.Bounds.Bottom < viewport.Top) continue;
            if (b.Bounds.Top > viewport.Bottom) break;
            b.Paint(ds, viewport);
        }
    }

    public bool HitTest(Point point, out DocumentPosition position)
    {
        foreach (var b in Blocks)
        {
            // Use `continue` rather than `break` so we don't skip a hit just
            // because a preceding block has Bounds.Top below the hit Y.  Custom
            // renderers, footnote groups, and any future virtualisation may
            // produce out-of-vertical-order blocks; iterating all is cheap.
            if (b.Bounds.Top - 4 > point.Y) continue;
            if (b.HitTest(point, out position)) return true;
        }
        position = DocumentPosition.Zero;
        return false;
    }
}
