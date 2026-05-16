using System.Collections.Generic;
using Microsoft.Graphics.Canvas;
using Windows.Foundation;
using Windows.UI;
using MarkdownRenderer.Diagnostics;
using MarkdownRenderer.Document;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;

namespace MarkdownRenderer.Selection;

/// <summary>
/// Holds the active selection range and produces highlight rectangles for
/// painting. Hit tests are performed by the layout snapshot; this class only
/// owns state.
/// </summary>
public sealed class SelectionController
{
    public DocumentRange Range { get; private set; } = DocumentRange.Empty;
    public bool IsActive => !Range.IsEmpty;

    public event System.EventHandler? Changed;

    public void SetAnchor(DocumentPosition anchor)
    {
        Range = new DocumentRange(anchor, anchor);
        if (ShakeLogger.IsEnabled)
            ShakeLogger.Log("sel-anchor",
                $"blk={anchor.BlockIndex} inl={anchor.InlineIndex} c={anchor.CharacterOffset}");
        Changed?.Invoke(this, System.EventArgs.Empty);
    }

    public void ExtendTo(DocumentPosition position)
    {
        Range = new DocumentRange(Range.Start, position);
        if (ShakeLogger.IsEnabled)
            ShakeLogger.Log("sel-extend",
                $"start=blk{Range.Start.BlockIndex}/c{Range.Start.CharacterOffset} " +
                $"end=blk{position.BlockIndex}/c{position.CharacterOffset}");
        Changed?.Invoke(this, System.EventArgs.Empty);
    }

    public void Clear()
    {
        if (Range.IsEmpty) return;
        Range = DocumentRange.Empty;
        Changed?.Invoke(this, System.EventArgs.Empty);
    }

    public IEnumerable<Rect> GetHighlightRects(LayoutSnapshot snapshot)
    {
        if (Range.IsEmpty) yield break;
        var normalized = Range.Normalized();
        foreach (var block in snapshot.Blocks)
        {
            foreach (var rect in EnumerateBlockRects(block, normalized))
                yield return rect;
        }
    }

    private static IEnumerable<Rect> EnumerateBlockRects(BlockBox box, DocumentRange range)
    {
        // Containers recurse; leaves return their own rects via the virtual.
        if (box is ListItemBox lib)
        {
            foreach (var r in EnumerateBlockRects(lib.Marker, range)) yield return r;
            foreach (var r in EnumerateBlockRects(lib.Content, range)) yield return r;
            yield break;
        }
        if (box is TableBox tb)
        {
            foreach (var cell in tb.GetCellBoxes())
                foreach (var r in EnumerateBlockRects(cell, range))
                    yield return r;
            yield break;
        }
        if (box is StackBox sb)
        {
            foreach (var c in sb.Children)
                foreach (var r in EnumerateBlockRects(c, range))
                    yield return r;
            yield break;
        }
        foreach (var r in box.GetSelectionRects(range)) yield return r;
    }

    public void PaintHighlight(CanvasDrawingSession ds, LayoutSnapshot snapshot, Color color)
    {
        bool first = true;
        foreach (var rect in GetHighlightRects(snapshot))
        {
            // Snap to integer DIPs and use square corners. Sub-pixel rect edges
            // (especially with rounded corners) cause neighbouring glyph dirty
            // rects to vary frame-to-frame during a drag, which manifests as
            // text "shake". Browser-style square selection avoids corner AA.
            double x = System.Math.Floor(rect.X);
            double y = System.Math.Floor(rect.Y);
            double w = System.Math.Ceiling(rect.X + rect.Width) - x;
            double h = System.Math.Ceiling(rect.Y + rect.Height) - y;
            var snapped = new Rect(x, y, w, h);
            if (first)
            {
                if (ShakeLogger.IsEnabled)
                    ShakeLogger.LogPaint(
                        "sel-rect-first", -1, snapped.X, snapped.Y, snapped.Width, snapped.Height);
                first = false;
            }
            ds.FillRectangle(snapped, color);
        }
    }
}
