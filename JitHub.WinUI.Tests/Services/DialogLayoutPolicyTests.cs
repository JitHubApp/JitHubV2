using JitHub.Services;
using Xunit;

namespace JitHub.WinUI.Tests.Services;

public sealed class DialogLayoutPolicyTests
{
    private static readonly DialogLayoutTokenSet Tokens = new(
        CompactBreakpoint: 640,
        CompactMargin: 12,
        StandardMargin: 24,
        ConfirmationPreferredWidth: 620,
        CompactFormPreferredWidth: 480,
        StandardPreferredWidth: 620,
        EditorPreferredWidth: 840,
        ConfirmationPreferredHeight: 360,
        CompactFormPreferredHeight: 340,
        StandardPreferredHeight: 520,
        EditorPreferredHeight: 720,
        PreferredMinimumWidth: 320);

    [Theory]
    [InlineData(1366, 900, 620, 360)]
    [InlineData(640, 600, 592, 360)]
    [InlineData(320, 360, 296, 336)]
    public void ConfirmationDialog_UsesCompactStableEnvelope(
        double width,
        double height,
        double expectedWidth,
        double expectedHeight)
    {
        DialogLayoutMetrics metrics = DialogLayoutPolicy.Calculate(
            width,
            height,
            Tokens,
            AppDialogLayoutKind.Confirmation);

        Assert.Equal(expectedWidth, metrics.MaximumWidth);
        Assert.Equal(expectedHeight, metrics.MaximumHeight);
        Assert.True(metrics.MaximumWidth + (metrics.OuterMargin * 2) <= width);
        Assert.True(metrics.MaximumHeight + (metrics.OuterMargin * 2) <= height);
    }

    [Theory]
    [InlineData(1366, 900, 480, 340)]
    [InlineData(520, 560, 480, 340)]
    [InlineData(320, 480, 296, 340)]
    public void CompactFormDialog_UsesStableResponsiveEnvelope(
        double width,
        double height,
        double expectedWidth,
        double expectedHeight)
    {
        DialogLayoutMetrics metrics = DialogLayoutPolicy.Calculate(
            width,
            height,
            Tokens,
            AppDialogLayoutKind.CompactForm);

        Assert.Equal(expectedWidth, metrics.MaximumWidth);
        Assert.Equal(expectedHeight, metrics.MaximumHeight);
        Assert.True(metrics.MinimumWidth <= metrics.MaximumWidth);
        Assert.True(metrics.MaximumWidth + (metrics.OuterMargin * 2) <= width);
        Assert.True(metrics.MaximumHeight + (metrics.OuterMargin * 2) <= height);
    }

    [Theory]
    [InlineData(1366, 900, 24, 620, 520)]
    [InlineData(760, 650, 24, 620, 520)]
    [InlineData(640, 600, 24, 592, 520)]
    [InlineData(520, 560, 12, 496, 520)]
    [InlineData(320, 480, 12, 296, 456)]
    public void StandardDialog_StaysInsideViewport(
        double width,
        double height,
        double expectedMargin,
        double expectedWidth,
        double expectedHeight)
    {
        DialogLayoutMetrics metrics = DialogLayoutPolicy.Calculate(width, height, Tokens);

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
            Tokens,
            AppDialogLayoutKind.Editor);

        Assert.Equal(expectedWidth, metrics.MaximumWidth);
        Assert.True(metrics.MaximumWidth + (metrics.OuterMargin * 2) <= width);
        Assert.True(metrics.MaximumHeight + (metrics.OuterMargin * 2) <= height);
    }

    [Fact]
    public void InvalidViewport_UsesFiniteSafeDefaults()
    {
        DialogLayoutMetrics metrics = DialogLayoutPolicy.Calculate(
            double.NaN,
            double.PositiveInfinity,
            Tokens);

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
        DialogLayoutMetrics wide = DialogLayoutPolicy.Calculate(1366, 900, Tokens, AppDialogLayoutKind.Editor);
        DialogLayoutMetrics compact = DialogLayoutPolicy.Calculate(520, 560, Tokens, AppDialogLayoutKind.Editor);
        DialogLayoutMetrics restored = DialogLayoutPolicy.Calculate(1180, 800, Tokens, AppDialogLayoutKind.Editor);

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
