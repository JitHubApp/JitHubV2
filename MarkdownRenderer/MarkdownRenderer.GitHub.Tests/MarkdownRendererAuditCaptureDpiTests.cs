using MarkdownRenderer.Controls;
using Microsoft.Graphics.Canvas;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

public sealed class MarkdownRendererAuditCaptureDpiTests
{
    [Theory]
    [InlineData(1.0, 643.0, 507.0)]
    [InlineData(1.25, 514.4, 405.6)]
    [InlineData(1.5, 428.6666666666667, 338.0)]
    [InlineData(2.0, 321.5, 253.5)]
    [InlineData(4.0, 160.75, 126.75)]
    public void AuditCaptureTargetUsesRasterizationScaleForFractionalDipViewports(
        double rasterizationScale,
        double viewportWidthDips,
        double viewportHeightDips)
    {
#pragma warning disable MR1001 // Exercises the compatibility control's audit-capture contract.
        float dpi = MarkdownRendererControl.GetAuditCaptureDpi(rasterizationScale);
#pragma warning restore MR1001
        Assert.Equal((float)(96d * rasterizationScale), dpi);

        using var target = new CanvasRenderTarget(
            CanvasDevice.GetSharedDevice(),
            checked((float)viewportWidthDips),
            checked((float)viewportHeightDips),
            dpi);

        Assert.Equal((uint)643, target.SizeInPixels.Width);
        Assert.Equal((uint)507, target.SizeInPixels.Height);
    }

    [Fact]
    public void AuditCapturePixelBudgetAllowsCanvasFractionalDipRoundingDown()
    {
        const double rasterizationScale = 1.25;
        float widthDips = 514.4f;
        float heightDips = 405.6f;

#pragma warning disable MR1001 // Exercises the compatibility control's audit-capture contract.
        MarkdownRendererControl.AuditCapturePixelDimensions budget =
            MarkdownRendererControl.GetAuditCapturePixelDimensions(
                widthDips,
                heightDips,
                rasterizationScale);
#pragma warning restore MR1001

        using var target = new CanvasRenderTarget(
            CanvasDevice.GetSharedDevice(),
            widthDips,
            heightDips,
            checked((float)(96d * rasterizationScale)));

        // Float DIPs can land just above an integer when scaled, while
        // CanvasRenderTarget rounds the physical size to the nearest pixel.
        Assert.Equal((uint)643, target.SizeInPixels.Width);
        Assert.Equal((uint)507, target.SizeInPixels.Height);
        Assert.Equal(644, budget.Width);
        Assert.Equal(508, budget.Height);
        Assert.True((long)target.SizeInPixels.Width <= budget.Width);
        Assert.True((long)target.SizeInPixels.Height <= budget.Height);
    }

    [Fact]
    public void AuditCapturePixelBudgetRejectsDimensionsAndPixelCountBeyondLimits()
    {
#pragma warning disable MR1001 // Exercises the compatibility control's audit-capture contract.
        Assert.Throws<InvalidOperationException>(
            () => MarkdownRendererControl.GetAuditCapturePixelDimensions(8192.1, 1, 1));
        Assert.Throws<InvalidOperationException>(
            () => MarkdownRendererControl.GetAuditCapturePixelDimensions(100, 100, 1, maximumPixels: 9_999));
#pragma warning restore MR1001
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.MaxValue)]
    [InlineData(double.Epsilon)]
    public void AuditCaptureDpiRejectsInvalidRasterizationScales(double rasterizationScale)
    {
#pragma warning disable MR1001 // Exercises the compatibility control's audit-capture contract.
        Assert.Throws<ArgumentOutOfRangeException>(
            () => MarkdownRendererControl.GetAuditCaptureDpi(rasterizationScale));
#pragma warning restore MR1001
    }
}
