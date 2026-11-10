using MarkdownRenderer.GitHub.Tests.Layout.Internal.Images.Raster;
using MarkdownRenderer.GitHub.Tests.TestSupport.Fixtures;
using MarkdownRenderer.Images;
using MarkdownRenderer.Layout.Internal.Boxes;
using MarkdownRenderer.Layout.Internal.Images;
using MarkdownRenderer.Layout.Internal.Images.BitmapCache;
using MarkdownRenderer.Layout.Internal.Inlines;
using MarkdownRenderer.Layout.Internal.Inlines.Runs;
using MarkdownRenderer.Layout.Internal.Placement;
using MarkdownRenderer.Layout.Internal.PrivatePlacement;
using MarkdownRenderer.Layout.Internal.Snapshots;
using MarkdownRenderer.Layout.Internal.Tables;
using Microsoft.Graphics.Canvas;
using Windows.Foundation;
using Windows.UI;
using Xunit;
using static MarkdownRenderer.GitHub.Tests.TestSupport.Fixtures.ImageRelayoutTestSupport;

namespace MarkdownRenderer.GitHub.Tests.Layout.Internal.Relayout;

/// <summary>
/// Sources, lookups, steps and oracles for
/// <see cref="RootLocalInlineImageRelayoutTests"/>: inline image runs (alone
/// or batched with standalone image roots) that relayout through the
/// completed-image path and are compared with a protected full reflow.
/// </summary>
internal static class RootLocalInlineImageRelayoutFixture
{
    private const float LayoutWidth = RootLocalInlineImageDocument.LayoutWidth;

    internal static string CreateMixedSource(InlineRasterResolver resolver) =>
        "# Inline and standalone image placement\n\n" +
        $"Text **before** ![first inline image]({resolver.InlineFirstUrl}), middle " +
        $"![second inline image]({resolver.InlineSecondUrl}), and *after*.\n\n" +
        $"![standalone image]({resolver.StandaloneUrl})\n\n" +
        "A trailing paragraph preserves suffix placement and paint.\n";

    internal static string CreateInlineSource(InlineRasterResolver resolver) =>
        $"Prefix text ![first inline image]({resolver.InlineFirstUrl}) between " +
        $"![second inline image]({resolver.InlineSecondUrl}) suffix text.\n";

    internal static string CreateLongLazySource(InlineRasterResolver resolver) =>
        $"Front ![first inline image]({resolver.InlineFirstUrl}) and " +
        $"![second inline image]({resolver.InlineSecondUrl}) tail.\n\n" +
        $"![pending sibling]({resolver.PendingUrl})\n\n" +
        string.Join("\n\n", Enumerable.Range(0, 90).Select(static index =>
            $"Lazy sibling {index}: preserve the measured mask and pending image work.")) +
        $"\n\n![far estimated image]({resolver.FarUrl})\n";

    internal static InlineContainerBox FindInlineImageRoot(LayoutSnapshot snapshot, int expectedImages) =>
        Assert.Single(snapshot.Blocks.OfType<InlineContainerBox>(),
            inline => inline.Runs.OfType<InlineImageRun>().Count() == expectedImages);

    internal static IEnumerable<ImageBox> FindInlineImages(InlineContainerBox inline) =>
        inline.Runs.OfType<InlineImageRun>().Select(static run => run.Image);

    internal static ImageBox FindImage(LayoutSnapshot snapshot, string url) =>
        Assert.Single(EnumerateImages(snapshot), image =>
            string.Equals(image.Url, url, StringComparison.Ordinal));

    internal static IEnumerable<ImageBox> EnumerateImages(LayoutSnapshot snapshot)
    {
        foreach (BlockBox root in snapshot.Blocks)
        foreach (ImageBox image in EnumerateImages(root))
            yield return image;
    }

