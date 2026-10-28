using MarkdownRenderer.Layout;
using Xunit;

namespace MarkdownRenderer.Tests;

[Collection(AllocationSensitiveContractCollection.Name)]
public sealed class ViewportBandIndexTests
{
    [Fact]
    public void RepeatedLazyReflowKeepsViewportQueriesCorrect()
    {
        const int blockCount = 4_096;
        var heights = new double[blockCount];
        Array.Fill(heights, 32d);
        var index = new ViewportBandIndex(blockCount);

        for (int pass = 0; pass < 128; pass++)
        {
            int measuredBlock = (pass * 31) % blockCount;
            heights[measuredBlock] = 48 + (pass % 5) * 12;

            double y = 0;
            for (int block = 0; block < blockCount; block++)
            {
                double bottom = y + heights[block];
                index.SetEntry(block, block, y, bottom);
                y = bottom;
            }

            index.Commit();

            double queryY = y - heights[^1] / 2;
            ViewportRange range = index.Find(queryY, queryY);
            Assert.True(ContainsBlock(index, range, blockCount - 1));

            double measuredTop = 0;
            for (int block = 0; block < measuredBlock; block++)
                measuredTop += heights[block];

            queryY = measuredTop + heights[measuredBlock] / 2;
            range = index.Find(queryY, queryY);
            Assert.True(ContainsBlock(index, range, measuredBlock));
        }
    }

