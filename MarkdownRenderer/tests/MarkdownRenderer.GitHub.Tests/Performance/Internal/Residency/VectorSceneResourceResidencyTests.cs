using System.Threading;
using System.Linq;
using MarkdownRenderer.Math;
using MarkdownRenderer.Document;
using MarkdownRenderer.Extensions;
using MarkdownRenderer.Performance;
using MarkdownRenderer.Theming;
using MarkdownRenderer.Layout.Internal.Boxes;
using MarkdownRenderer.Layout.Internal.Building;
using MarkdownRenderer.Layout.Internal.Context;
using MarkdownRenderer.Layout.Internal.Inlines;
using MarkdownRenderer.Layout.Internal.Inlines.Runs;
using MarkdownRenderer.Layout.Internal.LazyLayout;
using MarkdownRenderer.Layout.Internal.Snapshots;
using MarkdownRenderer.Layout.Internal.Tables;
using MarkdownRenderer.Layout.Internal.Vectors;
using MarkdownRenderer.Performance.Internal;
using MarkdownRenderer.Performance.Internal.Residency;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Windows.UI;
using Xunit;
using static MarkdownRenderer.GitHub.Tests.Performance.Internal.Residency.VectorSceneResidencyFixture;

namespace MarkdownRenderer.GitHub.Tests.Performance.Internal.Residency;

