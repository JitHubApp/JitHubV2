using MarkdownRenderer.Controls;
using Xunit;

namespace MarkdownRenderer.Tests;

public sealed class MarkdownLazyLayoutPolicyTests
{
    [Theory]
    [InlineData(127, 32 * 1024 - 1, false)]
    [InlineData(0, 0, false)]
    [InlineData(128, 0, true)]
    [InlineData(0, 32 * 1024, true)]
    public void ShouldUse_KeepsSmallOrCustomMeasuredDocumentsEager(
        int blockCount,
        int sourceLength,
        bool hasCustomMeasurement)
    {
        Assert.False(MarkdownLazyLayoutPolicy.ShouldUse(
            blockCount,
            sourceLength,
            hasCustomMeasurement));
    }

    [Theory]
    [InlineData(128, 0)]
    [InlineData(227, 51_200)]
    [InlineData(1, 32 * 1024)]
    public void ShouldUse_VirtualizesLargeBlockSetsOrLongSources(
        int blockCount,
        int sourceLength)
    {
        Assert.True(MarkdownLazyLayoutPolicy.ShouldUse(
            blockCount,
            sourceLength,
            hasCustomBlockEmbedMeasurement: false));
    }

    [Fact]
    public void ShouldUse_RejectsInvalidMetrics()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            MarkdownLazyLayoutPolicy.ShouldUse(-1, 0, false));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            MarkdownLazyLayoutPolicy.ShouldUse(0, -1, false));
    }
}
