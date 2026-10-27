using Xunit;

namespace MarkdownRenderer.Math.Tests;

public sealed class MathDelimiterScannerCallbackTests
{
    [Fact]
    public void Scan_CachesEachLocalizedDelimiterKeyForRepeatedDiagnostics()
    {
        const string source = "$alpha\n$beta\n$$gamma";
        var strings = new CountingStringProvider();

        MathDelimiterScanResult result = MathDelimiterScanner.Scan(
            source,
            sourceOffset: 0,
            languageTag: "fr-FR",
            strings);

        Assert.Empty(result.Formulas);
        Assert.Collection(
            result.Diagnostics,
            diagnostic => Assert.Equal("Localized unmatched inline.", diagnostic.Message),
            diagnostic => Assert.Equal("Localized unmatched inline.", diagnostic.Message),
            diagnostic => Assert.Equal("Localized unmatched display.", diagnostic.Message));
        Assert.Equal(1, strings.GetCallCount(MathStringKeys.UnmatchedInlineDelimiter));
        Assert.Equal(1, strings.GetCallCount(MathStringKeys.UnmatchedDisplayDelimiter));
    }

    [Fact]
    public void Scan_KeepsLocalizedDelimiterCacheScopedToOneInvocation()
    {
        const string source = "$alpha\n$beta\n$$gamma";
        var strings = new CountingStringProvider();

        MathDelimiterScanResult first = MathDelimiterScanner.Scan(
            source,
            sourceOffset: 0,
            languageTag: "fr-FR",
            strings);
        MathDelimiterScanResult second = MathDelimiterScanner.Scan(
            source,
            sourceOffset: 0,
            languageTag: "fr-FR",
            strings);

        Assert.Equal(3, first.Diagnostics.Length);
        Assert.Equal(3, second.Diagnostics.Length);
        string[] expectedMessages =
        [
            "Localized unmatched inline.",
            "Localized unmatched inline.",
            "Localized unmatched display.",
        ];
        Assert.Equal(expectedMessages, first.Diagnostics.Select(static diagnostic => diagnostic.Message));
        Assert.Equal(expectedMessages, second.Diagnostics.Select(static diagnostic => diagnostic.Message));
        Assert.Equal(2, strings.GetCallCount(MathStringKeys.UnmatchedInlineDelimiter));
        Assert.Equal(2, strings.GetCallCount(MathStringKeys.UnmatchedDisplayDelimiter));
    }

    private sealed class CountingStringProvider : IMathStringProvider
    {
        private readonly Dictionary<string, int> _callCounts = new(StringComparer.Ordinal);

        public string? GetString(string resourceKey, string? languageTag)
        {
            _callCounts.TryGetValue(resourceKey, out int count);
            _callCounts[resourceKey] = count + 1;
            return resourceKey switch
            {
                MathStringKeys.UnmatchedInlineDelimiter => "Localized unmatched inline.",
                MathStringKeys.UnmatchedDisplayDelimiter => "Localized unmatched display.",
                _ => null,
            };
        }

        internal int GetCallCount(string resourceKey) =>
            _callCounts.TryGetValue(resourceKey, out int count) ? count : 0;
    }
}
