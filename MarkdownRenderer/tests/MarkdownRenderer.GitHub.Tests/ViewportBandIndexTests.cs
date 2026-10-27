using System;
using System.Reflection;
using MarkdownRenderer.Layout;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

public sealed class ViewportBandIndexTests
{
    [Fact]
    public void SuffixCommitReusesBackingArrayAndAllocatesNothingForLastEntry()
    {
        const int count = 100_000;
        var index = new ViewportBandIndex(count);
        for (int ordinal = 0; ordinal < count; ordinal++)
            index.SetEntry(ordinal, ordinal, ordinal * 2d, ordinal * 2d + 1d);
        index.Commit();
        Assert.True(index.IsDocumentOrder);

        FieldInfo entriesField = typeof(ViewportBandIndex).GetField(
            "_entries", BindingFlags.Instance | BindingFlags.NonPublic)!;
        object backingArray = entriesField.GetValue(index)!;
        int last = count - 1;

        // Warm the successful suffix path before measuring managed allocation.
        index.SetEntry(last, last, last * 2d, last * 2d + 2d);
        index.CommitSuffix(last);
        long before = GC.GetAllocatedBytesForCurrentThread();
        index.SetEntry(last, last, last * 2d, last * 2d + 3d);
        index.CommitSuffix(last);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
        Assert.Same(backingArray, entriesField.GetValue(index));
        Assert.True(index.IsDocumentOrder);
        Assert.Equal((last * 2d, last * 2d + 3d), index.GetBounds(last));
    }

    [Fact]
    public void SuffixCommitRecomputesOverlappingPrefixMaximaAndHandlesSanitizedBounds()
    {
        var index = new ViewportBandIndex(4);
        index.SetEntry(0, 0, 0, 100);
        index.SetEntry(1, 1, 10, 5); // Inverted bounds collapse to the top.
        index.SetEntry(2, 2, 20, double.NaN); // Non-finite bottom collapses to top.
        index.SetEntry(3, 3, double.NaN, 80); // Non-finite top normalizes to zero.
        index.Commit();

        // The custom last entry sorts ahead of the other entries; use a second
        // logical-order snapshot for a suffix update while retaining overlap.
        Assert.False(index.IsDocumentOrder);
        index.SetEntry(0, 0, 0, 100);
        index.SetEntry(1, 1, 10, 10);
        index.SetEntry(2, 2, 20, 25);
        index.SetEntry(3, 3, 30, 35);
        index.Commit();
        Assert.True(index.IsDocumentOrder);

        index.SetEntry(2, 2, 20, 120);
        index.SetEntry(3, 3, 30, 31);
        index.CommitSuffix(2);
        Assert.True(index.IsDocumentOrder);
        Assert.Equal((20d, 120d), index.GetBounds(2));
        Assert.Equal((30d, 31d), index.GetBounds(3));
        ViewportRange overlap = index.Find(119, 119);
        Assert.Equal(2, overlap.Start);
        Assert.Equal(4, overlap.End);

        // A prefix replacement recomputes all prefixes from negative infinity.
        index.SetEntry(0, 0, 0, 5);
        index.SetEntry(2, 2, 20, 25);
        index.CommitSuffix(0);
        ViewportRange noOverlap = index.Find(119, 119);
        Assert.Equal(4, noOverlap.Start);
        Assert.Equal(4, noOverlap.End);
        Assert.True(index.IsDocumentOrder);
    }

    [Fact]
    public void ReorderedSuffixFallsBackToFullSortAndCanReturnToDocumentOrder()
    {
        var index = new ViewportBandIndex(3);
        index.SetEntry(0, 0, 0, 2);
        index.SetEntry(1, 1, 10, 12);
        index.SetEntry(2, 2, 20, 22);
        index.Commit();
        Assert.True(index.IsDocumentOrder);

        index.SetEntry(2, 2, -10, -5);
        index.CommitSuffix(2);

        Assert.False(index.IsDocumentOrder);
        Assert.Equal(2, index.GetBlockOrdinal(0));
        Assert.Equal(0, index.GetBlockOrdinal(1));
        Assert.Equal(1, index.GetBlockOrdinal(2));
        ViewportRange first = index.Find(-9, -9);
        Assert.Equal(0, first.Start);
        Assert.Equal(1, first.End);

        // A full refresh writes slots in logical order before committing again.
        index.SetEntry(0, 0, 0, 2);
        index.SetEntry(1, 1, 10, 12);
        index.SetEntry(2, 2, 20, 22);
        index.Commit();
        Assert.True(index.IsDocumentOrder);
    }

    [Fact]
    public void EmptyIndexAndRepeatedSuffixCommitsRemainWellDefined()
    {
        var empty = new ViewportBandIndex(0);
        empty.Commit();
        Assert.True(empty.IsDocumentOrder);
        empty.CommitSuffix(0);
        Assert.Equal(default, empty.Find(double.NaN, double.PositiveInfinity));

        var index = new ViewportBandIndex(3);
        index.SetEntry(0, 0, 0, 4);
        index.SetEntry(1, 1, 10, 14);
        index.SetEntry(2, 2, 20, 24);
        index.Commit();
        index.SetEntry(2, 2, 20, 25);
        index.CommitSuffix(2);
        index.SetEntry(2, 2, 20, 26);
        index.CommitSuffix(2);
        Assert.Equal((20d, 26d), index.GetBounds(2));
        Assert.Equal(new ViewportRange(2, 3), index.Find(25, 15));
    }
}
