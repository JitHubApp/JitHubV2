using MarkdownRenderer.PerformanceHarness;
using Xunit;

namespace MarkdownRenderer.Tests;

public sealed class FirstViewportStageProfileSelectionTests
{
    [Fact]
    public void UnconfiguredProfileKeepsTheExistingOneMiBDefault()
    {
        Assert.Equal(
            1024 * 1024,
            FirstViewportStageProfileSelection.Resolve(
                quick: true,
                outputPathConfigured: false,
                configuredSourceBytes: null).SourceUtf16Bytes);
    }

    [Fact]
    public void EmptyEnvironmentValueBehavesLikeAnUnsetSelector()
    {
        Assert.Equal(
            1024 * 1024,
            FirstViewportStageProfileSelection.Resolve(
                quick: true,
                outputPathConfigured: false,
                configuredSourceBytes: string.Empty).SourceUtf16Bytes);
    }

    [Theory]
    [InlineData("102400", 100 * 1024)]
    [InlineData("1048576", 1024 * 1024)]
    [InlineData("10485760", 10 * 1024 * 1024)]
    public void OptInProfileAcceptsOnlyTheThreeExactSourceByteConditions(
        string value,
        int expectedBytes)
    {
        FirstViewportStageProfileSelection selection =
            FirstViewportStageProfileSelection.Resolve(
                quick: true,
                outputPathConfigured: true,
                configuredSourceBytes: value);

        Assert.Equal(expectedBytes, selection.SourceUtf16Bytes);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void OptInProfileWithoutSizeSelectionKeepsOneMiB(string? configuredSourceBytes)
    {
        Assert.Equal(
            1024 * 1024,
            FirstViewportStageProfileSelection.Resolve(
                quick: true,
                outputPathConfigured: true,
                configuredSourceBytes).SourceUtf16Bytes);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("102399")]
    [InlineData("102401")]
    [InlineData("1MiB")]
    [InlineData(" 102400")]
    [InlineData("10485760 ")]
    public void UnsupportedOrNonExactSourceByteValuesAreRejected(string value)
    {
        Assert.Throws<ArgumentException>(() =>
            FirstViewportStageProfileSelection.Resolve(
                quick: true,
                outputPathConfigured: true,
                configuredSourceBytes: value));
    }

    [Fact]
    public void SourceSizeSelectorCannotEnableOrChangeAReleaseMeasurement()
    {
        Assert.Throws<ArgumentException>(() =>
            FirstViewportStageProfileSelection.Resolve(
                quick: false,
                outputPathConfigured: true,
                configuredSourceBytes: "10485760"));
    }

    [Fact]
    public void OptInProfilePathRemainsQuickOnlyWithTheDefaultSize()
    {
        Assert.Throws<ArgumentException>(() =>
            FirstViewportStageProfileSelection.Resolve(
                quick: false,
                outputPathConfigured: true,
                configuredSourceBytes: null));
    }

    [Fact]
    public void SourceSizeSelectorRequiresTheOptInProfilePath()
    {
        Assert.Throws<ArgumentException>(() =>
            FirstViewportStageProfileSelection.Resolve(
                quick: true,
                outputPathConfigured: false,
                configuredSourceBytes: "102400"));
    }
}
