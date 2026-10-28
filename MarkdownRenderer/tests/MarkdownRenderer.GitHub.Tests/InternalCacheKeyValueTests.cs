using MarkdownRenderer.CodeBlocks;
using MarkdownRenderer.Images;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Theming;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

public sealed class InternalCacheKeyValueTests
{
    [Fact]
    public void HighlightKeyPreservesRecordValueEqualityForEveryComponent()
    {
        CodeBlockHighlightCacheKey[] keys =
        [
            default, new(null, 0, 0, default, 0, 0), new(string.Empty, 0, 0, default, 0, 0),
            new("csharp", 42, 10, MarkdownThemeVariant.Light, 7, 3),
            new(new string("csharp".ToCharArray()), 42, 10, MarkdownThemeVariant.Light, 7, 3),
            new("CSharp", 42, 10, MarkdownThemeVariant.Light, 7, 3),
            new("csharp", 43, 10, MarkdownThemeVariant.Light, 7, 3),
            new("csharp", 42, 11, MarkdownThemeVariant.Light, 7, 3),
            new("csharp", 42, 10, MarkdownThemeVariant.Dark, 7, 3),
            new("csharp", 42, 10, MarkdownThemeVariant.Light, 8, 3),
            new("csharp", 42, 10, MarkdownThemeVariant.Light, 7, 4),
        ];
        foreach (CodeBlockHighlightCacheKey left in keys)
        foreach (CodeBlockHighlightCacheKey right in keys)
        {
            var originalLeft = new OriginalHighlightKey(left.Language, left.CodeHash, left.CodeLength,
                left.ThemeVariant, left.ProviderIdentity, left.ProviderRevision);
            var originalRight = new OriginalHighlightKey(right.Language, right.CodeHash, right.CodeLength,
                right.ThemeVariant, right.ProviderIdentity, right.ProviderRevision);
            bool expected = originalLeft == originalRight;
            Assert.Equal(expected, left.Equals(right));
            Assert.Equal(expected, left.Equals((object)right));
            Assert.Equal(expected, left == right);
            Assert.Equal(!expected, left != right);
            if (expected) Assert.Equal(left.GetHashCode(), right.GetHashCode());
        }
    }

    [Fact]
    public void ImageContextPreservesOrdinalAndDocumentSourceValueEquality()
    {
        var source = new MarkdownDocumentSource("readme", "owner", "repo", "main", "README.md");
        SharedLayoutImageContext[] contexts =
        [
            default, new(null, null, false, null), new(string.Empty, null, false, null),
            new("https://example.test/base/", "README.md", true, source),
            new(new string("https://example.test/base/".ToCharArray()), "README.md", true, source with { }),
            new("https://EXAMPLE.test/base/", "README.md", true, source),
            new("https://example.test/base/", "readme.md", true, source),
            new("https://example.test/base/", "README.md", false, source),
            new("https://example.test/base/", "README.md", true, source with { DocumentId = "other" }),
            new("https://example.test/base/", "README.md", true, source with { Owner = "other" }),
            new("https://example.test/base/", "README.md", true, source with { Repository = "other" }),
            new("https://example.test/base/", "README.md", true, source with { Ref = "other" }),
            new("https://example.test/base/", "README.md", true, source with { Path = "other" }),
            new("https://example.test/base/", "README.md", true, null),
        ];
        foreach (SharedLayoutImageContext left in contexts)
        foreach (SharedLayoutImageContext right in contexts)
        {
            var originalLeft = new OriginalImageContext(left.BaseUri, left.DocumentPath,
                left.AllowThirdPartyRemoteImages, left.DocumentSource);
            var originalRight = new OriginalImageContext(right.BaseUri, right.DocumentPath,
                right.AllowThirdPartyRemoteImages, right.DocumentSource);
            bool expected = originalLeft.Equals(originalRight);
            Assert.Equal(expected, left.Equals(right));
            Assert.Equal(expected, left.Equals((object)right));
            if (expected) Assert.Equal(left.GetHashCode(), right.GetHashCode());
        }
    }

    [Fact]
    public void HighlightKeysKeepCodeAndProviderRevisionsPartitionedInHashCollections()
    {
        var key = new CodeBlockHighlightCacheKey("text", 1, 5, MarkdownThemeVariant.Light, 1, 1);
        var keys = new HashSet<CodeBlockHighlightCacheKey> { key };
        Assert.False(keys.Add(new("text", 1, 5, MarkdownThemeVariant.Light, 1, 1)));
        Assert.True(keys.Add(new("text", 2, 5, MarkdownThemeVariant.Light, 1, 1)));
        Assert.True(keys.Add(new("text", 1, 5, MarkdownThemeVariant.Light, 2, 1)));
        Assert.True(keys.Add(new("text", 1, 5, MarkdownThemeVariant.Light, 1, 2)));
        Assert.Equal(4, keys.Count);
    }

    [Fact]
    public void ImageContextsReuseEqualRecordsWithoutCrossingPolicyOrSourcePartitions()
    {
        var source = new MarkdownDocumentSource("readme", "owner", "repo", "main", "README.md");
        var first = new SharedLayoutImageContext("base", "README.md", false, source);
        var entries = new Dictionary<SharedLayoutImageContext, object> { [first] = new object() };
        Assert.Same(entries[first], entries[new("base", "README.md", false, source with { })]);
        Assert.False(entries.ContainsKey(new("base", "README.md", true, source)));
        Assert.False(entries.ContainsKey(new("base", "README.md", false, source with { DocumentId = "other" })));
    }

    // Reference contracts live in the test assembly only; generated record
    // hash values are not serialized or compared across implementations.
    private readonly record struct OriginalHighlightKey(string? Language, ulong CodeHash, int CodeLength,
        MarkdownThemeVariant ThemeVariant, int ProviderIdentity, int ProviderRevision);
    private readonly record struct OriginalImageContext(string? BaseUri, string? DocumentPath,
        bool AllowThirdPartyRemoteImages, MarkdownDocumentSource? DocumentSource);
}
