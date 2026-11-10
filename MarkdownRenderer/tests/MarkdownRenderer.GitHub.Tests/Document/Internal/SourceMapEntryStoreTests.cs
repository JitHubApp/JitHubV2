using MarkdownRenderer.Document;
using MarkdownRenderer.Document.Internal;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests.Document.Internal;

/// <summary>
/// Covers the block-bounds index behind <see cref="MarkdownSourceMap"/> source
/// affixes: ordered additions locate a block directly in the ordered entries,
/// and only an out-of-order addition builds the fallback index.
/// </summary>
public sealed class SourceMapEntryStoreTests
{
    [Fact]
    public void OrderedAffixesUseNonemptyBoundariesWithoutBuildingBlockIndex()
    {
        var store = new SourceMapEntryStore();
        store.Add(-7, 0, 0, new SourceSpan(0, 0), projection: null);
        store.Add(-7, 1, 1, new SourceSpan(2, 1), projection: null);
        store.Add(-7, 1, 1, new SourceSpan(4, 1), projection: null);
        store.Add(-7, 2, 0, new SourceSpan(7, 0), projection: null);

        Assert.True(store.AddSourceAffixesToBlock(-7, 0, 7, sourceTextLength: 7));

        MarkdownSourceMap.Entry[] entries = store.ToArray();
        Assert.Equal(new SourceSpan(0, 0), entries[0].Span);
        Assert.Equal(new SourceSpan(0, 3), entries[1].Span);
        Assert.Equal(2, entries[1].SourcePrefixLength);
        Assert.Equal(new SourceSpan(4, 3), entries[2].Span);
        Assert.Equal(2, entries[2].SourceSuffixLength);
        Assert.Equal(new SourceSpan(7, 0), entries[3].Span);
        Assert.True(store.EntriesWereAddedInOrder);
        Assert.False(store.HasBlockEntryIndex);
    }

    [Fact]
    public void OrderedAllEmptyBlockReportsNoChangeAndBuildsNoIndex()
    {
        var store = new SourceMapEntryStore();
        store.Add(-4, 0, 0, new SourceSpan(1, 0), projection: null);

        Assert.False(store.AddSourceAffixesToBlock(-4, 0, 2, sourceTextLength: 2));

        MarkdownSourceMap.Entry entry = Assert.Single(store.ToArray());
        Assert.Equal(new SourceSpan(1, 0), entry.Span);
        Assert.Equal(0, entry.SourcePrefixLength);
        Assert.Equal(0, entry.SourceSuffixLength);
        Assert.False(store.HasBlockEntryIndex);
    }

    [Fact]
    public void RepeatedOrderedAffixesAccumulateWithoutBlockIndex()
    {
        var store = new SourceMapEntryStore();
        store.Add(0, 0, 1, new SourceSpan(2, 1), projection: null);

        Assert.True(store.AddSourceAffixesToBlock(0, 0, 3, sourceTextLength: 5));
        Assert.True(store.AddSourceAffixesToBlock(0, 0, 5, sourceTextLength: 5));
        Assert.False(store.AddSourceAffixesToBlock(0, 0, 5, sourceTextLength: 5));

        MarkdownSourceMap.Entry entry = Assert.Single(store.ToArray());
        Assert.Equal(new SourceSpan(0, 5), entry.Span);
        Assert.Equal(2, entry.SourcePrefixLength);
        Assert.Equal(2, entry.SourceSuffixLength);
        Assert.False(store.HasBlockEntryIndex);
    }

    [Fact]
    public void OutOfOrderAdditionBuildsIndexOnFirstAffixAndTracksLaterEntries()
    {
        var store = new SourceMapEntryStore();
        store.Add(5, 2, 1, new SourceSpan(6, 1), projection: null);
        store.Add(1, 0, 2, new SourceSpan(0, 2), projection: null);
        Assert.False(store.EntriesWereAddedInOrder);
        Assert.False(store.HasBlockEntryIndex);

        store.Add(5, 1, 2, new SourceSpan(3, 2), projection: null);
        store.Add(5, 2, 1, new SourceSpan(7, 1), projection: null);
        Assert.True(store.AddSourceAffixesToBlock(5, 0, 9, sourceTextLength: 12));
        Assert.True(store.HasBlockEntryIndex);

        MarkdownSourceMap.Entry[] entries = store.ToArray();
        Assert.Equal(new SourceSpan(0, 5), entries[2].Span);
        Assert.Equal(3, entries[2].SourcePrefixLength);
        Assert.Equal(new SourceSpan(7, 2), entries[3].Span);
        Assert.Equal(1, entries[3].SourceSuffixLength);

        // Added after the index exists, so Add must track it for the next suffix.
        store.Add(5, 3, 2, new SourceSpan(9, 2), projection: null);
        Assert.True(store.AddSourceAffixesToBlock(5, 0, 12, sourceTextLength: 12));

        MarkdownSourceMap.Entry last = store.ToArray()[4];
        Assert.Equal(new SourceSpan(9, 3), last.Span);
        Assert.Equal(1, last.SourceSuffixLength);
    }
}
