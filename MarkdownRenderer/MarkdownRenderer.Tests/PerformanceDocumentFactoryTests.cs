using MarkdownRenderer.PerformanceHarness;
using Xunit;

namespace MarkdownRenderer.Tests;

public sealed class PerformanceDocumentFactoryTests
{
    [Theory]
    [InlineData(1024 * 1024, MarkdownParseLimits.DefaultMaximumSourceLength)]
    [InlineData(10 * 1024 * 1024, 5 * 1024 * 1024)]
    public void HarnessParseLimitAdmitsEveryMeasuredCorpus(
        int sourceUtf16Bytes,
        int expectedMaximumCharacters)
    {
        MarkdownParseLimits limits = PerformanceParsePolicy.CreateLimits(sourceUtf16Bytes);

        Assert.Equal(expectedMaximumCharacters, limits.MaximumSourceLength);
        Assert.True(limits.MaximumOutstandingSourceBytes >= sourceUtf16Bytes);
    }

    [Fact]
    public void WarmScrollStressCorpusHasFrozenSizeAndAdversarialContainerShape()
    {
        const int sourceBytes = 1024 * 1024;

        string source = PerformanceDocumentFactory.CreateWarmScrollStress(sourceBytes);

        Assert.Equal(sourceBytes, source.Length * sizeof(char));
        Assert.Equal("adversarial-nested-table-code-v1", PerformanceDocumentFactory.WarmScrollStressCorpus);
        Assert.Equal(2_048, Count(source, "- parent item "));
        Assert.Equal(1_024, Count(source, "  - nested item "));
        Assert.Equal(2_048, Count(source, "|viewport-indexed table row "));
        Assert.Equal(5_000, Count(source, "var viewportLine"));
        Assert.Contains("```csharp\n", source, StringComparison.Ordinal);
        Assert.Contains("\n```\n", source, StringComparison.Ordinal);
    }

    private static int Count(string source, string value)
    {
        int count = 0;
        int offset = 0;
        while ((offset = source.IndexOf(value, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += value.Length;
        }

        return count;
    }
}
