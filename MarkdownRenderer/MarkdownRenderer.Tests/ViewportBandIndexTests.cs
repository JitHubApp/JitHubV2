using MarkdownRenderer.Layout;
using Xunit;

namespace MarkdownRenderer.Tests;

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