    internal static IEnumerable<ImageBox> EnumerateImages(BlockBox block)
    {
        switch (block)
        {
            case ImageBox image:
                yield return image;
                yield break;
            case InlineContainerBox inline:
                foreach (ImageBox image in FindInlineImages(inline))
                    yield return image;
                yield break;
            case StackBox stack:
                foreach (BlockBox child in stack.Children)
                foreach (ImageBox image in EnumerateImages(child))
                    yield return image;
                yield break;
            case ListItemBox listItem:
                foreach (ImageBox image in EnumerateImages(listItem.Marker))
                    yield return image;
                foreach (ImageBox image in EnumerateImages(listItem.Content))
                    yield return image;
                yield break;
            case TableBox table:
                foreach (InlineContainerBox cell in table.GetCellBoxes())
                foreach (ImageBox image in EnumerateImages(cell))
                    yield return image;
                yield break;
        }
    }

    internal static BlockBox FindTopLevelOwner(LayoutSnapshot snapshot, ImageBox image)
    {
        foreach (BlockBox root in snapshot.Blocks)
            if (EnumerateImages(root).Any(candidate => ReferenceEquals(candidate, image)))
                return root;
        throw new InvalidOperationException("The image is not part of this snapshot.");
    }

    internal static Color[] Paint(LayoutSnapshot snapshot, CanvasDevice device, float rasterizationScale) =>
        ImageRelayoutTestSupport.Paint(snapshot, device, rasterizationScale, LayoutWidth);

    /// <summary>
    /// Narrows the image's native visual clip to half of its run's desired
    /// width, leaving the run's desired metrics and the measured layout intact.
    /// </summary>
    internal static void ClipInlineImageToHalfDesiredWidth(
        InlineContainerBox inline,
        InlineImageRun run,
        ImageBox image)
    {
        float desiredWidth = run.DesiredWidth;
        float desiredHeight = run.DesiredHeight;
        Assert.True(desiredWidth > 2f);
        Rect priorImageBounds = image.Bounds;
        image.SetInlineBounds(new Rect(
            priorImageBounds.X,
            priorImageBounds.Y,
            desiredWidth / 2f,
            desiredHeight));
        Assert.Equal(desiredWidth, run.DesiredWidth);
        Assert.Equal(desiredWidth / 2f, image.MeasuredImageWidth);
        Assert.Equal(desiredHeight, image.MeasuredImageHeight);
        Assert.True(inline.HasMeasuredLayout);
    }

    internal static void AssertPresentationRetained(
        ImageBox image,
        CanvasBitmap bitmapBefore,
        SharedCanvasBitmapCache.Lease leaseBefore)
    {
        Assert.Same(bitmapBefore, image.Bitmap);
        Assert.Same(leaseBefore, GetBitmapLease(image));
    }

    internal static void AssertMeasuredLikeFallback(
        InlineImageRun run,
        InlineImageRun fallbackRun,
        ImageBox image,
        ImageBox fallbackImage)
    {
        Assert.Equal(fallbackRun.DesiredWidth, run.DesiredWidth);
        Assert.Equal(fallbackRun.DesiredHeight, run.DesiredHeight);
        Assert.Equal(fallbackImage.MeasuredImageWidth, image.MeasuredImageWidth);
        Assert.Equal(fallbackImage.MeasuredImageHeight, image.MeasuredImageHeight);
    }

