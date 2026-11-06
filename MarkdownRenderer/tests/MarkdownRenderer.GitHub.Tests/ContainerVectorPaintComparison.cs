using System.Numerics;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using Microsoft.Graphics.Canvas;
using Windows.Foundation;
using Windows.UI;
using Xunit;
using static MarkdownRenderer.GitHub.Tests.ContainerRelayoutTestTree;
using static MarkdownRenderer.GitHub.Tests.RootLocalContainerVectorRelayoutHarness;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>
/// Paints a transferred container-vector snapshot and its fresh fallback and
/// reports the first differing pixel with its root placements and the inline
/// boxes under it.
/// </summary>
internal static class ContainerVectorPaintComparison
{
    internal static void AssertPaintMatchesFallback(
        LayoutSnapshot snapshot,
        LayoutSnapshot fallback,
        CanvasDevice device,
        float rasterizationScale,
        Matrix3x2? callerTransform = null)
    {
        Color[] actualPixels = Paint(snapshot, device, rasterizationScale, callerTransform);
        Color[] expectedPixels = Paint(fallback, device, rasterizationScale, callerTransform);
        int firstMismatch = -1;
        for (int index = 0; index < expectedPixels.Length; index++)
        {
            if (actualPixels[index] == expectedPixels[index])
                continue;
            firstMismatch = index;
            break;
        }
        Assert.True(firstMismatch < 0, firstMismatch < 0
            ? null
            : DescribeFirstPixelMismatch(
                snapshot, fallback, expectedPixels, actualPixels, firstMismatch, rasterizationScale));
    }

    private static Color[] Paint(
        LayoutSnapshot snapshot,
        CanvasDevice device,
        float rasterizationScale,
        Matrix3x2? callerTransform)
    {
        float height = (float)System.Math.Max(1, System.Math.Ceiling(snapshot.Size.Height + 1));
        using var target = new CanvasRenderTarget(device, LayoutWidth, height, 96 * rasterizationScale);
        using (CanvasDrawingSession drawing = target.CreateDrawingSession())
        {
            drawing.Clear(Color.FromArgb(0, 0, 0, 0));
            if (callerTransform is Matrix3x2 transform)
                drawing.Transform = transform;
            snapshot.Paint(drawing, new Rect(0, 0, LayoutWidth, height));
        }
        return target.GetPixelColors();
    }

    private static string DescribeFirstPixelMismatch(
        LayoutSnapshot snapshot,
        LayoutSnapshot fallback,
        Color[] expected,
        Color[] actual,
        int pixelIndex,
        float rasterizationScale)
    {
        int pixelWidth = (int)System.Math.Ceiling(LayoutWidth * rasterizationScale);
        int pixelX = pixelIndex % pixelWidth;
        int pixelY = pixelIndex / pixelWidth;
        float dipX = pixelX / rasterizationScale;
        float dipY = pixelY / rasterizationScale;
        LayoutPlacementGeneration placements = snapshot.PublishedPlacementGeneration;
        string owners = string.Join("; ", Enumerable.Range(0, snapshot.Blocks.Count)
            .Select(ordinal =>
            {
                LayoutRootPlacement placement = placements.GetPlacement(ordinal);
                return $"{ordinal}:{snapshot.Blocks[ordinal].GetType().Name} " +
                    $"base={placement.BaseX},{placement.BaseTop},{placement.Width},{placement.Height} " +
                    $"logical={placement.X},{placement.Top},{placement.Width},{placement.Height}";
            }));
        string actualInline = DescribeInlineBoxesAt(snapshot, new Point(dipX, dipY));
        string fallbackInline = DescribeInlineBoxesAt(fallback, new Point(dipX, dipY));
        return $"rasterScale={rasterizationScale}; pixel=({pixelX},{pixelY}); dip=({dipX},{dipY}); " +
            $"expected={expected[pixelIndex]}; actual={actual[pixelIndex]}; roots=[{owners}]; " +
            $"actualInline=[{actualInline}]; fallbackInline=[{fallbackInline}]";
    }

    private static string DescribeInlineBoxesAt(LayoutSnapshot snapshot, Point point)
    {
        if (!snapshot.TryBeginInteraction())
            return "unavailable";
        try
        {
            return string.Join("; ", EnumerateBoxes(snapshot.Blocks)
                .OfType<InlineContainerBox>()
                .Where(inline => LayoutPlacementTestGeometry.ToLogical(snapshot, inline).Contains(point))
                .Select(inline => DescribeInlineBox(snapshot, inline)));
        }
        finally
        {
            snapshot.EndInteraction();
        }
    }

    private static string DescribeInlineBox(LayoutSnapshot snapshot, InlineContainerBox inline)
    {
        using LayoutPlacementTransformContext.Scope placementScope =
            snapshot.EnterPlacementTransformScopeForBlockIndexUnderPaint(inline.BlockIndex);
        inline.GetSceneGeometryOrigin(out double originX, out double originY);
        snapshot.PlacementTransform.GetCurrentTranslation(out float translationX, out float translationY);
        float paintOriginX = (float)originX;
        float paintOriginY = (float)originY;
        float finalPaintOriginX = paintOriginX + translationX;
        float finalPaintOriginY = paintOriginY + translationY;
        string runs = string.Join(",", inline.Runs.Select(run => run switch
        {
            InlineVectorSceneRun vector =>
                $"vector:{vector.LocalBounds.X},{vector.LocalBounds.Y},{vector.LocalBounds.Width},{vector.LocalBounds.Height}",
            InlineImageRun image => $"image:{image.DesiredWidth},{image.DesiredHeight}",
            _ => run.GetType().Name,
        }));
        return $"block={inline.BlockIndex}; bounds={LayoutPlacementTestGeometry.ToLogical(snapshot, inline)}; " +
            $"origin={originX},{originY}; translation={FormatFloat(translationX)},{FormatFloat(translationY)}; " +
            $"localPaintOrigin={FormatFloat(paintOriginX)},{FormatFloat(paintOriginY)}; " +
            $"finalPaintOrigin={FormatFloat(finalPaintOriginX)},{FormatFloat(finalPaintOriginY)}; runs=[{runs}]";
    }

    private static string FormatFloat(float value) =>
        $"{value:R}/0x{BitConverter.SingleToInt32Bits(value):X8}";
}
