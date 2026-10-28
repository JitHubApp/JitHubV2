using System.Reflection;
using MarkdownRenderer.Document;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

public sealed class MarkdownSourceMapAffixIndexTests
{
    private static readonly FieldInfo BlockEntryBoundsField =
        typeof(MarkdownSourceMap).GetField("_blockEntryBounds", BindingFlags.Instance | BindingFlags.NonPublic)!;

    [Fact]
    public void OrderedAffixesUseNonemptyDocumentBoundariesWithoutMaterializingBlockIndex()
    {
        const string source = "##A|B##";
        var map = new MarkdownSourceMap(source);

        map.Add(-7, 0, 0, new SourceSpan(0, 0));
        map.Add(-7, 1, 1, new SourceSpan(2, 1));
        map.Add(-7, 1, 1, new SourceSpan(4, 1));
        map.Add(-7, 2, 0, new SourceSpan(source.Length, 0));

        map.AddSourceAffixesToBlock(-7, 0, source.Length);

        MarkdownSourceMap.Entry[] entries = map.Entries.ToArray();
        Assert.Equal(new SourceSpan(0, 0), entries[0].Span);
        Assert.Equal(new SourceSpan(0, 3), entries[1].Span);
        Assert.Equal(2, entries[1].SourcePrefixLength);
        Assert.Equal(new SourceSpan(4, 3), entries[2].Span);
        Assert.Equal(2, entries[2].SourceSuffixLength);
        Assert.Equal(new SourceSpan(source.Length, 0), entries[3].Span);
        Assert.Null(BlockEntryBoundsField.GetValue(map));

        var fullBlock = new DocumentRange(
            new DocumentPosition(-7, 1, 0),
            new DocumentPosition(-7, 1, 2));
        Assert.Equal(source, map.Slice(fullBlock));
    }

    [Fact]
    public void OrderedAllEmptyBlockDoesNotCreateBoundsOrAcquireAffixes()
    {
        const string source = "##";
        var map = new MarkdownSourceMap(source);
        map.Add(-4, 0, 0, new SourceSpan(1, 0));

        map.AddSourceAffixesToBlock(-4, 0, source.Length);

        MarkdownSourceMap.Entry entry = Assert.Single(map.Entries);
        Assert.Equal(new SourceSpan(1, 0), entry.Span);
        Assert.Equal(0, entry.SourcePrefixLength);
        Assert.Equal(0, entry.SourceSuffixLength);
        Assert.Null(BlockEntryBoundsField.GetValue(map));
    }

    [Fact]
    public void RepeatedOrderedAffixesAccumulateOnSingleEntryWithoutBlockIndex()
    {
        const string source = "##A!!";
        var map = new MarkdownSourceMap(source);
        map.Add(0, 0, 1, new SourceSpan(2, 1));

        map.AddSourceAffixesToBlock(0, 0, 3);
        map.AddSourceAffixesToBlock(0, 0, source.Length);

        MarkdownSourceMap.Entry entry = Assert.Single(map.Entries);
        Assert.Equal(new SourceSpan(0, source.Length), entry.Span);
        Assert.Equal(2, entry.SourcePrefixLength);
        Assert.Equal(2, entry.SourceSuffixLength);
        Assert.Null(BlockEntryBoundsField.GetValue(map));
        Assert.Equal(source, map.Slice(new DocumentRange(
            new DocumentPosition(0, 0, 0),
            new DocumentPosition(0, 0, 1))));
    }

    [Fact]
    public void NonorderedAffixesMatchDocumentOrderAndTrackLaterAdditions()
    {
        const string source = "AA|bb|CC|dd!";
        var map = new MarkdownSourceMap(source);
        map.Add(5, 2, 1, new SourceSpan(6, 1));
        map.Add(1, 0, 2, new SourceSpan(0, 2));
        map.Add(5, 1, 2, new SourceSpan(3, 2));
        map.Add(5, 2, 1, new SourceSpan(7, 1));

        map.AddSourceAffixesToBlock(5, 0, 9);

        Assert.NotNull(BlockEntryBoundsField.GetValue(map));
        MarkdownSourceMap.Entry[] afterFirstAffix = map.Entries.ToArray();
        MarkdownSourceMap.Entry first = Assert.Single(afterFirstAffix, entry => entry.BlockIndex == 5 && entry.InlineIndex == 1);
        Assert.Equal(new SourceSpan(0, 5), first.Span);
        Assert.Equal(3, first.SourcePrefixLength);

        MarkdownSourceMap.Entry[] equalInline = afterFirstAffix
            .Where(entry => entry.BlockIndex == 5 && entry.InlineIndex == 2)
            .ToArray();
        Assert.Equal(2, equalInline.Length);
        Assert.Equal(new SourceSpan(6, 1), equalInline[0].Span);
        Assert.Equal(0, equalInline[0].InsertionOrder);
        Assert.Equal(new SourceSpan(7, 2), equalInline[1].Span);
        Assert.Equal(1, equalInline[1].SourceSuffixLength);
        Assert.Equal(3, equalInline[1].InsertionOrder);

        // The dictionary was materialized by the out-of-order prefix. This new
        // last entry must be tracked by AddCore so its later suffix receives the
        // same treatment as entries already in the fallback index.
        map.Add(5, 3, 2, new SourceSpan(9, 2));
        map.AddSourceAffixesToBlock(5, 0, source.Length);

        MarkdownSourceMap.Entry last = Assert.Single(map.Entries, entry => entry.BlockIndex == 5 && entry.InlineIndex == 3);
        Assert.Equal(new SourceSpan(9, 3), last.Span);
        Assert.Equal(1, last.SourceSuffixLength);
        Assert.Equal(source, map.Slice(new DocumentRange(
            new DocumentPosition(5, 1, 0),
            new DocumentPosition(5, 3, 2))));
    }
}