    [Fact]
    public void RefreshAndQueryDoNotAllocateAfterWarmup()
    {
        const int blockCount = 2_048;
        const int passes = 128;
        var index = new ViewportBandIndex(blockCount);

        Refresh(index, blockCount, heightBias: 0);
        _ = index.Find(100, 700);

        long before = GC.GetAllocatedBytesForCurrentThread();
        int checksum = 0;
        for (int pass = 0; pass < passes; pass++)
        {
            Refresh(index, blockCount, pass % 7);
            ViewportRange range = index.Find(pass * 37, pass * 37 + 600);
            checksum += range.End - range.Start;
            (double top, double bottom) = index.GetBounds(range.Start);
            checksum += (int)(bottom - top);
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(checksum > 0);
        Assert.Equal(0, allocated);
    }

    [Fact]
    public void FusedSuffixWriterMatchesFullRebuildAcrossLargeSequentialUpdates()
    {
        const int blockCount = 4_096;
        const int passes = 96;
        var expected = new ViewportBandIndex(blockCount);
        var actual = new ViewportBandIndex(blockCount);
        var heights = new double[blockCount];
        Array.Fill(heights, 24d);

        RefreshWithSetEntry(expected, heights, firstSlot: 0, spacing: 3d);
        RefreshWithSuffixWriter(actual, heights, firstSlot: 0, spacing: 3d);
        AssertIndexesEqual(expected, actual, blockCount);

        for (int pass = 0; pass < passes; pass++)
        {
            int firstSlot = (pass * 149) % blockCount;
            for (int index = firstSlot; index < blockCount; index++)
                heights[index] = 18 + ((index * 7 + pass * 11) % 9) * 3;

            RefreshWithSetEntry(expected, heights, firstSlot: 0, spacing: 3d);
            RefreshWithSuffixWriter(actual, heights, firstSlot, spacing: 3d);
            AssertIndexesEqual(expected, actual, blockCount);
        }
    }

    [Fact]
    public void FusedSuffixWriterPreservesPrefixMaximumCrossingTheSuffixBoundary()
    {
        var expected = new ViewportBandIndex(5);
        var actual = new ViewportBandIndex(5);
        int[] ordinals = [0, 1, 2, 3, 4];
        double[] tops = [0, 10, 30, 50, 70];
        double[] bottoms = [1_000, 20, 40, 60, 80];

        SetEntries(expected, ordinals, tops, bottoms, 0);
        WriteEntries(actual, ordinals, tops, bottoms, 0);

        tops[2] = 31;
        bottoms[2] = 41;
        tops[3] = 51;
        bottoms[3] = 61;
        tops[4] = 71;
        bottoms[4] = 81;
        SetEntries(expected, ordinals, tops, bottoms, 0);
        WriteEntries(actual, ordinals, tops, bottoms, 2);

        AssertIndexesEqual(expected, actual, expected.Count);
        ViewportRange crossingRange = actual.Find(900, 900);
        Assert.Equal(0, crossingRange.Start);
        Assert.Equal(5, crossingRange.End);
    }

    [Fact]
    public void SuffixWriterFallsBackWhenReplacementGeometryCrossesTheCommittedPrefix()
    {
        var expected = new ViewportBandIndex(4);
        var actual = new ViewportBandIndex(4);
        int[] ordinals = [0, 1, 2, 3];
        double[] tops = [0, 30, 60, 90];
        double[] bottoms = [20, 50, 80, 110];

        SetEntries(expected, ordinals, tops, bottoms, 0);
        WriteEntries(actual, ordinals, tops, bottoms, 0);

        tops[2] = 10;
        bottoms[2] = 20;
        tops[3] = 15;
        bottoms[3] = 25;
        SetEntries(expected, ordinals, tops, bottoms, 2);
        WriteEntries(actual, ordinals, tops, bottoms, 2);

        AssertIndexesEqual(expected, actual, expected.Count);
        Assert.False(actual.IsDocumentOrder);
        Assert.Equal(2, actual.GetBlockOrdinal(1));
        Assert.Equal(3, actual.GetBlockOrdinal(2));
        Assert.True(ContainsBlock(actual, actual.Find(16, 16), 3));
    }

    [Fact]
    public void FullWriterUsesDefensiveSortForCustomOrderThenCanRecoverDocumentOrder()
    {
        var expected = new ViewportBandIndex(4);
        var actual = new ViewportBandIndex(4);
        int[] ordinals = [0, 1, 2, 3];
        double[] tops = [90, 0, 60, 30];
        double[] bottoms = [120, 30, 90, 60];

        SetEntries(expected, ordinals, tops, bottoms, 0);
        WriteEntries(actual, ordinals, tops, bottoms, 0);

        AssertIndexesEqual(expected, actual, expected.Count);
        Assert.False(actual.IsDocumentOrder);
        Assert.True(ContainsBlock(actual, actual.Find(35, 55), 3));
        Assert.Throws<InvalidOperationException>(() => BeginNonzeroSuffixWrite(actual));

        tops = [0, 30, 60, 90];
        bottoms = [20, 50, 80, 110];
        SetEntries(expected, ordinals, tops, bottoms, 0);
        WriteEntries(actual, ordinals, tops, bottoms, 0);

        AssertIndexesEqual(expected, actual, expected.Count);
        Assert.True(actual.IsDocumentOrder);
    }

    [Fact]
    public void FusedWriterUsesTheSameMalformedGeometryNormalizationAsSetEntry()
    {
        var expected = new ViewportBandIndex(5);
        var actual = new ViewportBandIndex(5);
        int[] ordinals = [0, 1, 2, 3, 4];
        double[] tops = [double.NaN, double.PositiveInfinity, 10, 20, 30];
        double[] bottoms = [40, double.NegativeInfinity, double.NaN, 19, double.PositiveInfinity];

        SetEntries(expected, ordinals, tops, bottoms, 0);
        WriteEntries(actual, ordinals, tops, bottoms, 0);

        AssertIndexesEqual(expected, actual, expected.Count);
        Assert.Equal((0d, 40d), actual.GetBounds(0));
        Assert.Equal((0d, 0d), actual.GetBounds(1));
        Assert.Equal((10d, 10d), actual.GetBounds(2));
        Assert.Equal((20d, 20d), actual.GetBounds(3));
        Assert.Equal((30d, 30d), actual.GetBounds(4));
    }

    [Fact]
    public void WarmFusedSuffixRefreshAndQueriesDoNotAllocate()
    {
        const int blockCount = 2_048;
        const int passes = 128;
        const int firstSlot = 512;
        var index = new ViewportBandIndex(blockCount);
        RefreshWithSuffixWriter(index, blockCount, firstSlot: 0, heightBias: 0);
        _ = index.Find(100, 700);

        long before = GC.GetAllocatedBytesForCurrentThread();
        int checksum = 0;
        for (int pass = 0; pass < passes; pass++)
        {
            RefreshWithSuffixWriter(index, blockCount, firstSlot, pass % 7);
            ViewportRange range = index.Find(pass * 37, pass * 37 + 600);
            checksum += range.End - range.Start;
            (double top, double bottom) = index.GetBounds(range.Start);
            checksum += (int)(bottom - top);
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(checksum > 0);
        Assert.Equal(0, allocated);
    }

    [Fact]
    public void CommitDefensivelySortsOutOfOrderCustomBoundsInPlace()
    {
        var index = new ViewportBandIndex(4);
        index.SetEntry(0, 0, 90, 120);
        index.SetEntry(1, 1, 0, 30);
        index.SetEntry(2, 2, 60, 90);
        index.SetEntry(3, 3, 30, 60);

        index.Commit();

        ViewportRange range = index.Find(35, 55);
        Assert.True(ContainsBlock(index, range, 3));
        Assert.False(ContainsBlock(index, range, 0));
        Assert.False(ContainsBlock(index, range, 1));
        Assert.False(ContainsBlock(index, range, 2));
    }

    [Fact]
    public void GetBoundsReturnsTheCommittedValueGeometryInSortedOrder()
    {
        var index = new ViewportBandIndex(3);
        index.SetEntry(0, 10, 90, 125);
        index.SetEntry(1, 20, 5, 25);
        index.SetEntry(2, 30, 45, 80);

        index.Commit();

        Assert.Equal(20, index.GetBlockOrdinal(0));
        Assert.Equal((5d, 25d), index.GetBounds(0));
        Assert.Equal(30, index.GetBlockOrdinal(1));
        Assert.Equal((45d, 80d), index.GetBounds(1));
        Assert.Equal(10, index.GetBlockOrdinal(2));
        Assert.Equal((90d, 125d), index.GetBounds(2));

        index.SetEntry(0, 10, 90, 125);
        index.SetEntry(1, 20, 110, 145);
        index.SetEntry(2, 30, 45, 80);
        index.Commit();

        Assert.Equal((45d, 80d), index.GetBounds(0));
        Assert.Equal((90d, 125d), index.GetBounds(1));
        Assert.Equal((110d, 145d), index.GetBounds(2));
    }

    [Fact]
    public void ImageStormViewportQueryKeepsOnlyNearbyImagesAndTracksReflow()
    {
        const int imageCount = 1_800;
        var index = new ViewportBandIndex(imageCount);
        for (int image = 0; image < imageCount; image++)
            index.SetEntry(image, image, image * 120, image * 120 + 80);
        index.Commit();

        ViewportRange range = index.Find(120_000, 120_600);
        Assert.True(range.End - range.Start <= 7);
        Assert.True(ContainsBlock(index, range, 1_000));
        Assert.False(ContainsBlock(index, range, 1_400));

        // A measured image can change height after its intrinsic size arrives.
        // Replacing the index at layout publication must not strand later images.
        for (int image = 0; image < imageCount; image++)
        {
            double top = image < 1_000 ? image * 120 : image * 120 + 500;
            index.SetEntry(image, image, top, top + 80);
        }
        index.Commit();

        range = index.Find(120_500, 121_100);
        Assert.True(range.End - range.Start <= 7);
        Assert.True(ContainsBlock(index, range, 1_000));
        Assert.False(ContainsBlock(index, range, 1_400));
    }

    private static void Refresh(ViewportBandIndex index, int blockCount, int heightBias)
    {
        double y = 0;
        for (int block = 0; block < blockCount; block++)
        {
            double height = 24 + ((block + heightBias) % 5) * 4;
            double bottom = y + height;
            index.SetEntry(block, block, y, bottom);
            y = bottom;
        }

        index.Commit();
    }

    private static void RefreshWithSetEntry(
        ViewportBandIndex index,
        double[] heights,
        int firstSlot,
        double spacing)
    {
        double y = firstSlot == 0
            ? 0
            : index.GetBounds(firstSlot - 1).Bottom + spacing;

        for (int ordinal = firstSlot; ordinal < heights.Length; ordinal++)
        {
            double bottom = y + heights[ordinal];
            index.SetEntry(ordinal, ordinal, y, bottom);
            y = bottom + spacing;
        }

        index.CommitSuffix(firstSlot);
    }

    private static void RefreshWithSuffixWriter(
        ViewportBandIndex index,
        double[] heights,
        int firstSlot,
        double spacing)
    {
        double y = 0;
        if (firstSlot > 0)
        {
            (_, y) = index.GetBounds(firstSlot - 1);
            y += spacing;
        }

        ViewportBandIndex.SuffixWriter writer = index.BeginSuffixWrite(firstSlot);
        for (int ordinal = firstSlot; ordinal < heights.Length; ordinal++)
        {
            double bottom = y + heights[ordinal];
            writer.WriteNext(ordinal, y, bottom);
            y = bottom + spacing;
        }
        writer.Complete();
    }

    private static void RefreshWithSuffixWriter(
        ViewportBandIndex index,
        int blockCount,
        int firstSlot,
        int heightBias)
    {
        double y = 0;
        if (firstSlot > 0)
        {
            (_, y) = index.GetBounds(firstSlot - 1);
            y += 4;
        }

        ViewportBandIndex.SuffixWriter writer = index.BeginSuffixWrite(firstSlot);
        for (int block = firstSlot; block < blockCount; block++)
        {
            double height = 24 + ((block + heightBias) % 5) * 4;
            double bottom = y + height;
            writer.WriteNext(block, y, bottom);
            y = bottom + 4;
        }
        writer.Complete();
    }

    private static void SetEntries(
        ViewportBandIndex index,
        int[] ordinals,
        double[] tops,
        double[] bottoms,
        int firstSlot)
    {
        for (int slot = firstSlot; slot < ordinals.Length; slot++)
            index.SetEntry(slot, ordinals[slot], tops[slot], bottoms[slot]);
        if (firstSlot == 0)
            index.Commit();
        else
            index.CommitSuffix(firstSlot);
    }

    private static void WriteEntries(
        ViewportBandIndex index,
        int[] ordinals,
        double[] tops,
        double[] bottoms,
        int firstSlot)
    {
        ViewportBandIndex.SuffixWriter writer = index.BeginSuffixWrite(firstSlot);
        for (int slot = firstSlot; slot < ordinals.Length; slot++)
            writer.WriteNext(ordinals[slot], tops[slot], bottoms[slot]);
        writer.Complete();
    }

    private static void BeginNonzeroSuffixWrite(ViewportBandIndex index)
    {
        _ = index.BeginSuffixWrite(1);
    }

    private static void AssertIndexesEqual(
        ViewportBandIndex expected,
        ViewportBandIndex actual,
        int blockCount)
    {
        Assert.Equal(expected.IsDocumentOrder, actual.IsDocumentOrder);
        for (int index = 0; index < blockCount; index++)
        {
            Assert.Equal(expected.GetBlockOrdinal(index), actual.GetBlockOrdinal(index));
            Assert.Equal(expected.GetBounds(index), actual.GetBounds(index));
        }

        double finalBottom = expected.GetBounds(blockCount - 1).Bottom;
        const int queryCount = 48;
        for (int queryIndex = 0; queryIndex < queryCount; queryIndex++)
        {
            double query = -100 + ((finalBottom + 200) * queryIndex / (queryCount - 1));
            ViewportRange expectedRange = expected.Find(query, query + 53);
            ViewportRange actualRange = actual.Find(query, query + 53);
            Assert.Equal(expectedRange.Start, actualRange.Start);
            Assert.Equal(expectedRange.End, actualRange.End);
        }
    }

    private static bool ContainsBlock(ViewportBandIndex index, ViewportRange range, int blockOrdinal)
    {
        for (int i = range.Start; i < range.End; i++)
        {
            if (index.GetBlockOrdinal(i) == blockOrdinal)
                return true;
        }

        return false;
    }
}
