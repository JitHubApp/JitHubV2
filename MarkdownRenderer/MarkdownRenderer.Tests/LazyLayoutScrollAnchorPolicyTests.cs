using MarkdownRenderer.Controls;
using Xunit;

namespace MarkdownRenderer.Tests;

public sealed class LazyLayoutScrollAnchorPolicyTests
{
    [Theory]
    [InlineData(100, 100, true)]
    [InlineData(100, 100.49, true)]
    [InlineData(100, 100.5, false)]
    [InlineData(100, 0, false)]
    [InlineData(100, 200, false)]
    public void RestoreOnlyWhenViewportHasNotMoved(
        double capturedOffset,
        double currentOffset,
        bool expected)
    {
        Assert.Equal(expected, LazyLayoutScrollAnchorPolicy.ShouldRestore(
            capturedOffset, currentOffset, scrollInProgress: false));
    }

    [Fact]
    public void ActiveScrollMustNotBeRetargetedByLazyLayoutCommit()
    {
        Assert.False(LazyLayoutScrollAnchorPolicy.ShouldRestore(
            100, 100, scrollInProgress: true));
    }

    [Fact]
    public void MissingOrNonFiniteOffsetsCannotRestore()
    {
        Assert.False(LazyLayoutScrollAnchorPolicy.ShouldRestore(null, 100, false));
        Assert.False(LazyLayoutScrollAnchorPolicy.ShouldRestore(100, null, false));
        Assert.False(LazyLayoutScrollAnchorPolicy.ShouldRestore(double.NaN, 100, false));
        Assert.False(LazyLayoutScrollAnchorPolicy.ShouldRestore(100, double.PositiveInfinity, false));
    }
}
