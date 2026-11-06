using System;
using System.Collections.Generic;
using MarkdownRenderer.Layout.Boxes;

namespace MarkdownRenderer.Layout;

/// <summary>
/// Prepared off-thread from private measured roots. These containers borrow
/// the candidate's placements; disposing the candidate, not this plan, owns
/// their retirement. Ordinals address the image array, not document blocks.
/// </summary>
internal sealed class RasterPlacementImagePlan
{
    internal RasterPlacementImagePlan(InlineContainerBox root, int expectedCount)
        : this(new BlockBox[] { root }, expectedCount)
    {
    }

    internal RasterPlacementImagePlan(IReadOnlyList<BlockBox> roots, int expectedCount)
        : this(roots, expectedCount, placementTransform: null)
    {
    }

    internal RasterPlacementImagePlan(
        LayoutSnapshot snapshot,
        IReadOnlyList<BlockBox> roots,
        int expectedCount)
        : this(
            roots,
            expectedCount,
            (snapshot ?? throw new ArgumentNullException(nameof(snapshot))).CaptureLogicalRectMapper())
    {
    }

    private RasterPlacementImagePlan(
        IReadOnlyList<BlockBox> roots,
        int expectedCount,
        Func<int, Windows.Foundation.Rect, Windows.Foundation.Rect>? placementTransform)
    {
        ArgumentNullException.ThrowIfNull(roots);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(expectedCount);
        Images = new List<ImageBox>(expectedCount);
        Subscriptions = new HashSet<ImageBox>(expectedCount, ReferenceEqualityComparer.Instance);
        foreach (BlockBox block in roots)
            CollectImages(block);
        if (Images.Count != expectedCount)
            throw new InvalidOperationException("The private raster placement image membership is incomplete.");
        Index = new ViewportBandIndex(Images.Count);
        ImageBounds = new Windows.Foundation.Rect[Images.Count];
        for (int ordinal = 0; ordinal < Images.Count; ordinal++)
        {
            Windows.Foundation.Rect bounds = Images[ordinal].Bounds;
            if (placementTransform is not null)
                bounds = placementTransform(Images[ordinal].BlockIndex, bounds);
            ImageBounds[ordinal] = bounds;
            Index.SetEntry(ordinal, ordinal, bounds.Top, bounds.Bottom);
        }
        Index.Commit();
        PublicationImages = Images.ToArray();
    }

    private void CollectImages(BlockBox block)
    {
        // Preserve the control's ordinary image-plan order exactly, including
        // header/body cells and list marker/content ordering. Candidate source
        // fences use this same traversal; equal counts alone are insufficient.
        switch (block)
        {
            case ImageBox image:
                if (!Subscriptions.Add(image))
                    throw new InvalidOperationException("The private raster placement contains aliased images.");
                Images.Add(image);
                break;
            case InlineContainerBox inline:
                foreach (var (run, _) in inline.InlineImageRuns)
                {
                    if (!Subscriptions.Add(run.Image))
                        throw new InvalidOperationException("The private raster placement contains aliased images.");
                    Images.Add(run.Image);
                }
                break;
            case TableBox table:
                foreach (InlineContainerBox cell in table.GetCellBoxes())
                    CollectImages(cell);
                break;
            case ListItemBox item:
                CollectImages(item.Marker);
                CollectImages(item.Content);
                break;
            case StackBox stack when stack.GetType() == typeof(StackBox):
                foreach (BlockBox child in stack.Children)
                    CollectImages(child);
                break;
            default:
                throw new InvalidOperationException("The private raster placement contains an unsupported root.");
        }
    }

    internal List<ImageBox> Images { get; }
    internal HashSet<ImageBox> Subscriptions { get; }
    internal ViewportBandIndex Index { get; }
    internal ImageBox[] PublicationImages { get; }
    internal Windows.Foundation.Rect[] ImageBounds { get; }
}
