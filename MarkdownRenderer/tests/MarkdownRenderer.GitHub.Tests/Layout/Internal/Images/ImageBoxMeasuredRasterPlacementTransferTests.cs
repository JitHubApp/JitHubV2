using System.Threading;
using MarkdownRenderer.Document;
using MarkdownRenderer.Extensions;
using MarkdownRenderer.GitHub.Tests.Layout.Internal.Images.BitmapCache;
using MarkdownRenderer.GitHub.Tests.Layout.Internal.Inlines.Placement;
using MarkdownRenderer.Images.Svg;
using MarkdownRenderer.Layout.Internal.Boxes;
using MarkdownRenderer.Layout.Internal.Context;
using MarkdownRenderer.Layout.Internal.Images;
using MarkdownRenderer.Layout.Internal.Images.BitmapCache;
using MarkdownRenderer.Layout.Internal.Images.BitmapOutput;
using MarkdownRenderer.Layout.Internal.Images.Placement;
using MarkdownRenderer.Layout.Internal.PrivatePlacement;
using MarkdownRenderer.Layout.Internal.Snapshots;
using MarkdownRenderer.Theming;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Windows.Graphics.DirectX;
using Windows.UI;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests.Layout.Internal.Images;

/// <summary>Measured raster placement transfer from a completed private clone into its source image.</summary>
/// <remarks>
/// This file is longer than 500 lines because each transfer rule (lease identity, superseded build
/// tokens, bitmap-output history, live output ownership, replaced leases and batch validation) needs
/// its own completed-clone scenario and swap-or-reject oracle, and all of them share the private
/// image, snapshot and two-image batch helpers at the end of the class.
/// </remarks>
[Collection(SharedCanvasBitmapCacheTestCollection.Name)]
public sealed class ImageBoxMeasuredRasterPlacementTransferTests
{
    private const float Width = 320f;

