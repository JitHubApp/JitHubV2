using System.Text;
using MarkdownRenderer.Document;
using Xunit;

namespace MarkdownRenderer.Tests;

public sealed class AdversarialMarkdownPropertyTests
{
    [Fact]
    public async Task RandomizedMarkdown_PreservesUtf16AndAllPublicRangesStayInBounds()
    {
        MarkdownProfile[] profiles =
        [
            MarkdownProfiles.CommonMark,
            MarkdownProfiles.GfmStrict,
            MarkdownProfiles.GitHubReadme,
            MarkdownProfiles.MarkdownExtra,
        ];
        var random = new Random(0x4D_44_31_30);

        foreach (MarkdownProfile profile in profiles)
        {
            MarkdownEngine engine = new MarkdownEngineBuilder()
                .UseProfile(profile)
                .WithParseCacheBudgetBytes(0)
                .Build();

            for (int iteration = 0; iteration < 160; iteration++)
            {
                string source = CreateAdversarialSource(random, random.Next(0, 4097));
                MarkdownDocument document = await engine.ParseAsync(source);

                Assert.Equal(source, document.Source);
                AssertRangesAreValidAndOrdered(document.SourceMap, source.Length);
                Assert.All(document.Diagnostics, diagnostic =>
                    AssertRangeIsValid(diagnostic.SourceSpan, source.Length));
                Assert.All(document.GetHeadings(), item => AssertRangeIsValid(item.SourceSpan, source.Length));
                Assert.All(document.GetLinks(), item => AssertRangeIsValid(item.SourceSpan, source.Length));
                Assert.All(document.GetCodeBlocks(), item => AssertRangeIsValid(item.SourceSpan, source.Length));
                Assert.All(document.GetImages(), item => AssertRangeIsValid(item.SourceSpan, source.Length));
                Assert.All(document.GetFootnotes(), item => AssertRangeIsValid(item.SourceSpan, source.Length));
                Assert.All(document.GetDefinitionItems(), item => AssertRangeIsValid(item.SourceSpan, source.Length));
                Assert.All(document.GetAbbreviations(), item => AssertRangeIsValid(item.SourceSpan, source.Length));
                Assert.All(document.GetFragments(), item => AssertRangeIsValid(item.SourceSpan, source.Length));
            }
        }
    }

    private static void AssertRangesAreValidAndOrdered(
        IReadOnlyList<MarkdownSourceMapEntry> entries,
        int sourceLength)
    {
        int previousStart = -1;
        foreach (MarkdownSourceMapEntry entry in entries)
        {
            AssertRangeIsValid(entry.SourceSpan, sourceLength);
            Assert.True(
                entry.SourceSpan.Start >= previousStart,
                $"Source map moved backwards from {previousStart} to {entry.SourceSpan.Start}.");
            previousStart = entry.SourceSpan.Start;
        }
    }

    private static void AssertRangeIsValid(SourceSpan range, int sourceLength)
    {
        Assert.InRange(range.Start, 0, sourceLength);
        Assert.InRange(range.Length, 0, sourceLength - range.Start);
        Assert.Equal(range.Start + range.Length, range.End);
    }

    private static string CreateAdversarialSource(Random random, int targetLength)
    {
        string[] tokens =
        [
            "# ", "## ", "- ", "1. ", "> ", "```", "~~~", "|", "---", "*", "_",
            "[", "]", "(", ")", "![", "<", ">", "&amp;", "&#x1F9EA;", "\\", "`",
            "\r\n", "\n", "\r", "\0", "\u202E", "\u2067", "\u2069", "😀", "e\u0301",
            "\uD800", "\uDC00", "<!--", "-->", "<script>", "</script>", "$", "$$", "{#id .c}",
            "abcdefghijklmnopqrstuvwxyz", "0123456789", " \t",
        ];
        var builder = new StringBuilder(targetLength + 32);
        while (builder.Length < targetLength)
        {
            string token = tokens[random.Next(tokens.Length)];
            int remaining = targetLength - builder.Length;
            builder.Append(token.AsSpan(0, Math.Min(token.Length, remaining)));
        }

        return builder.ToString();
    }
}
