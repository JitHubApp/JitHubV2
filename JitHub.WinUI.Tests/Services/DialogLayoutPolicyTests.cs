using JitHub.Services;
using Xunit;

namespace JitHub.WinUI.Tests.Services;

public sealed class DialogLayoutPolicyTests
{
    [Theory]
    [InlineData(1366, 900, 24, 620, 720)]
    [InlineData(760, 650, 24, 620, 602)]
    [InlineData(640, 600, 24, 592, 552)]
    [InlineData(520, 560, 12, 496, 536)]
    [InlineData(320, 480, 12, 296, 456)]
    public void StandardDialog_StaysInsideViewport(
        double width,
        double height,
        double expectedMargin,
        double expectedWidth,
        double expectedHeight)
    {
        DialogLayoutMetrics metrics = DialogLayoutPolicy.Calculate(width, height);

        Assert.Equal(expectedMargin, metrics.OuterMargin);
        Assert.Equal(expectedWidth, metrics.MaximumWidth);
        Assert.Equal(expectedHeight, metrics.MaximumHeight);
        Assert.True(metrics.MinimumWidth <= metrics.MaximumWidth);
        Assert.True(metrics.MaximumWidth + (metrics.OuterMargin * 2) <= width);
        Assert.True(metrics.MaximumHeight + (metrics.OuterMargin * 2) <= height);
    }

    [Theory]
    [InlineData(1366, 900, 840)]
    [InlineData(900, 700, 840)]
    [InlineData(760, 650, 712)]
    [InlineData(520, 560, 496)]
    public void EditorDialog_UsesAvailableWidthWithoutEscapingViewport(
        double width,
        double height,
        double expectedWidth)
    {
        DialogLayoutMetrics metrics = DialogLayoutPolicy.Calculate(
            width,
            height,
            AppDialogLayoutKind.Editor);

        Assert.Equal(expectedWidth, metrics.MaximumWidth);
        Assert.True(metrics.MaximumWidth + (metrics.OuterMargin * 2) <= width);
        Assert.True(metrics.MaximumHeight + (metrics.OuterMargin * 2) <= height);
    }

    [Fact]
    public void InvalidViewport_UsesFiniteSafeDefaults()
    {
        DialogLayoutMetrics metrics = DialogLayoutPolicy.Calculate(double.NaN, double.PositiveInfinity);

        Assert.All(
            new[]
            {
                metrics.OuterMargin,
                metrics.MinimumWidth,
                metrics.MaximumWidth,
                metrics.MaximumHeight
            },
            value => Assert.True(double.IsFinite(value) && value >= 0));
    }

    [Fact]
    public void OpenDialog_TracksOwnerResizeInBothDirections()
    {
        DialogLayoutMetrics wide = DialogLayoutPolicy.Calculate(1366, 900, AppDialogLayoutKind.Editor);
        DialogLayoutMetrics compact = DialogLayoutPolicy.Calculate(520, 560, AppDialogLayoutKind.Editor);
        DialogLayoutMetrics restored = DialogLayoutPolicy.Calculate(1180, 800, AppDialogLayoutKind.Editor);

        Assert.True(compact.MaximumWidth < wide.MaximumWidth);
        Assert.True(compact.MaximumHeight < wide.MaximumHeight);
        Assert.True(restored.MaximumWidth > compact.MaximumWidth);
        Assert.True(restored.MaximumHeight > compact.MaximumHeight);
        Assert.Equal(840, restored.MaximumWidth);
        Assert.Equal(720, restored.MaximumHeight);

        Assert.All(new[] { wide, compact, restored }, metrics =>
        {
            Assert.True(metrics.MinimumWidth <= metrics.MaximumWidth);
            Assert.True(double.IsFinite(metrics.MaximumWidth));
            Assert.True(double.IsFinite(metrics.MaximumHeight));
        });
    }
}