    [Fact]
    public async Task TransferSwapsMeasuredNativePlacementButKeepsSourceImageAndLeases()
    {
        await using var fixture = new CompletedRasterPlacementCloneTests.PlacementFixture();
        ImageBox source = CreateStandaloneImage(fixture);
        LayoutSnapshot snapshot = CreateSnapshot(source, fixture.SourceContext.SourceMap);
        MarkdownLayoutContext targetContext = fixture.CreateContext();
        ImageBox? prepared = null;

        try
        {
            Assert.True(source.TryCreateCompletedRasterPlacementClone(
                targetContext, out prepared, out SharedCanvasBitmapCache.Lease? expectedLease));
            Assert.NotNull(prepared);
            Assert.NotNull(expectedLease);
            PrepareAtDifferentOrigin(source, prepared);

            SharedCanvasBitmapCache.Lease sourceLease = Assert.IsType<SharedCanvasBitmapCache.Lease>(source.Presentation.Lease);
            SharedCanvasBitmapCache.Lease preparedLease = Assert.IsType<SharedCanvasBitmapCache.Lease>(prepared.Presentation.Lease);
            CanvasBitmap sourceBitmap = Assert.IsType<CanvasBitmap>(source.Bitmap);
            CanvasTextLayout sourceCaption = Assert.IsType<CanvasTextLayout>(source.MeasureCustody.GetLayoutState().Caption);
            CanvasTextLayout preparedCaption = Assert.IsType<CanvasTextLayout>(prepared.MeasureCustody.GetLayoutState().Caption);
            Rect sourceBounds = source.Bounds;
            Rect preparedBounds = prepared.Bounds;
            CancellationTokenSource sourceImageLifetime = source.Lifetime.LifetimeCancellation;
            CanvasTextLayout? sourcePlaceholder = source.MeasureCustody.GetLayoutState().Placeholder;
            CanvasTextLayout? preparedPlaceholder = prepared.MeasureCustody.GetLayoutState().Placeholder;

            Assert.NotSame(sourceLease, preparedLease);
            Assert.Same(sourceBitmap, prepared.Bitmap);
            Assert.NotEqual(sourceBounds, preparedBounds);
            Assert.NotSame(sourceCaption, preparedCaption);

            Assert.True(snapshot.TryBeginInteraction());
            try
            {
                Assert.True(source.TryTransferMeasuredPlacementFromCompletedClone(
                    prepared,
                    expectedLease,
                    expectedSvgMetadataFence: null,
                    currentProviderCacheGeneration: 0,
                    expectedAvailableWidth: Width));
            }
            finally
            {
                snapshot.EndInteraction();
            }

            Assert.Same(source, Assert.IsType<ImageBox>(Assert.Single(snapshot.Blocks)));
            Assert.Same(sourceLease, source.Presentation.Lease);
            Assert.Same(preparedLease, prepared.Presentation.Lease);
            Assert.Same(sourceImageLifetime, source.Lifetime.LifetimeCancellation);
            Assert.Same(sourceBitmap, source.Bitmap);
            Assert.Same(sourceBitmap, prepared.Bitmap);
            Assert.Equal(preparedBounds, source.Bounds);
            Assert.Equal(sourceBounds, prepared.Bounds);
            Assert.Same(preparedCaption, Assert.IsType<CanvasTextLayout>(source.MeasureCustody.GetLayoutState().Caption));
            Assert.Same(sourceCaption, Assert.IsType<CanvasTextLayout>(prepared.MeasureCustody.GetLayoutState().Caption));
            Assert.Same(preparedPlaceholder, source.MeasureCustody.GetLayoutState().Placeholder);
            Assert.Same(sourcePlaceholder, prepared.MeasureCustody.GetLayoutState().Placeholder);

            targetContext.DiscardConstructionBlock(prepared);
            await prepared.RetirementTask.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Null(prepared.Bitmap);
            Assert.Same(sourceBitmap, source.Bitmap);
            Assert.False(source.GetWorkState().LifetimeDisposed);
        }
        finally
        {
            if (prepared is not null && !prepared.GetWorkState().LifetimeDisposed)
            {
                targetContext.DiscardConstructionBlock(prepared);
                await prepared.RetirementTask.WaitAsync(TimeSpan.FromSeconds(10));
            }
            await snapshot.Retire().WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public async Task TransferAcceptsCommittedImageAfterItsSupersededBuildTokenWasCanceled()
    {
        await using var fixture = new CompletedRasterPlacementCloneTests.PlacementFixture();
        ImageBox source = CreateStandaloneImage(fixture);
        LayoutSnapshot snapshot = CreateSnapshot(source, fixture.SourceContext.SourceMap);
        MarkdownLayoutContext targetContext = fixture.CreateContext();
        ImageBox? prepared = null;
        using var supersededBuild = new CancellationTokenSource();

        try
        {
            Assert.True(source.TryCreateCompletedRasterPlacementClone(
                targetContext, out prepared, out SharedCanvasBitmapCache.Lease? expectedLease));
            Assert.NotNull(prepared);
            Assert.NotNull(expectedLease);
            PrepareAtDifferentOrigin(source, prepared);

            fixture.SourceContext.SetCancellationToken(supersededBuild.Token);
            supersededBuild.Cancel();
            Assert.True(supersededBuild.IsCancellationRequested);

            Assert.True(snapshot.TryBeginInteraction());
            try
            {
                Assert.True(source.TryTransferMeasuredPlacementFromCompletedClone(
                    prepared,
                    expectedLease,
                    expectedSvgMetadataFence: null,
                    currentProviderCacheGeneration: 0,
                    expectedAvailableWidth: Width));
            }
            finally
            {
                snapshot.EndInteraction();
            }

            Assert.Same(source, Assert.IsType<ImageBox>(Assert.Single(snapshot.Blocks)));
            Assert.NotNull(source.Bitmap);
            Assert.False(source.GetWorkState().LifetimeDisposed);
        }
        finally
        {
            if (prepared is not null && !prepared.GetWorkState().LifetimeDisposed)
            {
                targetContext.DiscardConstructionBlock(prepared);
                await prepared.RetirementTask.WaitAsync(TimeSpan.FromSeconds(10));
            }
            await snapshot.Retire().WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public async Task TransferAcceptsAndPreservesInertBitmapOutputHistory()
    {
        await using var fixture = new CompletedRasterPlacementCloneTests.PlacementFixture();
        ImageBox source = CreateStandaloneImage(fixture);
        LayoutSnapshot snapshot = CreateSnapshot(source, fixture.SourceContext.SourceMap);
        MarkdownLayoutContext targetContext = fixture.CreateContext();
        ImageBox? prepared = null;

        try
        {
            Assert.True(source.TryCreateCompletedRasterPlacementClone(
                targetContext, out prepared, out SharedCanvasBitmapCache.Lease? expectedLease));
            Assert.NotNull(prepared);
            Assert.NotNull(expectedLease);
            PrepareAtDifferentOrigin(source, prepared);

            ImageBitmapOutputState history = RecordInertBitmapOutputHistory(source);
            Rect preparedBounds = prepared.Bounds;
            CanvasTextLayout preparedCaption = Assert.IsType<CanvasTextLayout>(prepared.MeasureCustody.GetLayoutState().Caption);

            Assert.True(snapshot.TryBeginInteraction());
            try
            {
                Assert.True(source.TryTransferMeasuredPlacementFromCompletedClone(
                    prepared,
                    expectedLease,
                    expectedSvgMetadataFence: null,
                    currentProviderCacheGeneration: 0,
                    expectedAvailableWidth: Width));
            }
            finally
            {
                snapshot.EndInteraction();
            }

            // The transfer neither rejects nor resets the inert history.
            Assert.Equal(history, source.OutputDeferral.GetState());
            Assert.Equal(preparedBounds, source.Bounds);
            Assert.Same(preparedCaption, Assert.IsType<CanvasTextLayout>(source.MeasureCustody.GetLayoutState().Caption));
        }
        finally
        {
            if (prepared is not null && !prepared.GetWorkState().LifetimeDisposed)
            {
                targetContext.DiscardConstructionBlock(prepared);
                await prepared.RetirementTask.WaitAsync(TimeSpan.FromSeconds(10));
            }
            await snapshot.Retire().WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public async Task TransferRejectsLiveBitmapOutputOwnershipWithoutChangingPlacement()
    {
        await using var fixture = new CompletedRasterPlacementCloneTests.PlacementFixture();
        ImageBox source = CreateStandaloneImage(fixture);
        LayoutSnapshot snapshot = CreateSnapshot(source, fixture.SourceContext.SourceMap);
        MarkdownLayoutContext targetContext = fixture.CreateContext();
        ImageBox? prepared = null;

        try
        {
            Assert.True(source.TryCreateCompletedRasterPlacementClone(
                targetContext, out prepared, out SharedCanvasBitmapCache.Lease? expectedLease));
            Assert.NotNull(prepared);
            Assert.NotNull(expectedLease);
            PrepareAtDifferentOrigin(source, prepared);

            Rect sourceBounds = source.Bounds;
            Rect preparedBounds = prepared.Bounds;
            CanvasTextLayout sourceCaption = Assert.IsType<CanvasTextLayout>(source.MeasureCustody.GetLayoutState().Caption);
            CanvasTextLayout preparedCaption = Assert.IsType<CanvasTextLayout>(prepared.MeasureCustody.GetLayoutState().Caption);

            foreach ((string name, Action<ImageBox> enter) in LiveBitmapOutputStates())
            {
                enter(source);
                try
                {
                    Assert.False(source.OutputDeferral.GetState().HasNoLiveWork, name);
                    Assert.False(TransferInsideInteraction(snapshot, source, prepared, expectedLease));

                    Assert.Equal(sourceBounds, source.Bounds);
                    Assert.Equal(preparedBounds, prepared.Bounds);
                    Assert.Same(sourceCaption, Assert.IsType<CanvasTextLayout>(source.MeasureCustody.GetLayoutState().Caption));
                    Assert.Same(preparedCaption, Assert.IsType<CanvasTextLayout>(prepared.MeasureCustody.GetLayoutState().Caption));
                }
                finally
                {
                    Assert.True(source.OutputDeferral.ClearBitmapOutputDeferral());
                }
            }

            // Once the live request is retired only inert history remains, and
            // the same transfer is accepted.
            Assert.True(source.OutputDeferral.GetState().HasNoLiveWork);
            Assert.True(TransferInsideInteraction(snapshot, source, prepared, expectedLease));
            Assert.Equal(preparedBounds, source.Bounds);
        }
        finally
        {
            if (prepared is not null && !prepared.GetWorkState().LifetimeDisposed)
            {
                targetContext.DiscardConstructionBlock(prepared);
                await prepared.RetirementTask.WaitAsync(TimeSpan.FromSeconds(10));
            }
            await snapshot.Retire().WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    // Each live state is entered through the deferral's own operations: a
    // recorded deferred request, and that request after its deferral flag
    // cleared while its demand, waiter owner and cancellations remain.
    // ImageBitmapOutputStateTests pins every individual live field of the
    // HasNoLiveWork gate the measured-placement transfer consults.
    private static (string Name, Action<ImageBox> Enter)[] LiveBitmapOutputStates() =>
    [
        ("recorded deferral", image =>
        {
            RecordLiveBitmapOutputDeferral(image);
            ImageBitmapOutputState live = image.OutputDeferral.GetState();
            Assert.True(live.Deferred && live.HasDemand && live.HasWaiterOwner);
        }),
        ("demand and waiter owner without deferral", image =>
        {
            RecordLiveBitmapOutputDeferral(image);
            Assert.True(image.OutputDeferral.ClearBitmapOutputDeferral(preserveRetryOffer: true));
            ImageBitmapOutputState live = image.OutputDeferral.GetState();
            Assert.False(live.Deferred);
            Assert.True(live.HasDemand && live.HasWaiterOwner);
        }),
    ];

    private static bool TransferInsideInteraction(
        LayoutSnapshot snapshot,
        ImageBox source,
        ImageBox prepared,
        SharedCanvasBitmapCache.Lease expectedLease)
    {
        Assert.True(snapshot.TryBeginInteraction());
        try
        {
            return source.TryTransferMeasuredPlacementFromCompletedClone(
                prepared,
                expectedLease,
                expectedSvgMetadataFence: null,
                currentProviderCacheGeneration: 0,
                expectedAvailableWidth: Width);
        }
        finally
        {
            snapshot.EndInteraction();
        }
    }

    [Fact]
    public async Task LiveDeferredRetainedBitmapRejectsCloneClassificationAndPublicationFence()
    {
        await using var fixture = new CompletedRasterPlacementCloneTests.PlacementFixture();
        var source = new LayoutSnapshot(
            new BlockBox[] { fixture.Source },
            fixture.SourceContext.SourceMap,
            Width,
            (float)fixture.Source.Bounds.Height);
        fixture.TransferSourceOwnershipToSnapshot(source);
        ImageBox sourceImage = fixture.ImageRun.Image;
        CanvasBitmap retainedBitmap = Assert.IsType<CanvasBitmap>(sourceImage.Bitmap);
        SharedCanvasBitmapCache.Lease retainedLease = Assert.IsType<SharedCanvasBitmapCache.Lease>(sourceImage.Presentation.Lease);
        SingleRasterLayoutCandidate? candidate = null;
        MarkdownLayoutContext? constructionContext = null;

        try
        {
            candidate = source.CreateSingleRasterCandidate(
                new[] { fixture.Source.BlockIndex },
                Width,
                CancellationToken.None,
                CancellationToken.None,
                fixture.ImagePreparationOwnerToken,
                retirementSink: null,
                imageUnavailable: null,
                out constructionContext);
            Assert.NotNull(candidate);
            Assert.True(candidate!.HasCurrentSourceRaster());

            RecordLiveBitmapOutputDeferral(sourceImage);
            try
            {
                Assert.True(sourceImage.OutputDeferral.IsBitmapOutputDeferred);
                Assert.False(sourceImage.IsCurrentCompletedRasterPlacement(retainedLease));
                Assert.False(sourceImage.TryCreateCompletedRasterPlacementClone(
                    candidate.Context,
                    out ImageBox? privateClone,
                    out SharedCanvasBitmapCache.Lease? privateLease,
                    out CompletedSvgPlacementMetadataFence? svgFence));
                Assert.Null(privateClone);
                Assert.Null(privateLease);
                Assert.Null(svgFence);

                Assert.False(sourceImage.TryClassifyPrivateImagePlacement(
                    candidate.Context,
                    allowIdleImagePlacements: true,
                    out bool isIdleDescriptor,
                    out int idleGeneration));
                Assert.False(isIdleDescriptor);
                Assert.Equal(0, idleGeneration);
                Assert.False(candidate.HasCurrentSourceRaster());

                // The rejected candidate must not disturb the retained preview or
                // its lease; only the live waiter/deferral state invalidates reuse.
                Assert.Same(retainedBitmap, sourceImage.Bitmap);
                Assert.Same(retainedLease, sourceImage.Presentation.Lease);
            }
            finally
            {
                Assert.True(sourceImage.OutputDeferral.ClearBitmapOutputDeferral());
            }
        }
        finally
        {
            candidate?.Dispose();
            if (constructionContext is not null)
                await constructionContext.DrainOwnedRetirementsAsync();
            await source.Retire().WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public async Task BatchTransferSwapsAllValidatedPlacementsTogether()
    {
        await using var fixture = new CompletedRasterPlacementCloneTests.PlacementFixture();
        var batch = new TwoImageBatch(fixture, CreateStandaloneImage(fixture), CreateStandaloneImage(fixture));

        try
        {
            Assert.True(batch.TryCreateClone1(out SharedCanvasBitmapCache.Lease? expectedLease1));
            Assert.True(batch.TryCreateClone2(out SharedCanvasBitmapCache.Lease? expectedLease2));
            Assert.NotNull(batch.Prepared1);
            ImageBox prepared1 = batch.Prepared1;
            Assert.NotNull(batch.Prepared2);
            ImageBox prepared2 = batch.Prepared2;
            Assert.NotNull(expectedLease1);
            Assert.NotNull(expectedLease2);
            batch.PrepareAtDifferentOrigins();

            SharedCanvasBitmapCache.Lease sourceLease1 = Assert.IsType<SharedCanvasBitmapCache.Lease>(batch.Source1.Presentation.Lease);
            SharedCanvasBitmapCache.Lease sourceLease2 = Assert.IsType<SharedCanvasBitmapCache.Lease>(batch.Source2.Presentation.Lease);
            SharedCanvasBitmapCache.Lease preparedLease1 = Assert.IsType<SharedCanvasBitmapCache.Lease>(prepared1.Presentation.Lease);
            SharedCanvasBitmapCache.Lease preparedLease2 = Assert.IsType<SharedCanvasBitmapCache.Lease>(prepared2.Presentation.Lease);
            PlacementState source1Before = PlacementState.Capture(batch.Source1);
            PlacementState source2Before = PlacementState.Capture(batch.Source2);
            PlacementState prepared1Before = PlacementState.Capture(prepared1);
            PlacementState prepared2Before = PlacementState.Capture(prepared2);

            Assert.True(batch.TryTransfer(
                new(batch.Source1, prepared1, expectedLease1, ExpectedSvgMetadataFence: null),
                new(batch.Source2, prepared2, expectedLease2, ExpectedSvgMetadataFence: null)));

            AssertPlacementsSwapped(source1Before, prepared1Before, batch.Source1, prepared1);
            AssertPlacementsSwapped(source2Before, prepared2Before, batch.Source2, prepared2);
            Assert.Same(sourceLease1, batch.Source1.Presentation.Lease);
            Assert.Same(sourceLease2, batch.Source2.Presentation.Lease);
            Assert.Same(preparedLease1, prepared1.Presentation.Lease);
            Assert.Same(preparedLease2, prepared2.Presentation.Lease);
        }
        finally
        {
            await batch.RetireAsync();
        }
    }

    [Fact]
    public async Task BatchTransferRejectsStaleLastPairWithoutSwappingEarlierPairs()
    {
        await using var fixture = new CompletedRasterPlacementCloneTests.PlacementFixture();
        var batch = new TwoImageBatch(fixture, CreateStandaloneImage(fixture), CreateStandaloneImage(fixture));
        SharedCanvasBitmapCache.Lease? staleLease = null;

        try
        {
            Assert.True(batch.TryCreateClone1(out SharedCanvasBitmapCache.Lease? expectedLease1));
            Assert.True(batch.TryCreateClone2(out _));
            Assert.NotNull(batch.Prepared1);
            ImageBox prepared1 = batch.Prepared1;
            Assert.NotNull(batch.Prepared2);
            ImageBox prepared2 = batch.Prepared2;
            Assert.NotNull(expectedLease1);
            batch.PrepareAtDifferentOrigins();

            staleLease = fixture.RenderResources.Images.BitmapCache.CreateUncachedLease(
                CreateBitmap(fixture.Device, 7, 5), 7, 5, new Size(7, 5));
            PlacementState source1Before = PlacementState.Capture(batch.Source1);
            PlacementState source2Before = PlacementState.Capture(batch.Source2);
            PlacementState prepared1Before = PlacementState.Capture(prepared1);
            PlacementState prepared2Before = PlacementState.Capture(prepared2);
            SharedCanvasBitmapCache.Lease sourceLease1 = Assert.IsType<SharedCanvasBitmapCache.Lease>(batch.Source1.Presentation.Lease);
            SharedCanvasBitmapCache.Lease sourceLease2 = Assert.IsType<SharedCanvasBitmapCache.Lease>(batch.Source2.Presentation.Lease);

            Assert.False(batch.TryTransfer(
                new(batch.Source1, prepared1, expectedLease1, ExpectedSvgMetadataFence: null),
                // The final pair has an unrelated live lease identity.
                // Validation must finish before the first swap occurs.
                new(batch.Source2, prepared2, staleLease, ExpectedSvgMetadataFence: null)));

            source1Before.AssertUnchanged(batch.Source1);
            source2Before.AssertUnchanged(batch.Source2);
            prepared1Before.AssertUnchanged(prepared1);
            prepared2Before.AssertUnchanged(prepared2);
            Assert.Same(sourceLease1, batch.Source1.Presentation.Lease);
            Assert.Same(sourceLease2, batch.Source2.Presentation.Lease);
        }
        finally
        {
            staleLease?.Dispose();
            await batch.RetireAsync();
        }
    }

    [Fact]
    public async Task BatchTransferRejectsCrossPairNativeLayoutAliasWithoutSwapping()
    {
        await using var fixture = new CompletedRasterPlacementCloneTests.PlacementFixture();
        var batch = new TwoImageBatch(fixture, CreateStandaloneImage(fixture), CreateStandaloneImage(fixture));
        CanvasTextLayout? originalSource2Caption = null;

        try
        {
            Assert.True(batch.TryCreateClone1(out SharedCanvasBitmapCache.Lease? expectedLease1));
            Assert.True(batch.TryCreateClone2(out SharedCanvasBitmapCache.Lease? expectedLease2));
            Assert.NotNull(batch.Prepared1);
            ImageBox prepared1 = batch.Prepared1;
            Assert.NotNull(batch.Prepared2);
            ImageBox prepared2 = batch.Prepared2;
            Assert.NotNull(expectedLease1);
            Assert.NotNull(expectedLease2);
            batch.PrepareAtDifferentOrigins();

            CanvasTextLayout source1Caption = Assert.IsType<CanvasTextLayout>(batch.Source1.MeasureCustody.GetLayoutState().Caption);
            originalSource2Caption = Assert.IsType<CanvasTextLayout>(batch.Source2.MeasureCustody.GetLayoutState().Caption);
            Rect sourceBounds1 = batch.Source1.Bounds;
            Rect sourceBounds2 = batch.Source2.Bounds;
            PlacementState prepared1Before = PlacementState.Capture(prepared1);
            PlacementState prepared2Before = PlacementState.Capture(prepared2);

            // Simulate a malformed caller offering two pairs with a shared native
            // layout. The batch must reject before any pair is swapped.
            batch.Source2.MeasureCustody.Caption = source1Caption;
            Assert.False(batch.TryTransfer(
                new(batch.Source1, prepared1, expectedLease1, ExpectedSvgMetadataFence: null),
                new(batch.Source2, prepared2, expectedLease2, ExpectedSvgMetadataFence: null)));

            Assert.Equal(sourceBounds1, batch.Source1.Bounds);
            Assert.Equal(sourceBounds2, batch.Source2.Bounds);
            prepared1Before.AssertUnchanged(prepared1);
            prepared2Before.AssertUnchanged(prepared2);
            Assert.Same(source1Caption, Assert.IsType<CanvasTextLayout>(batch.Source1.MeasureCustody.GetLayoutState().Caption));
            Assert.Same(source1Caption, Assert.IsType<CanvasTextLayout>(batch.Source2.MeasureCustody.GetLayoutState().Caption));
        }
        finally
        {
            if (originalSource2Caption is not null)
                batch.Source2.MeasureCustody.Caption = originalSource2Caption;
            await batch.RetireAsync();
        }
    }

    [Fact]
    public async Task TransferRejectsAReplacedSourceLeaseWithoutChangingEitherPlacement()
    {
        await using var fixture = new CompletedRasterPlacementCloneTests.PlacementFixture();
        ImageBox source = CreateStandaloneImage(fixture);
        LayoutSnapshot snapshot = CreateSnapshot(source, fixture.SourceContext.SourceMap);
        MarkdownLayoutContext targetContext = fixture.CreateContext();
        ImageBox? prepared = null;

        try
        {
            Assert.True(source.TryCreateCompletedRasterPlacementClone(
                targetContext, out prepared, out SharedCanvasBitmapCache.Lease? expectedLease));
            Assert.NotNull(prepared);
            Assert.NotNull(expectedLease);
            PrepareAtDifferentOrigin(source, prepared);

            CanvasTextLayout sourceCaption = Assert.IsType<CanvasTextLayout>(source.MeasureCustody.GetLayoutState().Caption);
            CanvasTextLayout preparedCaption = Assert.IsType<CanvasTextLayout>(prepared.MeasureCustody.GetLayoutState().Caption);
            Rect sourceBounds = source.Bounds;
            Rect preparedBounds = prepared.Bounds;
            CanvasBitmap preparedBitmap = Assert.IsType<CanvasBitmap>(prepared.Bitmap);
            CanvasBitmap replacementBitmap = CreateBitmap(fixture.Device, 24, 12);
            SharedCanvasBitmapCache.Lease replacementLease = fixture.RenderResources.Images.BitmapCache.CreateUncachedLease(
                replacementBitmap,
                24,
                12,
                new Size(24, 12));
            InvokeReplaceBitmap(source, replacementLease);
            _ = source.Measure(Width);
            source.Arrange(7, 9, Width);
            sourceCaption = Assert.IsType<CanvasTextLayout>(source.MeasureCustody.GetLayoutState().Caption);
            sourceBounds = source.Bounds;

            Assert.True(snapshot.TryBeginInteraction());
            try
            {
                Assert.False(source.TryTransferMeasuredPlacementFromCompletedClone(
                    prepared,
                    expectedLease,
                    expectedSvgMetadataFence: null,
                    currentProviderCacheGeneration: 0,
                    expectedAvailableWidth: Width));
            }
            finally
            {
                snapshot.EndInteraction();
            }

            Assert.Same(replacementBitmap, source.Bitmap);
            Assert.Same(preparedBitmap, prepared.Bitmap);
            Assert.Same(sourceCaption, Assert.IsType<CanvasTextLayout>(source.MeasureCustody.GetLayoutState().Caption));
            Assert.Same(preparedCaption, Assert.IsType<CanvasTextLayout>(prepared.MeasureCustody.GetLayoutState().Caption));
            Assert.Equal(sourceBounds, source.Bounds);
            Assert.Equal(preparedBounds, prepared.Bounds);
        }
        finally
        {
            if (prepared is not null && !prepared.GetWorkState().LifetimeDisposed)
            {
                targetContext.DiscardConstructionBlock(prepared);
                await prepared.RetirementTask.WaitAsync(TimeSpan.FromSeconds(10));
            }
            await snapshot.Retire().WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    private static void AssertPlacementsSwapped(
        PlacementState sourceBefore,
        PlacementState preparedBefore,
        ImageBox source,
        ImageBox prepared)
    {
        Assert.Equal(preparedBefore.Bounds, source.Bounds);
        Assert.Equal(sourceBefore.Bounds, prepared.Bounds);
        Assert.NotSame(sourceBefore.Caption, Assert.IsType<CanvasTextLayout>(source.MeasureCustody.GetLayoutState().Caption));
        Assert.Same(preparedBefore.Caption, Assert.IsType<CanvasTextLayout>(source.MeasureCustody.GetLayoutState().Caption));
        Assert.Same(sourceBefore.Caption, Assert.IsType<CanvasTextLayout>(prepared.MeasureCustody.GetLayoutState().Caption));
    }

    /// <summary>The measured placement of one image: its bounds and caption layout.</summary>
    private sealed record PlacementState(Rect Bounds, CanvasTextLayout Caption)
    {
        internal static PlacementState Capture(ImageBox image) =>
            new(image.Bounds, Assert.IsType<CanvasTextLayout>(image.MeasureCustody.GetLayoutState().Caption));

        internal void AssertUnchanged(ImageBox image)
        {
            Assert.Equal(Bounds, image.Bounds);
            Assert.Same(Caption, Assert.IsType<CanvasTextLayout>(image.MeasureCustody.GetLayoutState().Caption));
        }
    }

    /// <summary>
    /// Two standalone images in their own snapshots and target contexts, for the
    /// batch transfer tests. It owns the clones' discard and the snapshots' retirement.
    /// </summary>
    private sealed class TwoImageBatch
    {
        internal TwoImageBatch(CompletedRasterPlacementCloneTests.PlacementFixture fixture, ImageBox source1, ImageBox source2)
        {
            Source1 = source1;
            Source2 = source2;
            source2.BlockIndex = source1.BlockIndex + 1;
            Snapshot1 = CreateSnapshot(source1, fixture.SourceContext.SourceMap);
            Snapshot2 = CreateSnapshot(source2, fixture.SourceContext.SourceMap);
            TargetContext1 = fixture.CreateContext();
            TargetContext2 = fixture.CreateContext();
        }

        internal ImageBox Source1 { get; }
        internal ImageBox Source2 { get; }
        internal LayoutSnapshot Snapshot1 { get; }
        internal LayoutSnapshot Snapshot2 { get; }
        internal MarkdownLayoutContext TargetContext1 { get; }
        internal MarkdownLayoutContext TargetContext2 { get; }
        internal ImageBox? Prepared1 { get; private set; }
        internal ImageBox? Prepared2 { get; private set; }

        internal bool TryCreateClone1(out SharedCanvasBitmapCache.Lease? expectedLease)
        {
            bool created = Source1.TryCreateCompletedRasterPlacementClone(
                TargetContext1, out ImageBox? prepared, out expectedLease);
            Prepared1 = prepared;
            return created;
        }

        internal bool TryCreateClone2(out SharedCanvasBitmapCache.Lease? expectedLease)
        {
            bool created = Source2.TryCreateCompletedRasterPlacementClone(
                TargetContext2, out ImageBox? prepared, out expectedLease);
            Prepared2 = prepared;
            return created;
        }

        internal void PrepareAtDifferentOrigins()
        {
            PrepareAtDifferentOrigin(Source1, Prepared1!);
            PrepareAtDifferentOrigin(Source2, Prepared2!);
        }

        internal bool TryTransfer(params ImageMeasuredPlacementTransfer[] transfers)
        {
            Assert.True(Snapshot1.TryBeginInteraction());
            Assert.True(Snapshot2.TryBeginInteraction());
            try
            {
                return ImageBox.TryTransferMeasuredPlacementsFromCompletedClones(
                    transfers,
                    currentProviderCacheGeneration: 0,
                    expectedAvailableWidth: Width);
            }
            finally
            {
                Snapshot2.EndInteraction();
                Snapshot1.EndInteraction();
            }
        }

        internal async Task RetireAsync()
        {
            await DiscardPreparedAsync(TargetContext1, Prepared1);
            await DiscardPreparedAsync(TargetContext2, Prepared2);
            await TargetContext1.DrainOwnedRetirementsAsync();
            await TargetContext2.DrainOwnedRetirementsAsync();
            await Snapshot1.Retire().WaitAsync(TimeSpan.FromSeconds(10));
            await Snapshot2.Retire().WaitAsync(TimeSpan.FromSeconds(10));
        }

        private static async Task DiscardPreparedAsync(MarkdownLayoutContext targetContext, ImageBox? prepared)
        {
            if (prepared is not null && !prepared.GetWorkState().LifetimeDisposed)
            {
                targetContext.DiscardConstructionBlock(prepared);
                await prepared.RetirementTask.WaitAsync(TimeSpan.FromSeconds(10));
            }
        }
    }

    private ImageBox CreateStandaloneImage(CompletedRasterPlacementCloneTests.PlacementFixture fixture)
    {
        var image = new ImageBox(fixture.SourceContext, fixture.SourceContext.ImageResources, fixture.RasterSource, "Standalone test image")
        {
            BlockIndex = fixture.Source.BlockIndex,
        };
        SharedCanvasBitmapCache.Lease lease = Assert.IsType<SharedCanvasBitmapCache.Lease>(
            fixture.ImageRun.Image.AcquireBitmapLease());
        InvokeReplaceBitmap(image, lease);
        _ = image.Measure(Width);
        image.Arrange(0, 0, Width);
        return image;
    }

    private static LayoutSnapshot CreateSnapshot(ImageBox source, MarkdownSourceMap sourceMap) =>
        new(new BlockBox[] { source }, sourceMap, Width, (float)source.Bounds.Height);

    private static void PrepareAtDifferentOrigin(ImageBox source, ImageBox prepared)
    {
        _ = source.Measure(Width);
        source.Arrange(11, 17, Width);
        _ = prepared.Measure(Width);
        prepared.Arrange(29, 43, Width);
    }

    private static void InvokeReplaceBitmap(ImageBox image, SharedCanvasBitmapCache.Lease lease)
    {
        image.Presentation.Replace(lease.Bitmap, lease);
    }

    private static CanvasBitmap CreateBitmap(CanvasDevice device, int width, int height)
    {
        byte[] pixels = new byte[checked(width * height * 4)];
        return CanvasBitmap.CreateFromBytes(
            device,
            pixels,
            width,
            height,
            DirectXPixelFormat.B8G8R8A8UIntNormalized);
    }

    // Records a live deferred bitmap-output request for the image's current
    // load demand through the deferral's own entry point, starting that
    // demand first when the image was never loaded.
    private static void RecordLiveBitmapOutputDeferral(
        ImageBox image,
        bool isVisible = false,
        bool isReplacement = false,
        bool isExistingTile = false)
    {
        if (image.LoadState.Demand is null)
            _ = image.LoadState.BeginDemand(new MarkdownSvgRenderDemand(MarkdownSvgRenderPriority.Visible));
        BitmapOutputWorkContext work = image.OutputAdmission.CaptureBitmapOutputWorkContext(
            CancellationToken.None, isExistingSvgTileOutput: isExistingTile);
        Assert.True(image.OutputDeferral.RecordBitmapOutputDeferral(
            requiredBytes: 4096,
            image.Resources.BitmapOutput.GetSnapshot().CapacityVersion,
            work,
            isVisible,
            isReplacement,
            needsCacheTrim: false));
    }

    // Leaves only inert bitmap-output history behind, through real deferral
    // operations: a visible replacement tile request whose governor waiter
    // cannot register while the scope's waiter table is full (which signals
    // recovery for its epoch), then retired.
    private static ImageBitmapOutputState RecordInertBitmapOutputHistory(ImageBox image)
    {
        BitmapOutputAdmission governor = image.Resources.BitmapOutput;
        var fillers = new List<FullWaiterTableProbe>();
        var registrations = new List<BitmapOutputAdmission.WaiterRegistration>();
        try
        {
            for (int index = 0; index < 4096; index++)
            {
                var filler = new FullWaiterTableProbe();
                if (!governor.TryRegisterWaiter(filler, 1, isVisible: true, isReplacement: false,
                        governor.GetSnapshot().CapacityVersion,
                        out BitmapOutputAdmission.WaiterRegistration? registration))
                {
                    break;
                }
                fillers.Add(filler);
                registrations.Add(Assert.IsType<BitmapOutputAdmission.WaiterRegistration>(registration));
            }
            Assert.NotEmpty(registrations);

            RecordLiveBitmapOutputDeferral(image, isVisible: true, isReplacement: true, isExistingTile: true);
            long epoch = image.OutputDeferral.GetState().WaiterEpoch;
            image.OutputDeferral.PrepareDeferredBitmapOutputWaiterSafely(epoch);
            ImageBitmapOutputState failed = image.OutputDeferral.GetState();
            Assert.True(failed.RegistrationFailed);
            Assert.Equal(epoch, failed.RecoverySignaledEpoch);
        }
        finally
        {
            foreach (BitmapOutputAdmission.WaiterRegistration registration in registrations)
                registration.Dispose();
            GC.KeepAlive(fillers);
        }

        Assert.True(image.OutputDeferral.ClearBitmapOutputDeferral());
        ImageBitmapOutputState history = image.OutputDeferral.GetState();
        Assert.True(history.HasNoLiveWork);
        Assert.False(history.IsPristine);
        Assert.NotEqual(0, history.WaiterEpoch);
        Assert.NotEqual(0, history.RecoverySignaledEpoch);
        Assert.True(history.Visible && history.Replacement && history.ExistingTile);
        return history;
    }

    private sealed class FullWaiterTableProbe : IBitmapOutputWaiter
    {
        public void OnBitmapOutputCapacityAvailable(long capacityVersion)
        {
            // A filler only occupies a waiter slot; it never retries.
        }
    }

}
