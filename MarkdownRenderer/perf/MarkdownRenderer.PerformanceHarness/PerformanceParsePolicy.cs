namespace MarkdownRenderer.PerformanceHarness;

internal static class PerformanceParsePolicy
{
    internal static MarkdownParseLimits CreateLimits(int sourceUtf16Bytes)
    {
        if (sourceUtf16Bytes <= 0 || (sourceUtf16Bytes & 1) != 0)
            throw new ArgumentOutOfRangeException(nameof(sourceUtf16Bytes));

        int sourceCharacters = sourceUtf16Bytes / sizeof(char);
        return new MarkdownParseLimits(
            maximumSourceLength: Math.Max(
                MarkdownParseLimits.DefaultMaximumSourceLength,
                sourceCharacters));
    }
}
