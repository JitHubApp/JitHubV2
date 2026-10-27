using System.Diagnostics;
using System.Text;
using MarkdownRenderer.Document;
using Xunit;

namespace MarkdownRenderer.Tests;

public class MarkdownSourceMapIndexTests
{
    [Fact]
    public void EntriesAndSlice_NormalizeOutOfOrderAdditions()
    {
        const string source = "zero|one|two";
        var map = new MarkdownSourceMap(source);
        map.Add(2, 0, 3, new SourceSpan(9, 3));
        map.Add(0, 0, 4, new SourceSpan(0, 4));
        map.Add(1, 0, 3, new SourceSpan(5, 3));

        Assert.Equal(new[] { 0, 1, 2 }, map.Entries.Select(entry => entry.BlockIndex));

        var range = new DocumentRange(
            new DocumentPosition(0, 0, 1),
            new DocumentPosition(2, 0, 2));
        Assert.Equal("ero|one|tw", map.Slice(range));
    }

    [Fact]
    public void Slice_EndAtNextInlineStart_ExcludesNextInlineAndSeparator()
    {
        const string source = "alpha|beta";
        var map = new MarkdownSourceMap(source);
        map.Add(0, 0, 5, new SourceSpan(0, 5));
        map.Add(0, 1, 4, new SourceSpan(6, 4));

        var range = new DocumentRange(
            new DocumentPosition(0, 0, 0),
            new DocumentPosition(0, 1, 0));
        Assert.Equal("alpha", map.Slice(range));
    }

    [Fact]
    public void Slice_DuplicateDocumentPositions_PreserveInsertionOrder()
    {
        var map = new MarkdownSourceMap("ab");
        map.Add(0, 0, 1, new SourceSpan(0, 1));
        map.Add(0, 0, 1, new SourceSpan(1, 1));

        var range = new DocumentRange(
            new DocumentPosition(0, 0, 0),
            new DocumentPosition(0, 0, 1));
        Assert.Equal("ab", map.Slice(range));
    }

    [Fact]
    public void AddSourceAffixes_OutOfOrderEntries_UsesDocumentBoundaries()
    {
        const string source = "# Alpha beta ##";
        var map = new MarkdownSourceMap(source);
        map.Add(4, 1, 4, new SourceSpan(8, 4));
        map.Add(4, 0, 5, new SourceSpan(2, 5));
        map.AddSourceAffixesToBlock(4, 0, source.Length);

        var range = new DocumentRange(
            new DocumentPosition(4, 0, 0),
            new DocumentPosition(4, 1, 4));
        Assert.Equal(source, map.Slice(range));
    }

    [Fact]
    public void Slice_IndexInvalidatedByLaterOutOfOrderAddition()
    {
        const string source = "a|b";
        var map = new MarkdownSourceMap(source);
        map.Add(1, 0, 1, new SourceSpan(2, 1));

        Assert.Equal("b", map.Slice(new DocumentRange(
            new DocumentPosition(1, 0, 0),
            new DocumentPosition(1, 0, 1))));

        map.Add(0, 0, 1, new SourceSpan(0, 1));
        Assert.Equal(source, map.Slice(new DocumentRange(
            new DocumentPosition(0, 0, 0),
            new DocumentPosition(1, 0, 1))));
    }

    [Fact]
    public void AddSourceAffixes_InvalidatesPreviouslyBuiltIndex()
    {
        const string source = "# Heading";
        var map = new MarkdownSourceMap(source);
        map.Add(1, 0, 7, new SourceSpan(2, 7));
        var range = new DocumentRange(
            new DocumentPosition(1, 0, 0),
            new DocumentPosition(1, 0, 7));

        Assert.Equal("Heading", map.Slice(range));

        map.AddSourceAffixesToBlock(1, 0, source.Length);
        Assert.Equal(source, map.Slice(range));
    }

    [Fact]
    public void Slice_ZeroLengthEntries_DoNotBecomeSelectionBoundaries()
    {
        var map = new MarkdownSourceMap("abc");
        map.Add(0, 0, 0, new SourceSpan(0, 1));
        map.Add(1, 0, 3, new SourceSpan(0, 3));

        var range = new DocumentRange(
            new DocumentPosition(0, 0, 0),
            new DocumentPosition(1, 0, 3));
        Assert.Equal("abc", map.Slice(range));
    }

