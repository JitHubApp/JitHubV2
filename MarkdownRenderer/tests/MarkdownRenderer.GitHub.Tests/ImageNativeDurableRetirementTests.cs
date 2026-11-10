using System.Runtime.CompilerServices;
using MarkdownRenderer.Extensions;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Layout.Internal.Images;
using MarkdownRenderer.Layout.Internal.Images.BitmapCache;
using MarkdownRenderer.Layout.Internal.Images.Placement;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

[Collection(SharedCanvasBitmapCacheTestCollection.Name)]
public sealed class ImageNativeDurableRetirementTests
{
    private const float Width = 320f;

    [Fact]
    public async Task CaptionOptionsFailureKeepsBothOldAndFailedNativeLayoutsInSourceFreeCustody()
    {
        using var harness = new ImageNativeRetirementHarness();
        var disposer = new ImageLayoutDisposalProbe();
        var retirement = new NativeResourceRetirement(createTimer: false, disposer.Dispose);
        ImageEvidence evidence = await CreateFailedCaptionEvidence(harness, retirement, disposer);

        // The image's active placeholder is synchronously disposed during
        // RetirementTask; the failed caption-construction candidate is the
        // durable manager-owned resource.
        Assert.Equal(1, retirement.PendingRecordCount);
        Assert.Equal(1, retirement.PendingResourceCount);
        Assert.True(evidence.PlaceholderRetirementRecordWasReserved);
        Assert.True(evidence.CaptionCandidateWasPublished);
        CollectGarbage();
        AssertCollected(evidence.Image);
        AssertCollected(evidence.Context);

        disposer.FailNextDisposal();
        Assert.False(retirement.DrainOnce());
        Assert.Equal(1, retirement.PendingRecordCount);
        Assert.Equal(1, retirement.PendingResourceCount);
        Assert.Single(disposer.ObservedLayouts, IsAlive);
        AssertRetained(Assert.Single(disposer.ObservedLayouts, IsAlive));

        disposer.AllowDisposal();
        Assert.True(retirement.DrainOnce());
        Assert.Equal(0, retirement.PendingRecordCount);
        Assert.Equal(0, retirement.PendingResourceCount);
        CollectGarbage();
        Assert.All(disposer.ObservedLayouts, AssertCollected);
        GC.KeepAlive(retirement);
    }

    [Fact]
    public async Task TerminalDisposalAttemptsEveryLayoutAndPublishesFailedOwnerBeforeReturning()
    {
        using var harness = new ImageNativeRetirementHarness();
        var disposer = new ImageLayoutDisposalProbe();
        var retirement = new NativeResourceRetirement(createTimer: false, disposer.Dispose);
        TerminalFailureEvidence evidence = await CreateTerminalFailureEvidence(harness, retirement, disposer);

        Assert.Equal("Injected native image-layout disposal failure.", evidence.Failure.Message);
        Assert.Equal(2, disposer.ObservedLayouts.Length);
        Assert.Equal(1, retirement.PendingRecordCount);
        Assert.Equal(1, retirement.PendingResourceCount);
        CollectGarbage();
        AssertCollected(evidence.Image);
        AssertCollected(evidence.Context);
        Assert.Single(disposer.ObservedLayouts, IsAlive);

        disposer.AllowDisposal();
        Assert.True(retirement.DrainOnce());
        Assert.Equal(0, retirement.PendingRecordCount);
        Assert.Equal(0, retirement.PendingResourceCount);
        CollectGarbage();
        Assert.All(disposer.ObservedLayouts, AssertCollected);
        GC.KeepAlive(retirement);
    }

