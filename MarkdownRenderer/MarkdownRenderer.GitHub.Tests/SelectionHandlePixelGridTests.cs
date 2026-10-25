using MarkdownRenderer.Selection;
using Windows.Foundation;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>
/// V1-005: at 150% display scaling the 44-DIP selection handles measured
/// 65x66 (and, once snapped to whole pixels, 66x65) physical pixels in UI
/// Automation. In-process the handles were exactly 44 DIP with layout rounding
/// on, and their peer bounds sat on whole pixels plus float32 noise
/// (858.99994, width 66.00006). UI Automation truncates each edge, so an edge
/// just below a whole pixel loses one. Handle edges must sit clearly inside a
/// pixel so the reported size is the target size under truncation or rounding.
/// </summary>
public sealed class SelectionHandlePixelGridTests
{
    private const double TargetSize = 44;
    private static readonly Rect Viewport = new(0, 0, 905.3333333333334, 600);

    [Theory]
    // Renderer origins measured or derived at 150%: a third of a DIP is half
    // a physical pixel.
    [InlineData(1.5, 120.33333333333333, 101.33333587646484, 122.98667)]
    [InlineData(1.5, 120.33333333333333, 220.6666717529297, 78.31999969482422)]
    [InlineData(1.5, 0.3333333333333333, 173.3333282470703, 142.32000732421875)]
    [InlineData(2.0, 10.25, 57.1, 33.3)]
    [InlineData(1.25, 7.3, 300.7, 211.9)]
    public void HandleEdgesFallOnWholePhysicalPixelsRelativeToTheRoot(
        double scale,
        double overlayOriginX,
        double endpointX,
        double endpointY)
    {
        var origin = new Point(overlayOriginX, 13.12);
        (double X, double Y) local = SelectionController.ComputeSelectionHandleOrigin(
            new Point(endpointX, endpointY), TargetSize, Viewport, origin, scale);

        AssertBiasedPixel((origin.X + local.X) * scale, "left edge");
        AssertBiasedPixel((origin.X + local.X + TargetSize) * scale, "right edge");
        AssertBiasedPixel((origin.Y + local.Y) * scale, "top edge");
        AssertBiasedPixel((origin.Y + local.Y + TargetSize) * scale, "bottom edge");
    }

    [Theory]
    [InlineData(1.5, 120.33333333333333, 101.33333587646484, 122.98667)]
    [InlineData(2.0, 10.25, 57.1, 33.3)]
    public void SnappedHandleStaysWithinOnePhysicalPixelOfItsUnsnappedPlace(
        double scale,
        double overlayOriginX,
        double endpointX,
        double endpointY)
    {
        var origin = new Point(overlayOriginX, 13.12);
        (double X, double Y) local = SelectionController.ComputeSelectionHandleOrigin(
            new Point(endpointX, endpointY), TargetSize, Viewport, origin, scale);

        Assert.InRange(local.X - (endpointX - TargetSize / 2.0), -1.0 / scale, 1.0 / scale);
        Assert.InRange(local.Y - (endpointY - TargetSize * 0.22), -1.0 / scale, 1.0 / scale);
    }

    [Fact]
    public void HandleAtTheViewportEdgesStaysInsideTheViewport()
    {
        const double scale = 1.5;
        var origin = new Point(120.33333333333333, 13.12);
        (double X, double Y) left = SelectionController.ComputeSelectionHandleOrigin(
            new Point(-50, 4), TargetSize, Viewport, origin, scale);
        (double X, double Y) right = SelectionController.ComputeSelectionHandleOrigin(
            new Point(Viewport.Width + 50, 4), TargetSize, Viewport, origin, scale);

        Assert.True(left.X >= 0, $"left handle at {left.X}");
        Assert.True(left.Y >= 0, $"top at {left.Y}");
        Assert.True(right.X + TargetSize <= Viewport.Width + 1e-9, $"right handle ends at {right.X + TargetSize}");
        AssertBiasedPixel((origin.X + left.X) * scale, "left handle edge");
        AssertBiasedPixel((origin.X + right.X) * scale, "right handle edge");
    }

