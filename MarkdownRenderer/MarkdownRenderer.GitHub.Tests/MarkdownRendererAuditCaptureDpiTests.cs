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