    /// <summary>
    /// The control synchronizes inline bounds during image-plan collection,
    /// before paint. This verifies that path explicitly so Paint cannot repair
    /// a stale local placement and conceal a broken publication or index: the
    /// published inline region and the raster placement plan must match the
    /// fallback for a standalone image followed by one inline image.
    /// </summary>
    internal static void AssertPublishedInlineRegionAndPlanMatchFallback(
        LayoutSnapshot snapshot,
        LayoutSnapshot fallback,
        InlineContainerBox inline,
        InlineContainerBox fallbackInline,
        InlineImageRun run,
        ImageBox image,
        ImageBox standalone)
    {
        var publishedRegion = CollectInlineRegionUnderProductionPlacementScope(snapshot, inline);
        var fallbackRegion = CollectInlineRegionUnderProductionPlacementScope(fallback, fallbackInline);
        Assert.Same(run, publishedRegion.Run);
        Assert.Equal(
            LayoutPlacementTestGeometry.ToLogical(fallback, fallbackInline.BlockIndex, [fallbackRegion.Rect]),
            LayoutPlacementTestGeometry.ToLogical(snapshot, inline.BlockIndex, [publishedRegion.Rect]));
        Assert.Equal(LayoutPlacementTestGeometry.ToLogical(snapshot, image),
            LayoutPlacementTestGeometry.ToLogical(snapshot, inline.BlockIndex, [publishedRegion.Rect])[0]);
        var publishedPlan = new RasterPlacementImagePlan(snapshot, snapshot.Blocks, expectedCount: 2);
        Assert.Same(standalone, publishedPlan.PublicationImages[0]);
        Assert.Same(image, publishedPlan.PublicationImages[1]);
        Assert.Equal(1, publishedPlan.Index.GetBlockOrdinal(1));
        Rect logicalImageBounds = LayoutPlacementTestGeometry.ToLogical(snapshot, image);
        Assert.Equal((logicalImageBounds.Top, logicalImageBounds.Bottom),
            publishedPlan.Index.GetBounds(1));
    }

    private static (InlineImageRun Run, Rect Rect) CollectInlineRegionUnderProductionPlacementScope(
        LayoutSnapshot snapshot,
        InlineContainerBox root)
    {
        Assert.True(snapshot.TryBeginPaint());
        try
        {
            using LayoutPlacementTransformContext.Scope placementScope =
                snapshot.EnterPlacementTransformScopeForTopLevelBlockUnderPaint(root);
            return Assert.Single(root.EnumerateInlineImageRects());
        }
        finally
        {
            snapshot.EndPaint();
        }
    }

    /// <summary>
    /// Asserts that a relayout moved <paramref name="root"/>: it was a root
    /// before, and its height differs from its captured height.
    /// </summary>
    internal static void AssertRootHeightChanged(BlockBox[] rootsBefore, Rect[] boundsBefore, BlockBox root)
    {
        int ordinal = Array.FindIndex(rootsBefore, candidate => ReferenceEquals(candidate, root));
        Assert.True(ordinal >= 0);
        Assert.NotEqual(boundsBefore[ordinal].Height, root.Bounds.Height);
    }

    /// <summary>
    /// Starts the pending sibling in both documents and waits until both hold
    /// live work and the resolver has seen both pending requests.
    /// </summary>
    internal static async Task StartPendingImagesAsync(
        InlineRasterResolver resolver,
        ImageBox optimizedPending,
        ImageBox fallbackPending)
    {
        optimizedPending.EnsureLoading();
        fallbackPending.EnsureLoading();
        await resolver.WaitForPendingResolveAsync().WaitAsync(TimeSpan.FromSeconds(10));
        await DisplaySizedRasterDecodeTests.WaitUntilAsync(
            () => optimizedPending.GetWorkState().ActiveWorkCount > 0 &&
                  fallbackPending.GetWorkState().ActiveWorkCount > 0 &&
                  resolver.ResolveCountFor(resolver.PendingUrl) >= 2);
    }

    /// <summary>
    /// Asserts root identity and fallback logical placement for every root,
    /// and that the distant estimated owner kept its x, width and height.
    /// </summary>
    internal static void AssertRootsMatchFallbackKeepingEstimatedOwner(
        LayoutSnapshot fallback,
        LayoutSnapshot snapshot,
        BlockBox[] rootsBefore,
        Rect[] boundsBefore,
        BlockBox estimatedOwner)
    {
        for (int ordinal = 0; ordinal < rootsBefore.Length; ordinal++)
        {
            Assert.Same(rootsBefore[ordinal], snapshot.Blocks[ordinal]);
            Assert.Equal(LayoutPlacementTestGeometry.ToLogical(fallback, fallback.Blocks[ordinal]),
                LayoutPlacementTestGeometry.ToLogical(snapshot, snapshot.Blocks[ordinal]));
            if (ReferenceEquals(rootsBefore[ordinal], estimatedOwner))
            {
                Assert.Equal(boundsBefore[ordinal].X,
                    snapshot.Blocks[ordinal].Bounds.X);
                Assert.Equal(boundsBefore[ordinal].Width,
                    snapshot.Blocks[ordinal].Bounds.Width);
                Assert.Equal(boundsBefore[ordinal].Height,
                    snapshot.Blocks[ordinal].Bounds.Height);
            }
        }
    }

