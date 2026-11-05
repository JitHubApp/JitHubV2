using JitHub.Services.Markdown.Automation;
using Xunit;

namespace JitHub.WinUI.Tests.Services;

public sealed class MarkdownLifecycleTextScaleTests
{
    [Theory]
    [InlineData(2.0, 1.0, 2.0)]
    [InlineData(2.0, 2.0, 1.0)]
    [InlineData(1.5, 1.25, 1.2)]
    [InlineData(3.0, 1.5, 2.0)]
    public void SystemScaleIsDividedOutOfTheRequestedScale(double requested, double system, double expected)
    {
        Assert.Equal(expected, MarkdownLifecycleTextScale.ToTokenScale(requested, system), 10);
    }

    [Theory]
    [InlineData(0.5, 1.0)]
    [InlineData(5.0, 3.0)]
    public void RequestedScaleIsClampedToOneThroughThree(double requested, double expected)
    {
        Assert.Equal(expected, MarkdownLifecycleTextScale.ToTokenScale(requested, 1.0), 10);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void UnreadableOrInvalidSystemScaleCountsAsOne(double? system)
    {
        Assert.Equal(2.0, MarkdownLifecycleTextScale.ToTokenScale(2.0, system), 10);
    }

    [Fact]
    public void SystemScaleIsClampedBeforeItIsDividedOut()
    {
        Assert.Equal(2.0, MarkdownLifecycleTextScale.ToTokenScale(2.0, 0.5), 10);
        Assert.Equal(1.0, MarkdownLifecycleTextScale.ToTokenScale(3.0, 4.0), 10);
    }
}
