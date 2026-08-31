using JitHub.Services.Accessibility;
using Xunit;

namespace JitHub.WinUI.Tests.Services;

public sealed class HighContrastVisualPolicyTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RepositoryLabel_HighContrastAlwaysUsesSystemBackedAccentPair(bool useDarkText)
    {
        RepositoryLabelBrushPolicy policy = HighContrastVisualPolicy.GetRepositoryLabelPolicy(
            isHighContrast: true,
            hasSourceColor: true,
            useDarkText);

        Assert.Equal(HighContrastVisualPolicy.AccentBrushKey, policy.BackgroundResourceKey);
        Assert.Equal(HighContrastVisualPolicy.AccentForegroundBrushKey, policy.ForegroundResourceKey);
    }

    [Theory]
    [InlineData(true, "AppLabelDarkTextBrush")]
    [InlineData(false, "AppLabelLightTextBrush")]
    public void RepositoryLabel_NormalThemesPreserveSourceColorAndCalculatedTextContrast(
        bool useDarkText,
        string expectedForeground)
    {
        RepositoryLabelBrushPolicy policy = HighContrastVisualPolicy.GetRepositoryLabelPolicy(
            isHighContrast: false,
            hasSourceColor: true,
            useDarkText);

        Assert.Null(policy.BackgroundResourceKey);
        Assert.Equal(expectedForeground, policy.ForegroundResourceKey);
    }

    [Fact]
    public void RepositoryLabel_WithoutSourceColorUsesThemeInkInNormalThemes()
    {
        RepositoryLabelBrushPolicy policy = HighContrastVisualPolicy.GetRepositoryLabelPolicy(
            isHighContrast: false,
            hasSourceColor: false,
            useDarkText: true);

        Assert.Null(policy.BackgroundResourceKey);
        Assert.Equal(HighContrastVisualPolicy.InkBrushKey, policy.ForegroundResourceKey);
    }

    [Theory]
    [InlineData(0, "AppCanvasBrush")]
    [InlineData(1, "AppAccentBrush")]
    [InlineData(42, "AppAccentBrush")]
    public void ContributionCells_HighContrastUseWindowAndHighlightBrushes(
        int contributionCount,
        string expectedBrush)
    {
        Assert.Equal(
            expectedBrush,
            HighContrastVisualPolicy.GetContributionCellBrushKey(true, contributionCount));
    }

    [Fact]
    public void ContributionCells_NormalThemesPreserveApiColor()
    {
        Assert.Null(HighContrastVisualPolicy.GetContributionCellBrushKey(false, 0));
        Assert.Null(HighContrastVisualPolicy.GetContributionCellBrushKey(false, 12));
    }

    [Theory]
    [InlineData(true, 0, "AppAccentBrush")]
    [InlineData(true, 7, "AppAccentForegroundBrush")]
    [InlineData(false, 0, "AppInkBrush")]
    [InlineData(false, 7, "AppInkBrush")]
    public void ContributionFocusRingContrastsWithItsCellFill(
        bool isHighContrast,
        int contributionCount,
        string expectedBrush)
    {
        Assert.Equal(
            expectedBrush,
            HighContrastVisualPolicy.GetContributionFocusBrushKey(isHighContrast, contributionCount));
    }
}
