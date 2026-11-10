using MarkdownRenderer.GitHub.Tests.Layout.Internal.Images.Raster;
using MarkdownRenderer.Layout.Internal.Boxes;
using MarkdownRenderer.Layout.Internal.Context;
using MarkdownRenderer.Layout.Internal.Images;
using MarkdownRenderer.Layout.Internal.Images.BitmapCache;
using MarkdownRenderer.Layout.Internal.Inlines;
using MarkdownRenderer.Layout.Internal.Inlines.Runs;
using MarkdownRenderer.Layout.Internal.Snapshots;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Windows.Foundation;
using Windows.UI;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests.TestSupport.Fixtures;

/// <summary>
/// Steps shared by the root-local completed-image relayout tests: loading an
/// image to a settled bitmap, reading its presentation owners, painting a
/// whole snapshot, and checking the published roots against their captured
/// state or against a protected full-reflow fallback.
/// </summary>
internal static class ImageRelayoutTestSupport
{
    private static readonly TimeSpan RetirementTimeout = TimeSpan.FromSeconds(10);

    internal static async Task LoadImageAsync(ImageBox image)
    {
        // WaitForLoadAsync registers before EnsureLoading; a synchronous cache
        // hit must not publish its sole completion before the test subscribes.
        await DisplaySizedRasterDecodeTests.WaitForLoadAsync(image).WaitAsync(RetirementTimeout);
        await DisplaySizedRasterDecodeTests.WaitUntilAsync(
            () => image.GetWorkState().ActiveWorkCount == 0);
        Assert.NotNull(image.Bitmap);
    }

    internal static async Task LoadImagesAsync(IEnumerable<ImageBox> images)
    {
        foreach (ImageBox image in images)
            await LoadImageAsync(image);
    }

    internal static async Task DrainAsync(MarkdownLayoutContext? context)
    {
        if (context is not null)
            await context.DrainOwnedRetirementsAsync().WaitAsync(RetirementTimeout);
    }

    internal static SharedCanvasBitmapCache.Lease GetBitmapLease(ImageBox image) =>
        Assert.IsType<SharedCanvasBitmapCache.Lease>(image.Presentation.Lease);

    internal static CanvasTextLayout GetInlineLayout(InlineContainerBox inline) =>
        Assert.IsType<CanvasTextLayout>(inline.TextLayout);

    /// <summary>
    /// Asserts that the inline root still holds the same run objects, in the
    /// same order, as before the attempted relayout.
    /// </summary>
    internal static void AssertRunsRetained(InlineContainerBox inline, InlineRun[] runsBefore)
    {
        Assert.Equal(runsBefore.Length, inline.Runs.Count);
        for (int index = 0; index < runsBefore.Length; index++)
            Assert.Same(runsBefore[index], inline.Runs[index]);
    }

    internal static Color[] Paint(
        LayoutSnapshot snapshot,
        CanvasDevice device,
        float rasterizationScale,
        float layoutWidth)
    {
        float height = (float)System.Math.Max(1, System.Math.Ceiling(snapshot.Size.Height + 1));
        using var target = new CanvasRenderTarget(device, layoutWidth, height, 96 * rasterizationScale);
        using (CanvasDrawingSession drawing = target.CreateDrawingSession())
        {
            drawing.Clear(Color.FromArgb(0, 0, 0, 0));
            snapshot.Paint(drawing, new Rect(0, 0, layoutWidth, height));
        }
        return target.GetPixelColors();
    }

    /// <summary>
    /// Asserts that every top-level root is the same object at the same
    /// ordinal with the same raw bounds as before the attempted relayout.
    /// </summary>
    internal static void AssertRootsUnchanged(
        LayoutSnapshot snapshot,
        BlockBox[] rootsBefore,
        Rect[] boundsBefore)
    {
        for (int ordinal = 0; ordinal < rootsBefore.Length; ordinal++)
        {
            Assert.Same(rootsBefore[ordinal], snapshot.Blocks[ordinal]);
            Assert.Equal(boundsBefore[ordinal], snapshot.Blocks[ordinal].Bounds);
        }
    }

    /// <summary>
    /// Asserts that every top-level root kept its identity and that its
    /// logical bounds equal the protected fallback root at the same ordinal.
    /// </summary>
    internal static void AssertRootsMatchFallback(
        LayoutSnapshot fallback,
        LayoutSnapshot snapshot,
        BlockBox[] rootsBefore)
    {
        for (int ordinal = 0; ordinal < rootsBefore.Length; ordinal++)
        {
            Assert.Same(rootsBefore[ordinal], snapshot.Blocks[ordinal]);
            Assert.Equal(LayoutPlacementTestGeometry.ToLogical(fallback, fallback.Blocks[ordinal]),
                LayoutPlacementTestGeometry.ToLogical(snapshot, snapshot.Blocks[ordinal]));
        }
    }
}
