using System.Collections.Generic;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using MarkdownRenderer.Document;
using MarkdownRenderer.Layout.Boxes;

namespace MarkdownRenderer.Layout;

/// <summary>
/// Holds the laid-out block tree for a single markdown source. Computed off the
/// UI thread; published atomically to <see cref="MarkdownRenderer.Controls.MarkdownRendererControl"/>.
/// </summary>
public sealed class LayoutSnapshot : System.IDisposable
{
    private readonly IReadOnlyDictionary<int, int> _footnoteDefBlocks;
    private readonly IReadOnlyDictionary<int, int> _footnoteRefBlocks;

    public LayoutSnapshot(
        IReadOnlyList<BlockBox> blocks,
        MarkdownSourceMap sourceMap,
        float width,
        float height,
        IReadOnlyDictionary<int, int>? footnoteDefBlocks = null,
        IReadOnlyDictionary<int, int>? footnoteRefBlocks = null)
    {
        Blocks = blocks;
        SourceMap = sourceMap;
        Size = new Size(width, height);
        _footnoteDefBlocks = footnoteDefBlocks ?? new Dictionary<int, int>();
        _footnoteRefBlocks = footnoteRefBlocks ?? new Dictionary<int, int>();
    }

    public IReadOnlyList<BlockBox> Blocks { get; }
    public MarkdownSourceMap SourceMap { get; }
    public Size Size { get; }

    /// <summary>Returns the block index of the footnote definition for the given order, or null.</summary>
    public int? FootnoteDefBlock(int order)
        => _footnoteDefBlocks.TryGetValue(order, out var v) ? v : null;

    /// <summary>Returns the block index of the inline citation paragraph for the given order, or null.</summary>
    public int? FootnoteRefBlock(int order)
        => _footnoteRefBlocks.TryGetValue(order, out var v) ? v : null;

    public void Dispose()
    {
        foreach (var b in Blocks) b.Dispose();
    }

    public void Paint(CanvasDrawingSession ds, Rect viewport)
    {
        foreach (var b in Blocks)
        {
            if (b.Bounds.Bottom < viewport.Top) continue;
            // Use `continue` rather than `break` to avoid skipping a visible block
            // that follows an out-of-order block (custom renderers, footnote groups,
            // and future virtualization may produce non-monotone Bounds.Top values).
            if (b.Bounds.Top > viewport.Bottom) continue;
            b.Paint(ds, viewport);
        }
    }

    public void PaintSelectionForeground(CanvasDrawingSession ds, DocumentRange range, Windows.UI.Color color, Rect viewport)
    {
        foreach (var b in Blocks)
        {
            if (b.Bounds.Bottom < viewport.Top || b.Bounds.Top > viewport.Bottom) continue;
            PaintSelectionForeground(b, ds, range, color, viewport);
        }
    }

    private static void PaintSelectionForeground(BlockBox box, CanvasDrawingSession ds, DocumentRange range, Windows.UI.Color color, Rect viewport)
    {
        box.PaintSelectionForeground(ds, range, color, viewport);
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

    /// <summary>
    /// Walks the full block tree and returns all keyboard-focusable items
    /// (<see cref="LinkRun"/> and <see cref="InlineEmbedRun"/> instances) in
    /// document order. Used by <see cref="Controls.MarkdownRendererControl"/> for
    /// Tab/Shift+Tab keyboard navigation.
    /// </summary>
    public IReadOnlyList<FocusableItem> CollectFocusableItems()
    {
        var list = new List<FocusableItem>();
        foreach (var b in Blocks) WalkForFocusable(b, list);
        return list;
    }

    private static void WalkForFocusable(BlockBox box, List<FocusableItem> list)
    {
        switch (box)
        {
            case InlineContainerBox icb:
                foreach (var run in icb.Runs)
                {
                    if (run is LinkRun)
                        list.Add(new FocusableItem(icb.BlockIndex, run.InlineIndex, FocusableItemKind.Link));
                    else if (run is InlineEmbedRun)
                        list.Add(new FocusableItem(icb.BlockIndex, run.InlineIndex, FocusableItemKind.InlineEmbed));
                }
                break;
            case EmbedBox eb:
                list.Add(new FocusableItem(eb.BlockIndex, 0, FocusableItemKind.BlockEmbed));
                break;
            case ListItemBox lib:
                WalkForFocusable(lib.Marker, list);
                WalkForFocusable(lib.Content, list);
                break;
            case TableBox tb:
                foreach (var cell in tb.GetCellBoxes()) WalkForFocusable(cell, list);
                break;
            case StackBox sb:
                foreach (var c in sb.Children) WalkForFocusable(c, list);
                break;
        }
    }
}
