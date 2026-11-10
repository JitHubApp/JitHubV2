using MarkdownRenderer.GitHub.Tests.Layout.Internal.Images.Raster;
using MarkdownRenderer.Images;
using MarkdownRenderer.Layout.Internal.Boxes;
using MarkdownRenderer.Layout.Internal.Images;
using MarkdownRenderer.Layout.Internal.Images.BitmapCache;
using MarkdownRenderer.Layout.Internal.Inlines;
using MarkdownRenderer.Layout.Internal.Placement;
using MarkdownRenderer.Layout.Internal.Snapshots;
using MarkdownRenderer.Layout.Internal.Tables;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests.TestSupport.Fixtures;

/// <summary>
/// Tree walks, image loading and published-placement probes shared by the
/// root-local list and blockquote image relayout tests (raster and vector).
/// </summary>
internal static class ContainerRelayoutTestTree
{
    internal static readonly TimeSpan AsyncDeadline = TimeSpan.FromSeconds(10);

    internal static IEnumerable<BlockBox> EnumerateBoxes(IEnumerable<BlockBox> roots)
    {
        foreach (BlockBox box in roots)
        {
            yield return box;
            switch (box)
            {
                case StackBox stack:
                    foreach (BlockBox descendant in EnumerateBoxes(stack.Children))
                        yield return descendant;
                    break;
                case ListItemBox item:
                    foreach (BlockBox descendant in EnumerateBoxes([item.Marker, item.Content]))
                        yield return descendant;
                    break;
                case TableBox table:
                    foreach (InlineContainerBox cell in table.GetCellBoxes())
                        yield return cell;
                    break;
            }
        }
    }

    internal static IEnumerable<ImageBox> EnumerateImages(IEnumerable<BlockBox> roots)
    {
        var seen = new HashSet<ImageBox>(ReferenceEqualityComparer.Instance);
        foreach (BlockBox box in EnumerateBoxes(roots))
        {
            if (box is ImageBox image && seen.Add(image))
                yield return image;
            if (box is InlineContainerBox inline)
            {
                foreach (var entry in inline.InlineImageRuns)
                    if (seen.Add(entry.Run.Image))
                        yield return entry.Run.Image;
            }
        }
    }

    internal static async Task LoadImageAsync(ImageBox image)
    {
        // The wait helper subscribes before triggering the load, which is
        // essential when repeated URLs complete synchronously from the cache.
        await DisplaySizedRasterDecodeTests.WaitForLoadAsync(image).WaitAsync(AsyncDeadline);
        await DisplaySizedRasterDecodeTests.WaitUntilAsync(
            () => image.GetWorkState().ActiveWorkCount == 0).WaitAsync(AsyncDeadline);
        Assert.NotNull(image.Bitmap);
    }

    internal static async Task LoadAllImagesAsync(IEnumerable<ImageBox> images)
    {
        foreach (ImageBox image in images)
        {
            await LoadImageAsync(image);
            Assert.Equal(0, image.GetWorkState().ActiveWorkCount);
        }
    }

    internal static SharedCanvasBitmapCache.Lease GetBitmapLease(ImageBox image) =>
        Assert.IsType<SharedCanvasBitmapCache.Lease>(image.Presentation.Lease);

    internal static int GetLeaseReferenceCount(SharedCanvasBitmapCache.Lease lease)
    {
        int? count = lease.EntryReferenceCount;
        Assert.True(count.HasValue, "The lease no longer holds a cache entry.");
        return count.Value;
    }

    internal static void AssertExactPublishedContainerOrigin(LayoutSnapshot snapshot, BlockBox root)
    {
        LayoutRootPlacement placement = GetPublishedPlacement(snapshot, root);
        Assert.Equal(placement.BaseX, placement.X);
        Assert.Equal(placement.BaseTop, placement.Top);
        Assert.Equal(placement.BaseX, (float)root.Bounds.X);
        Assert.Equal(placement.BaseTop, (float)root.Bounds.Y);
    }

    internal static LayoutRootPlacement GetPublishedPlacement(LayoutSnapshot snapshot, BlockBox root)
    {
        int ordinal = -1;
        for (int index = 0; index < snapshot.Blocks.Count; index++)
        {
            if (ReferenceEquals(snapshot.Blocks[index], root))
            {
                ordinal = index;
                break;
            }
        }
        Assert.InRange(ordinal, 0, snapshot.Blocks.Count - 1);
        return snapshot.PublishedPlacementGeneration.GetPlacement(ordinal);
    }

    internal static void AssertSourceLayoutLockReleased(LayoutSnapshot snapshot)
    {
        object gate = snapshot.Geometry.Gate;
        Assert.False(System.Threading.Monitor.IsEntered(gate),
            "Native candidate measurement/provider access must run outside the source layout lock.");
        Assert.True(System.Threading.Monitor.TryEnter(gate));
        System.Threading.Monitor.Exit(gate);
    }
}