/// <summary>
/// Native resource residency of vector scenes under lazy layout bands.
/// </summary>
/// <remarks>
/// This file is longer than 500 lines because it is the one residency contract
/// for both block vector scenes and inline math runs: band pinning and release,
/// viewport envelopes, cancellation of incoming preparation, paint leases and
/// the inline geometry index all run against the same lazy snapshots from
/// <see cref="VectorSceneResidencyFixture"/>, so the scenarios stay together.
/// </remarks>
public sealed class VectorSceneResourceResidencyTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MovingBandReusesImmutableWeightsForScenesThatRemainPinned(bool inline)
    {
        using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        using LayoutSnapshot snapshot = inline
            ? CreateLazyInlineSnapshot(session, out _, out _)
            : CreateLazySnapshot(session, out _, out _);
        BlockBox first = snapshot.Blocks[0];
        int estimates = snapshot.SceneCommandGraphEstimateCount;
        int activeCount = snapshot.ActivePreparedVectorSceneCount;
        long weight = snapshot.ActivePreparedVectorSceneEstimatedBytes;
        Assert.True(estimates > 0);
        Assert.True(weight > 0);

        // These are actual band exchanges, not warm readiness probes. A very
        // large immutable command/path graph must not be walked for every
        // small scroll while its owning block/run remains in the pin set.
        for (int move = 0; move < 100; move++)
        {
            double inset = 0.001 * move;
              snapshot.EnsureMeasuredBand(
                  new LazyLayoutBand(first.Bounds.Top, first.Bounds.Bottom),
                  new LazyLayoutBand(first.Bounds.Top + inset, first.Bounds.Bottom - inset),
                  CancellationToken.None);
            Assert.Equal(estimates, snapshot.SceneCommandGraphEstimateCount);
            Assert.Equal(activeCount, snapshot.ActivePreparedVectorSceneCount);
            Assert.Equal(weight, snapshot.ActivePreparedVectorSceneEstimatedBytes);
        }
    }

    [Fact]
    public void LazyBandPinsOnlyItsPreparedBlockScenesAndReacquiresOnReturn()
    {
        using var performanceSession = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        LayoutSnapshot snapshot = CreateLazySnapshot(performanceSession, out VectorSceneBox first, out VectorSceneBox second);
        using (snapshot)
        {
            var firstBand = new LazyLayoutBand(first.Bounds.Top, first.Bounds.Bottom);
            Assert.True(snapshot.IsBandMeasured(firstBand));
            Assert.True(first.IsNativeResourcesPrepared);
            Assert.False(second.IsNativeResourcesPrepared);
            Assert.Equal(1, snapshot.ActivePreparedVectorSceneCount);
            Assert.True(snapshot.ActivePreparedVectorSceneEstimatedBytes > 0);
            long firstSceneEstimatedBytes = snapshot.ActivePreparedVectorSceneEstimatedBytes;

            var secondBand = new LazyLayoutBand(second.Bounds.Top, second.Bounds.Bottom);
            Assert.False(snapshot.IsBandMeasured(secondBand));
            LazyLayoutCommit secondCommit = snapshot.EnsureMeasuredBand(secondBand, CancellationToken.None);

            Assert.True(secondCommit.NativeSceneResourcesChanged);
            Assert.True(second.IsNativeResourcesPrepared);
            Assert.False(first.IsNativeResourcesPrepared);
            Assert.Equal(1, snapshot.ActivePreparedVectorSceneCount);
            Assert.True(snapshot.ActivePreparedVectorSceneEstimatedBytes > firstSceneEstimatedBytes);
            Assert.False(snapshot.IsBandMeasured(firstBand));
            Assert.True(snapshot.TryBeginInteraction());
            try
            {
                Assert.False(snapshot.TryGetMeasuredTopLevelBlocksInBandForSceneProbe(
                    first.Bounds.Top,
                    first.Bounds.Bottom,
                    out _));
            }
            finally
            {
                snapshot.EndInteraction();
            }
            long secondRevision = snapshot.LayoutRevision;

            LazyLayoutCommit firstCommit = snapshot.EnsureMeasuredBand(firstBand, CancellationToken.None);

            Assert.False(firstCommit.Changed);
            Assert.True(firstCommit.NativeSceneResourcesChanged);
            Assert.Equal(secondRevision, snapshot.LayoutRevision);
            Assert.True(first.IsNativeResourcesPrepared);
            Assert.False(second.IsNativeResourcesPrepared);
            Assert.Equal(1, snapshot.ActivePreparedVectorSceneCount);
            Assert.True(snapshot.IsBandMeasured(firstBand));
        }
    }

    [Fact]
    public void SceneLeaseIsReleasedWhenTheRequestedMeasuredBandContainsNoScenes()
    {
        using var performanceSession = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        LayoutSnapshot snapshot = CreateLazySnapshot(performanceSession, out VectorSceneBox first, out _);
        using (snapshot)
        {
            Assert.True(first.IsNativeResourcesPrepared);
            Assert.Equal(1, snapshot.ActivePreparedVectorSceneCount);

            StackBox textOnlyBlock = Assert.IsType<StackBox>(snapshot.Blocks[1]);
            var textOnlyBand = new LazyLayoutBand(
                textOnlyBlock.Bounds.Top + 100,
                textOnlyBlock.Bounds.Top + 150);
            LazyLayoutCommit commit = snapshot.EnsureMeasuredBand(textOnlyBand, CancellationToken.None);

            Assert.True(commit.NativeSceneResourcesChanged);
            Assert.False(first.IsNativeResourcesPrepared);
            Assert.Equal(0, snapshot.ActivePreparedVectorSceneCount);
            Assert.Equal(0, snapshot.ActivePreparedVectorSceneEstimatedBytes);
            Assert.True(snapshot.IsBandMeasured(textOnlyBand));
        }
    }

    [Fact]
    public void MovingBetweenMeasuredTextOnlyBandsChangesCoverageWithoutRevisionOrResourceSignal()
    {
        using var performanceSession = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        using LayoutSnapshot snapshot = CreateLazySnapshot(performanceSession, out _, out _);

        StackBox textOnlyBlock = Assert.IsType<StackBox>(snapshot.Blocks[1]);
        var firstTextBand = new LazyLayoutBand(
            textOnlyBlock.Bounds.Top + 100,
            textOnlyBlock.Bounds.Top + 150);
        var secondTextBand = new LazyLayoutBand(
            textOnlyBlock.Bounds.Top + 250,
            textOnlyBlock.Bounds.Top + 300);

        // Leaving the initially pinned vector scene releases that lease and
        // realizes the containing text-only root. This also gives the snapshot
        // a real residency policy whose active visible band can move even when
        // neither target viewport contains scene resources.
        LazyLayoutCommit firstCommit = snapshot.EnsureMeasuredBand(firstTextBand, CancellationToken.None);
        Assert.True(firstCommit.NativeSceneResourcesChanged);
        Assert.Equal(0, snapshot.ActivePreparedVectorSceneCount);
        Assert.True(snapshot.IsBandMeasured(firstTextBand));
        Assert.False(snapshot.IsBandMeasured(secondTextBand));
        long measuredRevision = snapshot.LayoutRevision;

        // The second viewport is already measured by the same StackBox root,
        // so this exchange changes only active-band coverage. Neither geometry
        // revision nor native-resource ownership changes, but readiness for the
        // old band becomes false while readiness for the new band becomes true.
        LazyLayoutCommit secondCommit = snapshot.EnsureMeasuredBand(secondTextBand, CancellationToken.None);

        Assert.False(secondCommit.Changed);
        Assert.False(secondCommit.NativeSceneResourcesChanged);
        Assert.Equal(measuredRevision, secondCommit.LayoutRevision);
        Assert.Equal(measuredRevision, snapshot.LayoutRevision);
        Assert.Equal(0, snapshot.ActivePreparedVectorSceneCount);
        Assert.False(snapshot.IsBandMeasured(firstTextBand));
        Assert.True(snapshot.IsBandMeasured(secondTextBand));
    }

    [Fact]
    public void InitialLazyMeasurementCoversRectRoundedViewportBoundary()
    {
        const double scheduledTop = 549.9;
        const double nextRootTop = 1152;
        double viewportHeight = nextRootTop - scheduledTop - 0.00001;
        var scheduledBand = LazyLayoutBand.FromViewport(scheduledTop, viewportHeight, overscan: 0);
        var visibleViewport = new Rect(0, scheduledTop, 320, viewportHeight);
        var queriedBand = LazyLayoutBand.FromVisibleViewport(scheduledTop, viewportHeight);

        // Windows.Foundation.Rect retains the WinUI viewport's float-rounded
        // coordinates even though its managed properties are doubles. Keep
        // this assertion explicit so a future projection change updates the
        // regression's premise instead of silently hiding it.
        double projectedBottom = System.Math.Max(
            visibleViewport.Bottom,
            visibleViewport.Top + visibleViewport.Height);
        Assert.True(scheduledBand.Bottom < projectedBottom);
        Assert.True(queriedBand.Bottom >= projectedBottom);
        Assert.True(queriedBand.Bottom >= visibleViewport.Bottom);

        var blocks = new BlockBox[37];
        for (int index = 0; index < blocks.Length; index++)
            blocks[index] = new FixedThirtyTwoHeightBox { BlockIndex = index + 1 };

        using var snapshot = new LayoutSnapshot(
            blocks,
            new MarkdownSourceMap(new string(' ', 100)),
            width: 320,
            height: 0,
            blockSpacing: 0);
        snapshot.EnableLazyLayout(
            availableWidth: 320,
            viewportTop: scheduledTop,
            viewportHeight,
            overscan: 0,
            CancellationToken.None,
            initialVisibleVectorBand: queriedBand);

        Assert.Equal(nextRootTop, blocks[^1].Bounds.Top);
        Assert.True(snapshot.IsRealizationBandMeasured(scheduledBand));
        Assert.True(snapshot.IsRealizationBandMeasured(queriedBand));
    }

    [Fact]
    public void FractionalViewportEnvelopeReadiesAlreadyMeasuredTextOnlyBand()
    {
        const double scheduledTop = 549.9;
        const double viewportHeight = 611;
        LazyLayoutBand scheduledBand = LazyLayoutBand.FromViewport(
            scheduledTop, viewportHeight, overscan: 0);
        var visibleViewport = new Rect(0, scheduledTop, 320, viewportHeight);
        LazyLayoutBand queriedBand = LazyLayoutBand.FromViewport(
            visibleViewport.Top, visibleViewport.Height, overscan: 0);
        Assert.True(queriedBand.Bottom > scheduledBand.Bottom);

        using var performanceSession = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        MarkdownLayoutContext context = CreateContext(performanceSession);
        var blocks = new BlockBox[38];
        for (int index = 0; index < blocks.Length; index++)
            blocks[index] = index < 37
                ? new FixedThirtyTwoHeightBox { BlockIndex = index + 1 }
                : CreateVectorBox(context, blockIndex: index + 1);

        IMarkdownVectorSceneResidencyPolicy policy =
            ((IMarkdownPerformanceSessionInternal)performanceSession)
                .CreateVectorSceneResidencyPolicy(blocks)!;

        using var snapshot = new LayoutSnapshot(
            blocks,
            context.SourceMap,
            width: 320,
            height: 0,
            blockSpacing: 0,
            vectorSceneResidencyPolicy: policy);
        snapshot.EnableLazyLayout(
            availableWidth: 320,
            viewportTop: scheduledTop,
            viewportHeight,
            overscan: 0,
            CancellationToken.None,
            initialVisibleVectorBand: scheduledBand);

        // The tiny Rect expansion is inside a root that the ordinary viewport
        // already measured, matching the observed README case: geometry is
        // ready, but exact native-resource readiness still rejects the wider
        // visible rectangle because the active vector lease is too narrow.
        Assert.True(snapshot.IsRealizationBandMeasured(queriedBand));
        Assert.True(snapshot.IsRealizationBandMeasured(scheduledBand));
        Assert.True(snapshot.IsBandMeasured(scheduledBand));
        Assert.False(snapshot.IsBandMeasured(queriedBand));

        LazyLayoutBand visibleEnvelope = LazyLayoutBand.FromVisibleViewport(scheduledTop, viewportHeight);
        _ = snapshot.EnsureMeasuredBand(visibleEnvelope, visibleEnvelope, CancellationToken.None);

        Assert.True(snapshot.IsBandMeasured(queriedBand));
        var strictlyOutside = new LazyLayoutBand(visibleEnvelope.Bottom + 0.001, visibleEnvelope.Bottom + 1);
        Assert.True(snapshot.IsRealizationBandMeasured(strictlyOutside));
        Assert.False(snapshot.IsBandMeasured(strictlyOutside));
    }

    [Fact]
    public void HeightReductionMeasuresNewOrdinalThatMovesIntoFinalBand()
    {
        using var performanceSession = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        MarkdownLayoutContext context = CreateContext(performanceSession);
        var first = new FixedHeightBox { BlockIndex = 1 };
        VectorSceneBox scene = CreateVectorBox(context, blockIndex: 2);
        BlockBox[] blocks = [first, scene];
        IMarkdownVectorSceneResidencyPolicy policy =
            ((IMarkdownPerformanceSessionInternal)performanceSession)
                .CreateVectorSceneResidencyPolicy(blocks)!;
        using var snapshot = new LayoutSnapshot(
            blocks,
            context.SourceMap,
            width: 320,
            height: 0,
            blockSpacing: 12,
            vectorSceneResidencyPolicy: policy);

        // The generic estimated height places the scene after this band. The
        // first block measures to 50 DIPs, shifting the scene into the band.
        snapshot.EnableLazyLayout(
            availableWidth: 320,
            viewportTop: 0,
            viewportHeight: 80,
            overscan: 0,
            CancellationToken.None);

        Assert.True(first.Bounds.Height < 80);
        Assert.True(scene.Bounds.Top < 80);
        Assert.True(scene.IsNativeResourcesPrepared);
        Assert.Equal(2, snapshot.MeasuredTopLevelBlockCount);
        Assert.Equal(1, snapshot.ActivePreparedVectorSceneCount);
        Assert.True(snapshot.IsBandMeasured(new LazyLayoutBand(0, 80)));
    }

    [Fact]
    public async Task CancellationDuringIncomingScenePreparationKeepsOldPinsAndReleasesNewPrefix()
    {
        using var performanceSession = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        using var resourceCreator = new GatedResourceCreator(CanvasDevice.GetSharedDevice());
        MarkdownLayoutContext context = CreateContext(performanceSession, resourceCreator: resourceCreator);
        VectorSceneBox first = CreateVectorBox(context, blockIndex: 1);
        var gap = new StackBox { BlockIndex = 2 };
        for (int index = 0; index < 30; index++)
            gap.Add(new FixedHeightBox { BlockIndex = index + 3 });

        VectorSceneBox incomingPrefix = CreateVectorBox(context, blockIndex: 40, pathOperationCount: 4);
        VectorSceneBox incomingLong = CreateVectorBox(context, blockIndex: 41, pathOperationCount: 8);
        var incoming = new StackBox { BlockIndex = 39 };
        incoming.Add(incomingPrefix);
        incoming.Add(incomingLong);
        BlockBox[] blocks = [first, gap, incoming];
        IMarkdownVectorSceneResidencyPolicy policy =
            ((IMarkdownPerformanceSessionInternal)performanceSession)
                .CreateVectorSceneResidencyPolicy(blocks)!;
        using var snapshot = new LayoutSnapshot(
            blocks,
            context.SourceMap,
            width: 320,
            height: 0,
            blockSpacing: 12,
            vectorSceneResidencyPolicy: policy);
        snapshot.EnableLazyLayout(
            availableWidth: 320,
            viewportTop: 0,
            viewportHeight: 40,
            overscan: 0,
            CancellationToken.None);
        Assert.True(first.IsNativeResourcesPrepared);
        Assert.Equal(1, snapshot.ActivePreparedVectorSceneCount);
        long oldEstimatedBytes = snapshot.ActivePreparedVectorSceneEstimatedBytes;

        using var cancellation = new CancellationTokenSource();
        // Block the actual device request for the second incoming scene after
        // the first has prepared. No path size or machine-speed timing window
        // determines whether this test can cancel the partial incoming set.
        resourceCreator.ShouldGate = () => incomingPrefix.IsNativeResourcesPrepared;
        var nextBand = new LazyLayoutBand(incoming.Bounds.Top, incoming.Bounds.Bottom);
        Task<LazyLayoutCommit> realization = Task.Run(() =>
            snapshot.EnsureMeasuredBand(nextBand, cancellation.Token));
        try
        {
            await resourceCreator.Entered.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(incomingPrefix.IsNativeResourcesPrepared);
            Assert.False(incomingLong.IsNativeResourcesPrepared);
            cancellation.Cancel();
        }
        finally
        {
            resourceCreator.Release();
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await realization.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.True(first.IsNativeResourcesPrepared);
        Assert.False(incomingPrefix.IsNativeResourcesPrepared);
        Assert.False(incomingLong.IsNativeResourcesPrepared);
        Assert.Equal(1, snapshot.ActivePreparedVectorSceneCount);
        Assert.Equal(oldEstimatedBytes, snapshot.ActivePreparedVectorSceneEstimatedBytes);
        Assert.False(snapshot.IsBandMeasured(nextBand));
    }

    [Fact]
    public async Task PaintLeasePreventsOffBandResourceReleaseUntilPaintEnds()
    {
        using var performanceSession = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        LayoutSnapshot snapshot = CreateLazySnapshot(performanceSession, out VectorSceneBox first, out VectorSceneBox second);
        using (snapshot)
        {
            Assert.True(first.IsNativeResourcesPrepared);
            var secondBand = new LazyLayoutBand(second.Bounds.Top, second.Bounds.Bottom);
            Assert.True(snapshot.TryBeginPaint());
            using var entered = new ManualResetEventSlim();
            Task<LazyLayoutCommit> realization = Task.Run(() =>
            {
                entered.Set();
                return snapshot.EnsureMeasuredBand(secondBand, CancellationToken.None);
            });

            try
            {
                Assert.True(entered.Wait(TimeSpan.FromSeconds(2)));
                Thread.Sleep(75);
                Assert.False(realization.IsCompleted);
                Assert.True(first.IsNativeResourcesPrepared);
            }
            finally
            {
                snapshot.EndPaint();
            }

            LazyLayoutCommit commit = await realization.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(commit.NativeSceneResourcesChanged);
            Assert.False(first.IsNativeResourcesPrepared);
            Assert.True(second.IsNativeResourcesPrepared);
        }
    }

    [Fact]
    public void InlineMathResourcesAreDeferredAndFollowNestedMeasuredBands()
    {
        using var performanceSession = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        LayoutSnapshot snapshot = CreateNestedInlineSnapshot(
            performanceSession,
            out InlineContainerBox first,
            out InlineVectorSceneRun firstRun,
            out ListItemBox list,
            out InlineVectorSceneRun markerRun,
            out InlineVectorSceneRun listContentRun,
            out TableBox table,
            out InlineVectorSceneRun tableRun);
        using (snapshot)
        {
            Assert.True(firstRun.IsNativeResourcesPrepared);
            Assert.False(markerRun.IsNativeResourcesPrepared);
            Assert.False(listContentRun.IsNativeResourcesPrepared);
            Assert.False(tableRun.IsNativeResourcesPrepared);
            Assert.Equal(1, snapshot.ActivePreparedVectorSceneCount);

            LazyLayoutCommit listCommit = snapshot.EnsureMeasuredBand(
                new LazyLayoutBand(list.Bounds.Top, list.Bounds.Bottom),
                CancellationToken.None);

            Assert.True(listCommit.NativeSceneResourcesChanged);
            Assert.True(markerRun.IsNativeResourcesPrepared);
            Assert.True(listContentRun.IsNativeResourcesPrepared);
            Assert.False(firstRun.IsNativeResourcesPrepared);
            Assert.False(tableRun.IsNativeResourcesPrepared);
            Assert.Equal(2, snapshot.ActivePreparedVectorSceneCount);

            LazyLayoutCommit tableCommit = snapshot.EnsureMeasuredBand(
                new LazyLayoutBand(table.Bounds.Top, table.Bounds.Bottom),
                CancellationToken.None);

            Assert.True(tableCommit.NativeSceneResourcesChanged);
            Assert.True(tableRun.IsNativeResourcesPrepared);
            Assert.False(markerRun.IsNativeResourcesPrepared);
            Assert.False(listContentRun.IsNativeResourcesPrepared);
            Assert.Equal(1, snapshot.ActivePreparedVectorSceneCount);

            LazyLayoutCommit returnCommit = snapshot.EnsureMeasuredBand(
                new LazyLayoutBand(first.Bounds.Top, first.Bounds.Bottom),
                CancellationToken.None);

            Assert.True(returnCommit.NativeSceneResourcesChanged);
            Assert.True(firstRun.IsNativeResourcesPrepared);
            Assert.False(tableRun.IsNativeResourcesPrepared);
            Assert.Equal(1, snapshot.ActivePreparedVectorSceneCount);
        }
    }

    [Fact]
    public void ManyOffscreenInlineMathRunsRemainUnpreparedUntilTheirBandIsRequested()
    {
        using var performanceSession = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        MarkdownLayoutContext context = CreateContext(performanceSession);
        var blocks = new List<BlockBox>
        {
            CreateInlineBox(context, blockIndex: 1, pathOperationCount: 4),
        };
        var gap = new StackBox { BlockIndex = 2 };
        for (int index = 0; index < 30; index++)
            gap.Add(new FixedHeightBox { BlockIndex = index + 3 });
        blocks.Add(gap);

        var offscreen = new List<InlineContainerBox>();
        for (int index = 0; index < 64; index++)
        {
            InlineContainerBox box = CreateInlineBox(
                context,
                blockIndex: index + 40,
                pathOperationCount: index % 8 + 1);
            blocks.Add(box);
            offscreen.Add(box);
        }

        using LayoutSnapshot snapshot = CreateLazySnapshot(context, performanceSession, blocks);
        Assert.Equal(1, snapshot.ActivePreparedVectorSceneCount);
        Assert.All(offscreen, box =>
            Assert.False(Assert.IsType<InlineVectorSceneRun>(Assert.Single(box.Runs)).IsNativeResourcesPrepared));

        InlineContainerBox target = offscreen[^1];
        LazyLayoutCommit commit = snapshot.EnsureMeasuredBand(
            new LazyLayoutBand(target.Bounds.Top, target.Bounds.Bottom),
            CancellationToken.None);

        Assert.True(commit.NativeSceneResourcesChanged);
        Assert.True(Assert.IsType<InlineVectorSceneRun>(Assert.Single(target.Runs)).IsNativeResourcesPrepared);
        Assert.Equal(1, snapshot.ActivePreparedVectorSceneCount);
        Assert.All(offscreen.Take(offscreen.Count - 1), box =>
            Assert.False(Assert.IsType<InlineVectorSceneRun>(Assert.Single(box.Runs)).IsNativeResourcesPrepared));
    }

    [Fact]
    public void TallMeasuredParagraphPreparesOnlyInlineMathWhoseCharacterRegionIntersectsBand()
    {
        using var performanceSession = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        MarkdownLayoutContext context = CreateContext(performanceSession);
        var paragraph = new InlineContainerBox(context, MarkdownElementKeys.Body)
        {
            BlockIndex = 1,
        };
        var runs = new List<InlineVectorSceneRun>();
        for (int index = 0; index < 10; index++)
        {
            InlineVectorSceneRun run = CreateInlineRun(
                context,
                pathOperationCount: index + 1,
                deferredSceneMarker: $"tall-paragraph:{index}");
            paragraph.Add(run);
            runs.Add(run);
            if (index < 9)
                paragraph.Add(new LineBreakRun(isHard: true));
        }

        using LayoutSnapshot snapshot = CreateLazySnapshot(
            context,
            performanceSession,
            new BlockBox[] { paragraph },
            initialViewportHeight: 16);
        Assert.True(runs[0].IsNativeResourcesPrepared);
        Assert.All(runs.Skip(1), static run => Assert.False(run.IsNativeResourcesPrepared));
        Assert.Equal(1, snapshot.ActivePreparedVectorSceneCount);

        InlineVectorSceneRun lastRun = runs[^1];
        int targetStart = 0;
        foreach (InlineRun run in paragraph.Runs)
        {
            if (ReferenceEquals(run, lastRun))
                break;
            targetStart += run.Text.Length;
        }
        Assert.True(paragraph.TryGetRunCharacterRegionsForSceneReadiness(
            targetStart,
            lastRun.Text.Length,
            out Microsoft.Graphics.Canvas.Text.CanvasTextLayoutRegion[]? targetRegions,
            out double originY));
        Rect targetRegion = Assert.Single(targetRegions!).LayoutBounds;
        var lastBand = new LazyLayoutBand(
            originY + targetRegion.Top + 0.25,
            originY + targetRegion.Bottom - 0.25);
        LazyLayoutCommit commit = snapshot.EnsureMeasuredBand(lastBand, CancellationToken.None);

        Assert.True(commit.NativeSceneResourcesChanged);
        Assert.True(lastRun.IsNativeResourcesPrepared);
        Assert.All(runs.Take(runs.Count - 1), static run => Assert.False(run.IsNativeResourcesPrepared));
        Assert.Equal(1, snapshot.ActivePreparedVectorSceneCount);
    }

    [Fact]
    public void WarmInlineReadinessAndDeferredMarkerProbesUsePublishedIndex()
    {
        using var performanceSession = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        using LayoutSnapshot snapshot = CreateLazyInlineSnapshot(
            performanceSession,
            out InlineContainerBox inline,
            out _);
        var band = new LazyLayoutBand(inline.Bounds.Top, inline.Bounds.Bottom);
        Assert.True(snapshot.IsBandMeasured(band));

        int nativeRegionQueries = inline.SceneCharacterRegionQueryCount;
        int indexBuilds = snapshot.InlineSceneGeometryIndexBuildCount;
        int commandGraphEstimates = snapshot.SceneCommandGraphEstimateCount;
        Assert.True(commandGraphEstimates > 0);
        Assert.Equal(1, snapshot.ActiveInlineSceneGeometryIndexCount);
        for (int probe = 0; probe < 100; probe++)
            Assert.True(snapshot.IsBandMeasured(band));
        Assert.Equal(nativeRegionQueries, inline.SceneCharacterRegionQueryCount);
        Assert.Equal(indexBuilds, snapshot.InlineSceneGeometryIndexBuildCount);
        Assert.Equal(commandGraphEstimates, snapshot.SceneCommandGraphEstimateCount);

        Assert.True(snapshot.TryBeginPaint());
        try
        {
            bool resourcesReady = CollectDeferredSceneMarkers(
                snapshot, band.Top, band.Bottom, viewport: null, out IReadOnlyList<string> markerKeys);
            Assert.Contains("inline-scene:1", markerKeys);
            Assert.True(resourcesReady);
        }
        finally
        {
            snapshot.EndPaint();
        }

        Assert.Equal(nativeRegionQueries, inline.SceneCharacterRegionQueryCount);
        Assert.Equal(indexBuilds, snapshot.InlineSceneGeometryIndexBuildCount);
        Assert.Equal(commandGraphEstimates, snapshot.SceneCommandGraphEstimateCount);
    }

    [Fact]
    public void InlineIndexRejectsStaleGenerationThenRebuildsFromBoxRetainedGeometry()
    {
        using var performanceSession = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        using LayoutSnapshot snapshot = CreateLazyInlineSnapshot(
            performanceSession,
            out InlineContainerBox inline,
            out _);
        Rect previousBounds = inline.Bounds;
        var band = new LazyLayoutBand(previousBounds.Top, previousBounds.Bottom);
        Assert.True(snapshot.IsBandMeasured(band));

        int priorIndexBuilds = snapshot.InlineSceneGeometryIndexBuildCount;
        int priorNativeQueries = inline.SceneCharacterRegionQueryCount;
        float resizedWidth = (float)System.Math.Max(1, previousBounds.Width - 17);
        inline.Measure(resizedWidth);
        inline.Arrange((float)previousBounds.X, (float)previousBounds.Y, resizedWidth);
        Assert.Equal(priorNativeQueries + 1, inline.SceneCharacterRegionQueryCount);
        Assert.False(snapshot.IsBandMeasured(band));

        _ = snapshot.EnsureMeasuredBand(band, CancellationToken.None);
        Assert.True(snapshot.IsBandMeasured(band));
        Assert.Equal(priorNativeQueries + 1, inline.SceneCharacterRegionQueryCount);
        Assert.Equal(priorIndexBuilds + 1, snapshot.InlineSceneGeometryIndexBuildCount);
        Assert.Equal(1, snapshot.ActiveInlineSceneGeometryIndexCount);
    }

    [Fact]
    public void InlineIndexUsesCurrentSnappedOriginAndExactRtlRegionsForHorizontalReadiness()
    {
        using var performanceSession = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        MarkdownLayoutContext context = CreateContext(
            performanceSession,
            flowDirection: FlowDirection.RightToLeft,
            rasterizationScale: 1.5);
        InlineContainerBox inline = CreateInlineBox(context, blockIndex: 1, pathOperationCount: 4);
        using LayoutSnapshot snapshot = CreateLazySnapshot(
            context,
            performanceSession,
            new BlockBox[] { inline });

        // Simulate an arranged table/RTL column origin changing without a new
        // DirectWrite layout. The cached index is local scalar geometry, so the
        // query must translate it through the latest snapped origin.
        inline.Arrange(100.4f, (float)inline.Bounds.Y, (float)inline.Bounds.Width);
        Assert.True(inline.TryGetRunCharacterRegionsForSceneReadiness(
            0,
            1,
            out Microsoft.Graphics.Canvas.Text.CanvasTextLayoutRegion[]? regions,
            out double originY));
        Rect local = Assert.Single(regions!).LayoutBounds;
        inline.GetSceneGeometryOrigin(out double originX, out _);
        Assert.InRange(System.Math.Abs(originX * 1.5 - System.Math.Round(originX * 1.5)), 0, 0.0001);
        Rect screen = new(originX + local.X, originY + local.Y, local.Width, local.Height);
        Assert.True(screen.Width > 0 && screen.Height > 0);

        var visible = new Rect(
            screen.X + screen.Width * 0.25,
            screen.Y + screen.Height * 0.25,
            System.Math.Max(0.01, screen.Width * 0.5),
            System.Math.Max(0.01, screen.Height * 0.5));
        var outside = screen.Left > inline.Bounds.Left + 4
            ? new Rect(inline.Bounds.Left + 1, screen.Top, screen.Left - inline.Bounds.Left - 2, System.Math.Max(0.01, screen.Height))
            : new Rect(screen.Right + 2, screen.Top, inline.Bounds.Right - screen.Right - 3, System.Math.Max(0.01, screen.Height));
        Assert.True(outside.Width > 0);

        Assert.True(snapshot.TryBeginPaint());
        try
        {
            bool visibleReady = CollectDeferredSceneMarkers(
                snapshot, visible.Top, visible.Bottom, visible, out IReadOnlyList<string> visibleMarkers);
            Assert.Contains("inline-scene:1", visibleMarkers);
            Assert.True(visibleReady);

            bool outsideReady = CollectDeferredSceneMarkers(
                snapshot, outside.Top, outside.Bottom, outside, out IReadOnlyList<string> outsideMarkers);
            Assert.DoesNotContain("inline-scene:1", outsideMarkers);
            Assert.True(outsideReady);
        }
        finally
        {
            snapshot.EndPaint();
        }
    }

    [Fact]
    public async Task CancellationWhileBuildingIncomingInlineIndexPreservesOldActiveIndexAndPins()
    {
        using var performanceSession = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        using var resourceCreator = new GatedResourceCreator(CanvasDevice.GetSharedDevice());
        MarkdownLayoutContext context = CreateContext(performanceSession, resourceCreator: resourceCreator);
        InlineContainerBox first = CreateInlineBox(context, blockIndex: 1, pathOperationCount: 4);
        InlineVectorSceneRun retainedRun = Assert.IsType<InlineVectorSceneRun>(Assert.Single(first.Runs));
        var gap = new StackBox { BlockIndex = 2 };
        for (int index = 0; index < 30; index++)
            gap.Add(new FixedHeightBox { BlockIndex = index + 3 });

        var incoming = new InlineContainerBox(context, MarkdownElementKeys.Body) { BlockIndex = 40 };
        InlineVectorSceneRun prefix = CreateInlineRun(context, pathOperationCount: 4, deferredSceneMarker: "incoming-prefix");
        InlineVectorSceneRun longRun = CreateInlineRun(context, pathOperationCount: 200_000, deferredSceneMarker: "incoming-long");
        incoming.Add(prefix);
        incoming.Add(longRun);
        using LayoutSnapshot snapshot = CreateLazySnapshot(
            context,
            performanceSession,
            new BlockBox[] { first, gap, incoming });
        var firstBand = new LazyLayoutBand(first.Bounds.Top, first.Bounds.Bottom);
        Assert.True(retainedRun.IsNativeResourcesPrepared);
        Assert.True(snapshot.IsBandMeasured(firstBand));
        int oldIndexBuilds = snapshot.InlineSceneGeometryIndexBuildCount;

        using var cancellation = new CancellationTokenSource();
        // Block the second scene's real device request after the prefix is
        // prepared. Polling a machine-speed-dependent prefix window can miss
        // the entire transition when other test workers are runnable.
        resourceCreator.ShouldGate = () => prefix.IsNativeResourcesPrepared;
        var incomingBand = new LazyLayoutBand(incoming.Bounds.Top, incoming.Bounds.Bottom);
        Task<LazyLayoutCommit> realization = Task.Run(() =>
            snapshot.EnsureMeasuredBand(incomingBand, cancellation.Token));
        try
        {
            await resourceCreator.Entered.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(prefix.IsNativeResourcesPrepared);
            Assert.False(longRun.IsNativeResourcesPrepared);
            cancellation.Cancel();
        }
        finally
        {
            resourceCreator.Release();
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await realization.WaitAsync(TimeSpan.FromSeconds(10)));
        // The failed live measure cannot be certified by the restored outer
        // bounds alone. Preserve the old active resource/index custody for
        // eventual retirement, while refusing all new reads from this snapshot.
        Assert.True(snapshot.GeometryRecoveryRequired);
        Assert.True(retainedRun.IsNativeResourcesPrepared);
        Assert.False(prefix.IsNativeResourcesPrepared);
        Assert.False(longRun.IsNativeResourcesPrepared);
        Assert.False(snapshot.IsBandMeasured(firstBand));
        Assert.False(snapshot.TryBeginPaint());
        Assert.Equal(1, snapshot.ActivePreparedVectorSceneCount);
        Assert.Equal(1, snapshot.ActiveInlineSceneGeometryIndexCount);
        Assert.True(snapshot.InlineSceneGeometryIndexBuildCount > oldIndexBuilds);
    }

    [Fact]
    public async Task PaintLeasePreventsInlineMathReleaseWhilePaintIsHeld()
    {
        using var performanceSession = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        LayoutSnapshot snapshot = CreateLazyInlineSnapshot(
            performanceSession,
            out InlineContainerBox first,
            out InlineContainerBox second);
        using (snapshot)
        {
            InlineVectorSceneRun firstRun = Assert.IsType<InlineVectorSceneRun>(Assert.Single(first.Runs));
            InlineVectorSceneRun secondRun = Assert.IsType<InlineVectorSceneRun>(Assert.Single(second.Runs));
            Assert.True(firstRun.IsNativeResourcesPrepared);
            Assert.False(secondRun.IsNativeResourcesPrepared);

            var secondBand = new LazyLayoutBand(second.Bounds.Top, second.Bounds.Bottom);
            Assert.True(snapshot.TryBeginPaint());
            using var entered = new ManualResetEventSlim();
            Task<LazyLayoutCommit> realization = Task.Run(() =>
            {
                entered.Set();
                return snapshot.EnsureMeasuredBand(secondBand, CancellationToken.None);
            });

            try
            {
                Assert.True(entered.Wait(TimeSpan.FromSeconds(2)));
                Thread.Sleep(75);
                Assert.False(realization.IsCompleted);
                Assert.True(firstRun.IsNativeResourcesPrepared);
                Assert.False(secondRun.IsNativeResourcesPrepared);
            }
            finally
            {
                snapshot.EndPaint();
            }

            LazyLayoutCommit commit = await realization.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(commit.NativeSceneResourcesChanged);
            Assert.False(firstRun.IsNativeResourcesPrepared);
            Assert.True(secondRun.IsNativeResourcesPrepared);
        }
    }

    [Fact]
    public void InlineDrawingNeverPreparesFromPaintAndNoSessionRemainsEager()
    {
        MarkdownLayoutContext eagerContext = CreateContext(performanceSession: null);
        var eagerRun = CreateInlineRun(eagerContext, pathOperationCount: 4);
        try
        {
            Assert.True(eagerRun.IsNativeResourcesPrepared);
        }
        finally
        {
            eagerRun.Dispose();
        }

        using var performanceSession = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        MarkdownLayoutContext deferredContext = CreateContext(performanceSession);
        deferredContext.UseProgressiveNativeSceneResidency = true;
        var deferredRun = CreateInlineRun(deferredContext, pathOperationCount: 4);
        try
        {
            Assert.False(deferredRun.IsNativeResourcesPrepared);
            using var target = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), 64, 64, 96);
            using CanvasDrawingSession drawingSession = target.CreateDrawingSession();
            deferredRun.Paint(
                deferredContext.PlacementTransform,
                drawingSession,
                baseX: 0,
                baseY: 0,
                Color.FromArgb(0xFF, 0x20, 0x20, 0x20),
                highContrast: false);

            Assert.False(deferredRun.IsNativeResourcesPrepared);
        }
        finally
        {
            deferredRun.Dispose();
        }
    }

    [Fact]
    public async Task LayoutBuilderDefersOnlyActualLazyBuildAndPreparesItsInitialInlineBand()
    {
        string source = string.Join("\n\n", Enumerable.Repeat("$x$", 32));
        using MarkdownEngine engine = new MarkdownEngineBuilder()
            .UseMathematics()
            .Build();
        MarkdownRenderer.Document.MarkdownDocument document = await engine.ParseAsync(source);
        using var performanceSession = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        MarkdownLayoutContext context = CreateBuildContext(document, performanceSession);
        var builder = new LayoutBuilder(context, semanticDocument: document);

        using (LayoutSnapshot eagerSnapshot = builder.Build(document.ParsedDocument!, availableWidth: 420))
        {
            InlineVectorSceneRun[] eagerRuns = GetTopLevelInlineSceneRuns(eagerSnapshot);
            Assert.NotEmpty(eagerRuns);
            Assert.All(eagerRuns, static run => Assert.True(run.IsNativeResourcesPrepared));
        }

        const double initialBandBottom = 72;
        using (LayoutSnapshot lazySnapshot = builder.BuildLazy(
            document.ParsedDocument!,
            availableWidth: 420,
            viewportTop: 0,
            viewportHeight: initialBandBottom,
            overscan: 0,
            CancellationToken.None))
        {
            InlineContainerBox[] inlineBlocks = lazySnapshot.Blocks.OfType<InlineContainerBox>().ToArray();
            Assert.NotEmpty(inlineBlocks);
            InlineVectorSceneRun[] visibleRuns = inlineBlocks
                .Where(block => new LazyLayoutBand(0, initialBandBottom)
                    .Intersects(block.Bounds.Top, block.Bounds.Bottom))
                .SelectMany(static block => block.Runs.OfType<InlineVectorSceneRun>())
                .ToArray();
            InlineVectorSceneRun[] offscreenRuns = inlineBlocks
                .Where(block => !new LazyLayoutBand(0, initialBandBottom)
                    .Intersects(block.Bounds.Top, block.Bounds.Bottom))
                .SelectMany(static block => block.Runs.OfType<InlineVectorSceneRun>())
                .ToArray();

            Assert.NotEmpty(visibleRuns);
            Assert.NotEmpty(offscreenRuns);
            Assert.All(visibleRuns, static run => Assert.True(run.IsNativeResourcesPrepared));
            Assert.All(offscreenRuns, static run => Assert.False(run.IsNativeResourcesPrepared));
            Assert.Equal(visibleRuns.Length, lazySnapshot.ActivePreparedVectorSceneCount);
        }

        // BuildLazy must restore the context hint even when the same builder
        // and context are reused for a later ordinary eager build.
        using LayoutSnapshot reusedEagerSnapshot = builder.Build(
            document.ParsedDocument!,
            availableWidth: 420);
        InlineVectorSceneRun[] reusedRuns = GetTopLevelInlineSceneRuns(reusedEagerSnapshot);
        Assert.NotEmpty(reusedRuns);
        Assert.All(reusedRuns, static run => Assert.True(run.IsNativeResourcesPrepared));
    }
}
