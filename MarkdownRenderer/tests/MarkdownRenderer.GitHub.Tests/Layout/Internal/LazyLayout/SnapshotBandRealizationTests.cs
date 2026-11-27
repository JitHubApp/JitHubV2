using MarkdownRenderer.Document;
using MarkdownRenderer.GitHub.Tests.Layout.Internal.Images.BitmapCache;
using MarkdownRenderer.GitHub.Tests.Layout.Internal.Inlines.Placement;
using MarkdownRenderer.Layout.Internal.Building;
using MarkdownRenderer.Layout.Internal.Context;
using MarkdownRenderer.Layout.Internal.LazyLayout;
using MarkdownRenderer.Layout.Internal.RenderResources;
using MarkdownRenderer.Layout.Internal.Snapshots;
using MarkdownRenderer.Parsing.Internal;
using Microsoft.UI.Xaml;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests.Layout.Internal.LazyLayout;

[Collection(SharedCanvasBitmapCacheTestCollection.Name)]
public sealed class SnapshotBandRealizationTests
{
    private const float Width = 640;
    private const double ViewportHeight = 480;

    private readonly MarkdownRenderResources _renderResources = new();

    [Fact]
    public async Task RequestedYieldLeavesTheSnapshotHealthyAndUnchanged()
    {
        await RunAsync(snapshot =>
        {
            int measured = snapshot.MeasuredTopLevelBlockCount;
            long revision = snapshot.LayoutRevision;
            var yieldSignal = new LazyBandYield();
            yieldSignal.Request();
            var lookAhead = new LazyLayoutBand(ViewportHeight, ViewportHeight * 6);

            LazyLayoutCommit commit = snapshot.EnsureMeasuredBand(
                lookAhead, LazyLayoutBand.FromVisibleViewport(0, ViewportHeight), CancellationToken.None, yieldSignal);

            Assert.False(commit.Changed);
            Assert.Equal(measured, snapshot.MeasuredTopLevelBlockCount);
            Assert.Equal(revision, snapshot.LayoutRevision);
            Assert.False(snapshot.GeometryRecoveryRequired);
            Assert.True(snapshot.TryBeginPaint());
            snapshot.EndPaint();
        });
    }

    [Fact]
    public async Task UnrequestedYieldMeasuresTheWholeBandAndLowersTheRepaintWatermark()
    {
        await RunAsync(snapshot =>
        {
            _ = snapshot.TakeLazyRepaintTop();
            var lookAhead = new LazyLayoutBand(ViewportHeight * 2, ViewportHeight * 4);

            LazyLayoutCommit commit = snapshot.EnsureMeasuredBand(
                lookAhead, LazyLayoutBand.FromVisibleViewport(0, ViewportHeight), CancellationToken.None,
                new LazyBandYield());

            Assert.True(commit.Changed);
            Assert.True(snapshot.IsBandMeasured(lookAhead));
            double repaintTop = snapshot.TakeLazyRepaintTop();
            Assert.InRange(repaintTop, 0, lookAhead.Bottom);
            Assert.Equal(double.PositiveInfinity, snapshot.TakeLazyRepaintTop());
        });
    }

    [Fact]
    public async Task UnmeasuredBandSearchPrefersLayoutBelowTheViewport()
    {
        await RunAsync(snapshot =>
        {
            Assert.True(snapshot.TryFindUnmeasuredBand(0, ViewportHeight, 2000, out LazyLayoutBand band));

            Assert.True(band.Top >= ViewportHeight - 1, $"Expected a band below the viewport, got {band.Top}.");
            Assert.Equal(2000, band.Bottom - band.Top, precision: 6);
        });
    }

    [Fact]
    public async Task UnmeasuredBandSearchFindsLayoutAboveOnceBelowIsMeasured()
    {
        await RunAsync(snapshot =>
        {
            double height = snapshot.Size.Height;
            double viewportTop = System.Math.Max(0, height - ViewportHeight);
            snapshot.EnsureMeasuredBand(
                new LazyLayoutBand(viewportTop, height + 1), CancellationToken.None);

            // Measuring reflows only the layout below the first measured root,
            // so the measured viewport keeps its top while the document's
            // height changes under it.
            Assert.True(snapshot.TryFindUnmeasuredBand(
                viewportTop, viewportTop + ViewportHeight, 1500, out LazyLayoutBand band));

            Assert.True(band.Bottom <= viewportTop + 1, $"Expected a band above {viewportTop}, got {band.Bottom}.");
        });
    }

    [Fact]
    public async Task UnmeasuredBandSearchStopsWhenTheDocumentIsMeasured()
    {
        await RunAsync(snapshot =>
        {
            for (int pass = 0; pass < 8; pass++)
            {
                snapshot.EnsureMeasuredBand(
                    new LazyLayoutBand(0, snapshot.Size.Height + 1), CancellationToken.None);
            }

            Assert.Equal(snapshot.Blocks.Count, snapshot.MeasuredTopLevelBlockCount);
            Assert.False(snapshot.TryFindUnmeasuredBand(0, ViewportHeight, 2000, out _));
        });
    }

    private async Task RunAsync(Action<LayoutSnapshot> test)
    {
        await using var seed = new CompletedRasterPlacementCloneTests.PlacementFixture();
        string source = string.Join(
            "\n\n",
            Enumerable.Range(0, 160).Select(index =>
                $"Paragraph {index} has **bold** text, `code` and enough words to wrap across the column."));
        var registry = new MarkdownExtensionRegistry().ConfigureGitHubReadmeRegistry();
        var context = new MarkdownLayoutContext(seed.Device, seed.SourceContext.ThemeSnapshot,
            new MarkdownSourceMap(source), registry, FlowDirection.LeftToRight) { RenderResources = _renderResources };
        var document = Markdig.Markdown.Parse(source, registry.BuildPipeline());
        LayoutSnapshot? snapshot = null;
        try
        {
            snapshot = new LayoutBuilder(context)
                .BuildLazy(document, Width, 0, ViewportHeight, 0, CancellationToken.None);
            Assert.True(snapshot.IsLazyLayoutEnabled);
            Assert.True(snapshot.MeasuredTopLevelBlockCount < snapshot.Blocks.Count);
            test(snapshot);
        }
        finally
        {
            try
            {
                if (snapshot is not null)
                    await snapshot.Retire().WaitAsync(TimeSpan.FromSeconds(10));
            }
            finally
            {
                await context.DrainOwnedRetirementsAsync().WaitAsync(TimeSpan.FromSeconds(10));
            }
        }
    }
}
