using MarkdownRenderer.Document;
using MarkdownRenderer.Layout;
using Microsoft.Graphics.Canvas;
using Windows.Foundation;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

public sealed class LayoutSnapshotViewportBandTests
{
    [Fact]
    public void MeasuredBandQuery_ExcludesDistantBlocksAndPreservesDocumentOrder()
    {
        var blocks = Enumerable.Range(0, 4)
            .Select(index => new FixedHeightBox { BlockIndex = index })
            .ToArray();
        for (int index = 0; index < blocks.Length; index++)
        {
            blocks[index].Measure(100);
            blocks[index].Arrange(0, index * 60, 100);
        }

        using var snapshot = new LayoutSnapshot(
            blocks,
            new MarkdownSourceMap(string.Empty),
            width: 100,
            height: 230);

        Assert.True(snapshot.TryGetMeasuredTopLevelBlocksInBand(55, 125, out var selected));
        Assert.Equal(
            new BlockBox[] { blocks[1], blocks[2] },
            selected);
        Assert.True(snapshot.TryGetMeasuredTopLevelBlocksInBand(52, 58, out var empty));
        Assert.Empty(empty);
    }

    [Fact]
    public async Task BusyBandQuery_DoesNotBlockTheUiThreadBehindMeasurement()
    {
        var block = new BlockingBox { BlockIndex = 0 };
        using var snapshot = new LayoutSnapshot(
            new BlockBox[] { block },
            new MarkdownSourceMap(string.Empty),
            width: 100,
            height: 0);
        snapshot.EnableLazyLayout(
            availableWidth: 100,
            viewportTop: 10_000,
            viewportHeight: 100,
            overscan: 0,
            CancellationToken.None);

        Task measure = Task.Run(() =>
            snapshot.EnsureMeasuredBand(new LazyLayoutBand(0, 100), CancellationToken.None));
        Assert.True(block.MeasureEntered.Wait(TimeSpan.FromSeconds(2)));
        try
        {
            Assert.False(snapshot.TryGetMeasuredTopLevelBlocksInBand(0, 100, out var busy));
            Assert.Empty(busy);
        }
        finally
        {
            block.ReleaseMeasure.Set();
        }

        await measure.WaitAsync(TimeSpan.FromSeconds(2));
    }

    private class FixedHeightBox : BlockBox
    {
        public override float Measure(float availableWidth)
        {
            Bounds = new Rect(Bounds.X, Bounds.Y, availableWidth, 50);
            return 50;
        }

        public override void Paint(CanvasDrawingSession ds, Rect viewport) { }
    }

    private sealed class BlockingBox : FixedHeightBox
    {
        internal ManualResetEventSlim MeasureEntered { get; } = new(false);
        internal ManualResetEventSlim ReleaseMeasure { get; } = new(false);

        public override float Measure(float availableWidth)
        {
            MeasureEntered.Set();
            if (!ReleaseMeasure.Wait(TimeSpan.FromSeconds(5)))
                throw new TimeoutException("The viewport-band measurement was not released.");
            return base.Measure(availableWidth);
        }
    }
}