    [Fact]
    public void Slice_RandomizedRanges_MatchDocumentOrderReference()
    {
        const int blockCount = 32;
        const int inlineCount = 8;
        const int tokenLength = 3;
        int entryCount = blockCount * inlineCount;
        var sourceBuilder = new StringBuilder(entryCount * (tokenLength + 1));
        var referenceEntries = new ReferenceEntry[entryCount];

        for (int ordinal = 0; ordinal < entryCount; ordinal++)
        {
            if (ordinal > 0)
                sourceBuilder.Append('|');

            int sourceStart = sourceBuilder.Length;
            sourceBuilder.Append(ordinal.ToString("D3", System.Globalization.CultureInfo.InvariantCulture));
            referenceEntries[ordinal] = new ReferenceEntry(
                ordinal / inlineCount,
                ordinal % inlineCount,
                tokenLength,
                sourceStart);
        }

        string source = sourceBuilder.ToString();
        var map = new MarkdownSourceMap(source);
        var random = new Random(0x51CE);
        int[] insertionOrder = Enumerable.Range(0, entryCount).ToArray();
        random.Shuffle(insertionOrder);
        foreach (int ordinal in insertionOrder)
        {
            ReferenceEntry entry = referenceEntries[ordinal];
            map.Add(
                entry.BlockIndex,
                entry.InlineIndex,
                entry.RenderedLength,
                new SourceSpan(entry.SourceStart, entry.RenderedLength));
        }

        for (int iteration = 0; iteration < 1_000; iteration++)
        {
            ReferenceEntry startEntry = referenceEntries[random.Next(entryCount)];
            ReferenceEntry endEntry = referenceEntries[random.Next(entryCount)];
            var range = new DocumentRange(
                new DocumentPosition(startEntry.BlockIndex, startEntry.InlineIndex, random.Next(tokenLength + 1)),
                new DocumentPosition(endEntry.BlockIndex, endEntry.InlineIndex, random.Next(tokenLength + 1)));

            Assert.Equal(ReferenceSlice(source, referenceEntries, range), map.Slice(range));
        }
    }

    [Fact]
    [Trait("Category", "Performance")]
    public void HundredThousandEntries_BuildIndexAndRepeatedSlices_AvoidQuadraticWork()
    {
        const int entryCount = 100_000;
        const int lookupCount = 20_000;
        string source = new('x', entryCount * 2);
        var map = new MarkdownSourceMap(source);

        var buildTimer = Stopwatch.StartNew();
        for (int blockIndex = entryCount - 1; blockIndex >= 0; blockIndex--)
        {
            int sourceStart = blockIndex * 2;
            map.Add(blockIndex, 0, 1, new SourceSpan(sourceStart, 1));
            map.AddSourceAffixesToBlock(blockIndex, sourceStart, sourceStart + 1);
        }

        Assert.Equal(entryCount, map.Entries.Count);
        buildTimer.Stop();
        Assert.True(
            buildTimer.Elapsed < TimeSpan.FromSeconds(5),
            $"Building and indexing {entryCount:N0} entries took {buildTimer.Elapsed.TotalMilliseconds:N0} ms.");

        int selectedCharacters = 0;
        var lookupTimer = Stopwatch.StartNew();
        for (int lookup = 0; lookup < lookupCount; lookup++)
        {
            int blockIndex = entryCount - 1 - (lookup % 1_024);
            selectedCharacters += map.Slice(new DocumentRange(
                new DocumentPosition(blockIndex, 0, 0),
                new DocumentPosition(blockIndex, 0, 1))).Length;
        }

        lookupTimer.Stop();
        Assert.Equal(lookupCount, selectedCharacters);
        Assert.True(
            lookupTimer.Elapsed < TimeSpan.FromSeconds(2),
            $"{lookupCount:N0} indexed lookups over {entryCount:N0} entries took " +
            $"{lookupTimer.Elapsed.TotalMilliseconds:N0} ms.");
    }

    private static string ReferenceSlice(string source, ReferenceEntry[] entries, DocumentRange range)
    {
        range = range.Normalized();
        if (range.IsEmpty)
            return string.Empty;

        int firstSourceOffset = -1;
        int lastSourceOffset = -1;
        foreach (ReferenceEntry entry in entries)
        {
            var startPosition = new DocumentPosition(entry.BlockIndex, entry.InlineIndex, 0);
            var endPosition = new DocumentPosition(entry.BlockIndex, entry.InlineIndex, entry.RenderedLength);
            if (endPosition <= range.Start)
                continue;
            if (startPosition >= range.End)
                break;

            int from = range.Start.BlockIndex == entry.BlockIndex && range.Start.InlineIndex == entry.InlineIndex
                ? Math.Max(0, range.Start.CharacterOffset)
                : 0;
            int to = range.End.BlockIndex == entry.BlockIndex && range.End.InlineIndex == entry.InlineIndex
                ? Math.Min(entry.RenderedLength, range.End.CharacterOffset)
                : entry.RenderedLength;
            if (to <= from)
                continue;

            if (firstSourceOffset < 0)
                firstSourceOffset = entry.SourceStart + from;
            lastSourceOffset = entry.SourceStart + to;
        }

        return firstSourceOffset < 0 || lastSourceOffset < 0
            ? string.Empty
            : source.Substring(firstSourceOffset, lastSourceOffset - firstSourceOffset);
    }

    private readonly record struct ReferenceEntry(
        int BlockIndex,
        int InlineIndex,
        int RenderedLength,
        int SourceStart);
}