    [Fact]
    public async Task DisposeDuringInlineCheckpointRetainsOriginalPairAndRevokesLateRollback()
    {
        using var harness = new ImageNativeRetirementHarness();
        var disposer = new ImageLayoutDisposalProbe();
        var retirement = new NativeResourceRetirement(createTimer: false, disposer.Dispose);
        DisposedCheckpointEvidence evidence = await CreateDisposedCheckpointEvidence(
            harness, retirement, disposer);

        Assert.Equal("Injected native image-layout disposal failure.", evidence.Failure.Message);
        Assert.Equal(4, disposer.ObservedLayouts.Length);
        Assert.Equal(1, retirement.PendingRecordCount);
        Assert.Equal(1, retirement.PendingResourceCount);
        CollectGarbage();
        AssertCollected(evidence.Image);
        AssertCollected(evidence.Context);
        Assert.Single(disposer.ObservedLayouts, IsAlive);

        disposer.AllowDisposal();
        Assert.True(retirement.DrainOnce());
        Assert.Equal(0, retirement.PendingResourceCount);
        CollectGarbage();
        Assert.All(disposer.ObservedLayouts, AssertCollected);
        GC.KeepAlive(retirement);
    }

    [Fact]
    public async Task InlineCheckpointRollbackRestoresOriginalPairAndCommitRetiresItOnce()
    {
        using var harness = new ImageNativeRetirementHarness();
        var disposer = new ImageLayoutDisposalProbe();
        var retirement = new NativeResourceRetirement(createTimer: false, disposer.Dispose);
        var image = new ImageBox(harness.CreateContext(), harness.RenderResources.Images, string.Empty, "inline failure", retirement);

        _ = image.MeasureInline(180f, 22f);
        CanvasTextLayout original = Assert.IsType<CanvasTextLayout>(image.MeasureCustody.GetLayoutState().Placeholder);
        NativeResourceRetirementRecord originalRecord = Assert.IsType<NativeResourceRetirementRecord>(
            image.MeasureCustody.GetLayoutState().PlaceholderRecord);

        InlineMeasureCheckpoint rollback = image.BeginInlineCheckpoint();
        _ = image.MeasureInline(100f, 22f, rollback);
        CanvasTextLayout rolledBackCandidate = Assert.IsType<CanvasTextLayout>(image.MeasureCustody.GetLayoutState().Placeholder);
        NativeResourceRetirementRecord rolledBackRecord = Assert.IsType<NativeResourceRetirementRecord>(
            image.MeasureCustody.GetLayoutState().PlaceholderRecord);
        Assert.NotSame(original, rolledBackCandidate);
        Assert.NotSame(originalRecord, rolledBackRecord);
        rollback.Rollback();

        Assert.Same(original, image.MeasureCustody.GetLayoutState().Placeholder);
        Assert.Same(originalRecord, image.MeasureCustody.GetLayoutState().PlaceholderRecord);
        Assert.True(image.MeasureCustody.GetLayoutState().HasValidCustody);
        Assert.Equal(1, retirement.PendingResourceCount);
        Assert.True(retirement.DrainOnce());

        InlineMeasureCheckpoint commit = image.BeginInlineCheckpoint();
        _ = image.MeasureInline(80f, 22f, commit);
        CanvasTextLayout committed = Assert.IsType<CanvasTextLayout>(image.MeasureCustody.GetLayoutState().Placeholder);
        NativeResourceRetirementRecord committedRecord = Assert.IsType<NativeResourceRetirementRecord>(
            image.MeasureCustody.GetLayoutState().PlaceholderRecord);
        Assert.NotSame(original, committed);
        Assert.NotSame(originalRecord, committedRecord);
        commit.Commit();
        Assert.Equal(1, retirement.PendingResourceCount);
        Assert.True(retirement.DrainOnce());
        Assert.Same(committed, image.MeasureCustody.GetLayoutState().Placeholder);
        Assert.Same(committedRecord, image.MeasureCustody.GetLayoutState().PlaceholderRecord);

        image.Dispose();
        await image.RetirementTask.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0, retirement.PendingResourceCount);
        Assert.Equal(0, retirement.PendingRecordCount);
        GC.KeepAlive(retirement);
    }

    [Fact]
    public async Task PlacementTransferMovesCaptionLayoutAndItsRetirementRecordAsOnePair()
    {
        await using var fixture = new CompletedRasterPlacementCloneTests.PlacementFixture();
        var disposer = new ImageLayoutDisposalProbe();
        var retirement = new NativeResourceRetirement(createTimer: false, disposer.Dispose);
        ImageBox source = CreateCompletedSourceImage(fixture, retirement, "Placement custody");
        LayoutSnapshot snapshot = CreateSourceSnapshot(fixture, source);
        MarkdownLayoutContext targetContext = fixture.CreateContext();
        ImageBox? prepared = null;

        try
        {
            Assert.True(source.TryCreateCompletedRasterPlacementClone(
                targetContext, out prepared, out SharedCanvasBitmapCache.Lease? expectedLease));
            Assert.NotNull(prepared);
            Assert.NotNull(expectedLease);

            NativeLayoutPair sourceLayouts = NativeLayoutPair.Capture(source);
            var transfer = new ImageMeasuredPlacementTransfer(
                source,
                prepared,
                expectedLease,
                ExpectedSvgMetadataFence: null,
                ExpectedInlineLayout: false,
                ExpectedAvailableWidth: Width,
                ExpectedPreparedAvailableWidth: Width);
            Assert.True(transfer.ExpectedSourceNativeLayouts.IsValid);
            sourceLayouts.AssertFencedBy(transfer.ExpectedSourceNativeLayouts);

            _ = prepared.Measure(Width);
            prepared.Arrange(0, 0, Width);
            Assert.True(prepared.TryCaptureNativeLayoutPlacementFence(out var preparedFence));
            transfer = transfer with { ExpectedPreparedNativeLayouts = preparedFence };

            NativeLayoutPair preparedLayouts = NativeLayoutPair.Capture(prepared);
            preparedLayouts.AssertFencedBy(preparedFence);

            // A same-reference owner mutation still advances the native
            // generation. The stale source fence must not authorize a swap.
            source.MeasureCustody.ReplaceCaptionRecord(sourceLayouts.CaptionRecord);
            Assert.True(source.TryCaptureNativeLayoutPlacementFence(out var currentSourceFence));
            Assert.False(TryTransferDuringInteraction(snapshot, transfer));
            transfer = transfer with { ExpectedSourceNativeLayouts = currentSourceFence };

            prepared.MeasureCustody.ReplaceCaptionRecord(preparedLayouts.CaptionRecord);
            Assert.True(prepared.TryCaptureNativeLayoutPlacementFence(out var currentPreparedFence));
            Assert.False(TryTransferDuringInteraction(snapshot, transfer));
            transfer = transfer with { ExpectedPreparedNativeLayouts = currentPreparedFence };

            Assert.True(TryTransferDuringInteraction(snapshot, transfer));

            AssertNativeLayoutPairsSwapped(source, prepared, sourceLayouts, preparedLayouts);
        }
        finally
        {
            await DiscardPreparedAndRetireSnapshotAsync(targetContext, prepared, snapshot);
        }

        Assert.Equal(0, retirement.PendingResourceCount);
        Assert.Equal(0, retirement.PendingRecordCount);
        GC.KeepAlive(retirement);
    }

    [Fact]
    public async Task PlacementTransferRejectsCrossImageRetirementRecordAlias()
    {
        await using var fixture = new CompletedRasterPlacementCloneTests.PlacementFixture();
        var disposer = new ImageLayoutDisposalProbe();
        var retirement = new NativeResourceRetirement(createTimer: false, disposer.Dispose);
        ImageBox source = CreateCompletedSourceImage(fixture, retirement, "Record alias");
        LayoutSnapshot snapshot = CreateSourceSnapshot(fixture, source);
        MarkdownLayoutContext targetContext = fixture.CreateContext();
        ImageBox? prepared = null;
        NativeResourceRetirementRecord? originalPreparedRecord = null;

        try
        {
            Assert.True(source.TryCreateCompletedRasterPlacementClone(
                targetContext, out prepared, out SharedCanvasBitmapCache.Lease? expectedLease));
            ImageBox candidate = Assert.IsType<ImageBox>(prepared);
            Assert.NotNull(expectedLease);
            _ = candidate.Measure(Width);
            candidate.Arrange(0, 0, Width);

            NativeResourceRetirementRecord sharedRecord = Assert.IsType<NativeResourceRetirementRecord>(
                source.MeasureCustody.GetLayoutState().CaptionRecord);
            originalPreparedRecord = Assert.IsType<NativeResourceRetirementRecord>(
                candidate.MeasureCustody.GetLayoutState().CaptionRecord);
            candidate.MeasureCustody.ReplaceCaptionRecord(sharedRecord);
            var transfer = new ImageMeasuredPlacementTransfer(
                source,
                candidate,
                expectedLease,
                ExpectedSvgMetadataFence: null,
                ExpectedAvailableWidth: Width,
                ExpectedPreparedAvailableWidth: Width);

            Assert.True(transfer.ExpectedSourceNativeLayouts.IsValid);
            Assert.Same(sharedRecord,
                transfer.ExpectedPreparedNativeLayouts!.Value.CaptionRetirementRecord);
            Assert.False(TryTransferDuringInteraction(snapshot, transfer));

            Assert.Same(sharedRecord, source.MeasureCustody.GetLayoutState().CaptionRecord);
            Assert.Same(sharedRecord, candidate.MeasureCustody.GetLayoutState().CaptionRecord);
        }
        finally
        {
            if (prepared is not null && originalPreparedRecord is not null)
                prepared.MeasureCustody.ReplaceCaptionRecord(originalPreparedRecord);
            await DiscardPreparedAndRetireSnapshotAsync(targetContext, prepared, snapshot);
        }

        Assert.Equal(0, retirement.PendingResourceCount);
        GC.KeepAlive(retirement);
    }

    private static ImageBox CreateCompletedSourceImage(
        CompletedRasterPlacementCloneTests.PlacementFixture fixture,
        NativeResourceRetirement retirement,
        string altText)
    {
        ImageBox source = new(
            fixture.SourceContext, fixture.SourceContext.ImageResources,
            fixture.RasterSource,
            altText,
            retirement)
        {
            BlockIndex = fixture.Source.BlockIndex,
        };
        // The fixture stored the raster for this built-in source, so the
        // constructor's synchronous cache probe completes the image.
        Assert.NotNull(source.Bitmap);
        _ = source.Measure(Width);
        source.Arrange(0, 0, Width);
        return source;
    }

    private static LayoutSnapshot CreateSourceSnapshot(
        CompletedRasterPlacementCloneTests.PlacementFixture fixture,
        ImageBox source) => new(new BlockBox[] { source }, fixture.SourceContext.SourceMap,
            Width, (float)source.Bounds.Height);

    private static bool TryTransferDuringInteraction(LayoutSnapshot snapshot, ImageMeasuredPlacementTransfer transfer)
    {
        Assert.True(snapshot.TryBeginInteraction());
        try
        {
            return ImageBox.TryTransferMeasuredPlacementsFromCompletedClones(
                [transfer], currentProviderCacheGeneration: 0, expectedAvailableWidth: Width);
        }
        finally
        {
            snapshot.EndInteraction();
        }
    }

    private static async Task DiscardPreparedAndRetireSnapshotAsync(
        MarkdownLayoutContext targetContext,
        ImageBox? prepared,
        LayoutSnapshot snapshot)
    {
        if (prepared is not null && !prepared.GetWorkState().LifetimeDisposed)
        {
            targetContext.DiscardConstructionBlock(prepared);
            await prepared.RetirementTask.WaitAsync(TimeSpan.FromSeconds(10));
        }
        await snapshot.Retire().WaitAsync(TimeSpan.FromSeconds(10));
    }

    private static void AssertNativeLayoutPairsSwapped(
        ImageBox source,
        ImageBox prepared,
        NativeLayoutPair sourceLayouts,
        NativeLayoutPair preparedLayouts)
    {
        Assert.Same(preparedLayouts.Placeholder, source.MeasureCustody.GetLayoutState().Placeholder);
        Assert.Same(preparedLayouts.PlaceholderRecord, source.MeasureCustody.GetLayoutState().PlaceholderRecord);
        Assert.Same(sourceLayouts.Placeholder, prepared.MeasureCustody.GetLayoutState().Placeholder);
        Assert.Same(sourceLayouts.PlaceholderRecord, prepared.MeasureCustody.GetLayoutState().PlaceholderRecord);
        Assert.Same(preparedLayouts.Caption, source.MeasureCustody.GetLayoutState().Caption);
        Assert.Same(preparedLayouts.CaptionRecord, source.MeasureCustody.GetLayoutState().CaptionRecord);
        Assert.Same(sourceLayouts.Caption, prepared.MeasureCustody.GetLayoutState().Caption);
        Assert.Same(sourceLayouts.CaptionRecord, prepared.MeasureCustody.GetLayoutState().CaptionRecord);
        Assert.True(source.MeasureCustody.GetLayoutState().HasValidCustody);
        Assert.True(prepared.MeasureCustody.GetLayoutState().HasValidCustody);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private async Task<ImageEvidence> CreateFailedCaptionEvidence(
        ImageNativeRetirementHarness harness,
        NativeResourceRetirement retirement,
        ImageLayoutDisposalProbe disposer)
    {
        MarkdownLayoutContext context = harness.CreateContext();
        int optionsCalls = 0;
        var image = new ImageBox(
            context, context.ImageResources,
            string.Empty,
            "caption failure",
            retirement,
            _ =>
            {
                if (Interlocked.Increment(ref optionsCalls) == 2)
                    throw new InvalidOperationException("Injected caption Options failure.");
            });

        InvalidOperationException failure = Assert.Throws<InvalidOperationException>(() => image.Measure(Width));
        Assert.Equal("Injected caption Options failure.", failure.Message);
        Assert.NotNull(image.MeasureCustody.GetLayoutState().Placeholder);
        Assert.NotNull(image.MeasureCustody.GetLayoutState().PlaceholderRecord);
        Assert.Null(image.MeasureCustody.GetLayoutState().Caption);
        Assert.True(image.MeasureCustody.GetLayoutState().HasValidCustody);
        Assert.Equal(1, retirement.PendingRecordCount);
        Assert.Equal(1, retirement.PendingResourceCount);
        bool placeholderRecordWasReserved = image.MeasureCustody.GetLayoutState().PlaceholderRecord is not null;

        image.Dispose();
        await image.RetirementTask.WaitAsync(TimeSpan.FromSeconds(10));
        var evidence = new ImageEvidence(
            disposer,
            new WeakReference<ImageBox>(image),
            new WeakReference<MarkdownLayoutContext>(context),
            placeholderRecordWasReserved,
            retirement.PendingRecordCount == 1);
        return evidence;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private async Task<TerminalFailureEvidence> CreateTerminalFailureEvidence(
        ImageNativeRetirementHarness harness,
        NativeResourceRetirement retirement,
        ImageLayoutDisposalProbe disposer)
    {
        MarkdownLayoutContext context = harness.CreateContext();
        var image = new ImageBox(context, context.ImageResources, string.Empty, "terminal failure", retirement);
        _ = image.Measure(Width);
        Assert.NotNull(image.MeasureCustody.GetLayoutState().Placeholder);
        Assert.NotNull(image.MeasureCustody.GetLayoutState().Caption);
        var imageReference = new WeakReference<ImageBox>(image);
        var contextReference = new WeakReference<MarkdownLayoutContext>(context);

        disposer.FailNextDisposal();
        image.Dispose();
        InvalidOperationException failure = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await image.RetirementTask);
        return new TerminalFailureEvidence(failure, imageReference, contextReference);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private async Task<DisposedCheckpointEvidence> CreateDisposedCheckpointEvidence(
        ImageNativeRetirementHarness harness,
        NativeResourceRetirement retirement,
        ImageLayoutDisposalProbe disposer)
    {
        MarkdownLayoutContext context = harness.CreateContext();
        var image = new ImageBox(context, context.ImageResources, string.Empty, "disposed checkpoint", retirement);
        _ = image.MeasureInline(180f, 22f);
        CanvasTextLayout original = Assert.IsType<CanvasTextLayout>(image.MeasureCustody.GetLayoutState().Placeholder);
        InlineMeasureCheckpoint checkpoint = image.BeginInlineCheckpoint();
        _ = image.MeasureInline(100f, 22f, checkpoint);
        CanvasTextLayout candidate = Assert.IsType<CanvasTextLayout>(image.MeasureCustody.GetLayoutState().Placeholder);
        Assert.NotSame(original, candidate);
        var imageReference = new WeakReference<ImageBox>(image);
        var contextReference = new WeakReference<MarkdownLayoutContext>(context);

        disposer.FailNextDisposal();
        image.Dispose();
        // Invalidation is synchronous with Dispose; this late token must not
        // publish or restore either pair before RetirementTask owns them.
        checkpoint.Rollback();
        InvalidOperationException failure = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await image.RetirementTask);
        return new DisposedCheckpointEvidence(failure, imageReference, contextReference);
    }

    private static void AssertCollected<T>(WeakReference<T> reference)
        where T : class => Assert.False(IsAlive(reference), "The native retirement root retained its former owner.");

    private static void AssertRetained<T>(WeakReference<T> reference)
        where T : class => Assert.True(IsAlive(reference), "The retirement manager lost a failed native layout.");

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool IsAlive<T>(WeakReference<T> reference)
        where T : class => reference.TryGetTarget(out _);

    private static void CollectGarbage()
    {
        for (int iteration = 0; iteration < 3; iteration++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
    }

    private sealed record ImageEvidence(
        ImageLayoutDisposalProbe Disposer,
        WeakReference<ImageBox> Image,
        WeakReference<MarkdownLayoutContext> Context,
        bool PlaceholderRetirementRecordWasReserved,
        bool CaptionCandidateWasPublished);

    private sealed record TerminalFailureEvidence(
        InvalidOperationException Failure,
        WeakReference<ImageBox> Image,
        WeakReference<MarkdownLayoutContext> Context);

    private sealed record DisposedCheckpointEvidence(
        InvalidOperationException Failure,
        WeakReference<ImageBox> Image,
        WeakReference<MarkdownLayoutContext> Context);

    private sealed record NativeLayoutPair(
        CanvasTextLayout? Placeholder,
        NativeResourceRetirementRecord? PlaceholderRecord,
        CanvasTextLayout Caption,
        NativeResourceRetirementRecord CaptionRecord)
    {
        internal static NativeLayoutPair Capture(ImageBox image)
        {
            CanvasTextLayout? placeholder = image.MeasureCustody.GetLayoutState().Placeholder;
            NativeResourceRetirementRecord? placeholderRecord = image.MeasureCustody.GetLayoutState().PlaceholderRecord;
            CanvasTextLayout caption = Assert.IsType<CanvasTextLayout>(image.MeasureCustody.GetLayoutState().Caption);
            NativeResourceRetirementRecord captionRecord = Assert.IsType<NativeResourceRetirementRecord>(
                image.MeasureCustody.GetLayoutState().CaptionRecord);
            return new NativeLayoutPair(placeholder, placeholderRecord, caption, captionRecord);
        }

        internal void AssertFencedBy(NativeLayoutPlacementFence fence)
        {
            Assert.Same(Placeholder, fence.Placeholder);
            Assert.Same(PlaceholderRecord, fence.PlaceholderRetirementRecord);
            Assert.Same(Caption, fence.Caption);
            Assert.Same(CaptionRecord, fence.CaptionRetirementRecord);
        }
    }
}