    // The overlay can move by a fraction of a pixel after a handle was placed
    // (for example a scroll before the next placement), so the reported size
    // must hold at any sub-pixel alignment, not only on the snapped grid.
    [Theory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(1.75)]
    [InlineData(2.0)]
    [InlineData(2.5)]
    public void TruncatedAutomationSizeMeetsTheTargetAtAnyAlignment(double scale)
    {
        double extent = SelectionController.ComputeSelectionHandleExtent(TargetSize, scale);
        double requiredPixels = System.Math.Ceiling(TargetSize * scale - 1e-9);
        for (int step = 0; step < 64; step++)
        {
            double left = 1000 + step / 64.0;
            foreach (double noise in new[] { -1e-4, 0, 1e-4 })
            {
                double right = left + extent * scale + noise;
                double reported = System.Math.Floor(right) - System.Math.Floor(left - noise);
                Assert.True(reported >= requiredPixels,
                    $"At scale {scale} a handle starting at {left} px reports {reported} px; the {TargetSize}-DIP target needs {requiredPixels}.");
            }
        }

        Assert.True(extent - TargetSize <= 1.0 / scale + 1e-9, "The slack must not exceed one physical pixel.");
    }

    [Fact]
    public void LiveHandlePlacementUsesTheRootPixelGridSnapping()
    {
        string control = ReadControlSource();
        int start = control.IndexOf("private bool PlaceSelectionHandle(", System.StringComparison.Ordinal);
        Assert.True(start >= 0, "PlaceSelectionHandle was not found.");
        int end = control.IndexOf("private Point GetSelectionOverlayOriginInRoot()", start, System.StringComparison.Ordinal);
        Assert.True(end > start, "GetSelectionOverlayOriginInRoot was not found after PlaceSelectionHandle.");
        string body = control[start..end];
        Assert.Contains("SelectionController.ComputeSelectionHandleOrigin(", body);
        Assert.Contains("GetSelectionOverlayOriginInRoot()", body);
        Assert.Contains("RasterizationScale", body);
        Assert.DoesNotContain("Math.Clamp(endpoint.X - targetSize / 2.0", body);
    }

    private static string ReadControlSource()
    {
        for (System.IO.DirectoryInfo? directory = new(System.AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            string path = System.IO.Path.Combine(directory.FullName, "MarkdownRenderer", "MarkdownRenderer", "Controls", "MarkdownRendererControl.cs");
            if (System.IO.File.Exists(path))
                return System.IO.File.ReadAllText(path);
        }
        throw new System.IO.FileNotFoundException("Could not locate MarkdownRendererControl.cs.");
    }

    private static void AssertBiasedPixel(double physical, string what)
    {
        double fraction = physical - System.Math.Floor(physical);
        Assert.True(System.Math.Abs(fraction - SelectionController.SelectionHandlePixelBias) < 1e-6,
            $"The handle {what} is at {physical} physical pixels, not a {SelectionController.SelectionHandlePixelBias} px bias past a whole pixel.");
    }

    [Theory]
    [InlineData(1.5, 120.33333333333333, 101.33333587646484, 122.98667)]
    [InlineData(1.5, 120.33333333333333, 220.6666717529297, 78.31999969482422)]
    [InlineData(1.5, 399.3333333333333, 173.3333282470703, 142.32000732421875)]
    [InlineData(2.0, 10.25, 57.1, 33.3)]
    [InlineData(1.25, 7.3, 300.7, 211.9)]
    [InlineData(1.75, 33.1428571, 412.5714, 96.2857)]
    public void TruncatedAutomationBoundsKeepTheFullTargetUnderTransformNoise(
        double scale,
        double overlayOriginX,
        double endpointX,
        double endpointY)
    {
        var origin = new Point(overlayOriginX, 13.12);
        (double X, double Y) local = SelectionController.ComputeSelectionHandleOrigin(
            new Point(endpointX, endpointY), TargetSize, Viewport, origin, scale);
        double targetPixels = TargetSize * scale;
        double expected = System.Math.Floor(targetPixels + 1e-9);
        double[] noise = { -1e-4, 0, 1e-4 };
        foreach (double leftNoise in noise)
        {
            foreach (double rightNoise in noise)
            {
                double left = (origin.X + local.X) * scale + leftNoise;
                double right = (origin.X + local.X + TargetSize) * scale + rightNoise;
                double top = (origin.Y + local.Y) * scale + leftNoise;
                double bottom = (origin.Y + local.Y + TargetSize) * scale + rightNoise;
                Assert.Equal(expected, System.Math.Floor(right) - System.Math.Floor(left));
                Assert.Equal(expected, System.Math.Floor(bottom) - System.Math.Floor(top));
                Assert.Equal(expected, System.Math.Round(right) - System.Math.Round(left));
                Assert.Equal(expected, System.Math.Round(bottom) - System.Math.Round(top));
            }
        }
    }
}