    /// <summary>
    /// The SVG output a completed inline SVG image publishes: bitmap, admitted
    /// cache lease and the provider metadata. A placement-only relayout must
    /// keep all of it.
    /// </summary>
    internal sealed record CompletedSvgOutput(
        CanvasBitmap Bitmap,
        SharedCanvasBitmapCache.Lease Lease,
        long LeaseWeight,
        MarkdownSvgDocumentInfo Info,
        Size IntrinsicSize,
        string? Title,
        string? Description,
        (int Width, int Height) RasterSize)
    {
        internal static CompletedSvgOutput Capture(ImageBox image)
        {
            CanvasBitmap bitmap = Assert.IsType<CanvasBitmap>(image.Bitmap);
            SharedCanvasBitmapCache.Lease lease = GetBitmapLease(image);
            Assert.Same(bitmap, lease.Bitmap);
            Assert.True(lease.IsBitmapOutputAdmitted);
            long leaseWeight = lease.PixelWeightBytes;
            MarkdownSvgDocumentInfo info = Assert.IsType<MarkdownSvgDocumentInfo>(image.Svg.Info);
            Size intrinsic = image.Svg.IntrinsicSize;
            string? title = image.SvgTitle;
            string? description = image.SvgDesc;
            (int Width, int Height) rasterSize = image.SvgRasterPixelSize;
            Assert.True(rasterSize.Width > 0 && rasterSize.Height > 0);
            return new CompletedSvgOutput(
                bitmap, lease, leaseWeight, info, intrinsic, title, description, rasterSize);
        }

        internal void AssertPreserved(ImageBox image)
        {
            Assert.Same(Bitmap, image.Bitmap);
            Assert.Equal(Info, image.Svg.Info);
            Assert.Equal(IntrinsicSize, image.Svg.IntrinsicSize);
            Assert.Equal(Title, image.SvgTitle);
            Assert.Equal(Description, image.SvgDesc);
            Assert.Equal(RasterSize, image.SvgRasterPixelSize);
            SharedCanvasBitmapCache.Lease currentLease = GetBitmapLease(image);
            Assert.Same(Lease, currentLease);
            Assert.Same(Bitmap, currentLease.Bitmap);
            Assert.True(currentLease.IsBitmapOutputAdmitted);
            Assert.Equal(LeaseWeight, currentLease.PixelWeightBytes);
        }
    }

    /// <summary>
    /// The image owners of a snapshot in enumeration order with their bitmap,
    /// cache lease and dirty flag, captured before a relayout.
    /// </summary>
    internal sealed class RetainedImageOwners
    {
        private readonly ImageBox[] _images;
        private readonly CanvasBitmap[] _bitmaps;
        private readonly bool[] _dirty;
        private readonly SharedCanvasBitmapCache.Lease[] _leases;

        private RetainedImageOwners(ImageBox[] images)
        {
            _images = images;
            _bitmaps = images.Select(image => Assert.IsType<CanvasBitmap>(image.Bitmap)).ToArray();
            _dirty = images.Select(static image => image.IsDirty).ToArray();
            _leases = images.Select(GetBitmapLease).ToArray();
        }

        internal static RetainedImageOwners Capture(ImageBox[] images) => new(images);

        internal void AssertRetained(LayoutSnapshot snapshot)
        {
            for (int index = 0; index < _images.Length; index++)
            {
                Assert.Same(_images[index], EnumerateImages(snapshot).ElementAt(index));
                Assert.Same(_bitmaps[index], _images[index].Bitmap);
                Assert.Same(_leases[index], GetBitmapLease(_images[index]));
                Assert.Equal(_dirty[index], _images[index].IsDirty);
            }
        }
    }
}
