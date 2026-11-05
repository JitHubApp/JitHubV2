using System.Buffers;
using System.Reflection;
using System.Threading;
using MarkdownRenderer.Document;
using MarkdownRenderer.Extensions;
using MarkdownRenderer.Images;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Parsing;
using MarkdownRenderer.Theming;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Windows.Graphics.DirectX;
using Windows.UI;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

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

            SetField(source.OutputDeferral, "_bitmapOutputWaiterEpoch", 11L);
            SetField(source.OutputDeferral, "_bitmapOutputRecoverySignaledEpoch", 7L);
            SetField(source.OutputDeferral, "_bitmapOutputVisible", 1);
            SetField(source.OutputDeferral, "_bitmapOutputReplacement", 1);
            SetField(source.OutputDeferral, "_bitmapOutputExistingTile", 1);
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

            ImageBitmapOutputState history = source.OutputDeferral.GetState();
            Assert.Equal(11L, history.WaiterEpoch);
            Assert.Equal(7L, history.RecoverySignaledEpoch);
            Assert.True(history.Visible && history.Replacement && history.ExistingTile);
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
            var fakeOwner = new ImageBitmapOutputWaiter(source.OutputDeferral, 17L);
            var liveStates = new (string Field, object? Value)[]
            {
                ("_bitmapOutputDeferred", 1),
                ("_bitmapOutputRetryOfferPending", 1),
                ("_bitmapOutputDemand", new MarkdownSvgRenderDemand(MarkdownSvgRenderPriority.Overscan)),
                ("_bitmapOutputWaiterOwner", fakeOwner),
            };

            foreach ((string field, object? value) in liveStates)
            {
                SetField(source.OutputDeferral, field, value);
                try
                {
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

                    Assert.Equal(sourceBounds, source.Bounds);
                    Assert.Equal(preparedBounds, prepared.Bounds);
                    Assert.Same(sourceCaption, Assert.IsType<CanvasTextLayout>(source.MeasureCustody.GetLayoutState().Caption));
                    Assert.Same(preparedCaption, Assert.IsType<CanvasTextLayout>(prepared.MeasureCustody.GetLayoutState().Caption));
                }
                finally
                {
                    if (field == "_bitmapOutputDeferred" || field == "_bitmapOutputRetryOfferPending")
                        SetField(source.OutputDeferral, field, 0);
                    else
                        SetField(source.OutputDeferral, field, null);
                }
            }
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

            SetField(sourceImage.OutputDeferral, "_bitmapOutputDeferred", 1);
            try
            {
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
                SetField(sourceImage.OutputDeferral, "_bitmapOutputDeferred", 0);
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
        ImageBox source1 = CreateStandaloneImage(fixture);
        ImageBox source2 = CreateStandaloneImage(fixture);
        source2.BlockIndex = source1.BlockIndex + 1;
        LayoutSnapshot snapshot1 = CreateSnapshot(source1, fixture.SourceContext.SourceMap);
        LayoutSnapshot snapshot2 = CreateSnapshot(source2, fixture.SourceContext.SourceMap);
        MarkdownLayoutContext targetContext1 = fixture.CreateContext();
        MarkdownLayoutContext targetContext2 = fixture.CreateContext();
        ImageBox? prepared1 = null;
        ImageBox? prepared2 = null;

        try
        {
            Assert.True(source1.TryCreateCompletedRasterPlacementClone(
                targetContext1, out prepared1, out SharedCanvasBitmapCache.Lease? expectedLease1));
            Assert.True(source2.TryCreateCompletedRasterPlacementClone(
                targetContext2, out prepared2, out SharedCanvasBitmapCache.Lease? expectedLease2));
            Assert.NotNull(prepared1);
            Assert.NotNull(prepared2);
            Assert.NotNull(expectedLease1);
            Assert.NotNull(expectedLease2);
            PrepareAtDifferentOrigin(source1, prepared1);
            PrepareAtDifferentOrigin(source2, prepared2);

            SharedCanvasBitmapCache.Lease sourceLease1 = Assert.IsType<SharedCanvasBitmapCache.Lease>(source1.Presentation.Lease);
            SharedCanvasBitmapCache.Lease sourceLease2 = Assert.IsType<SharedCanvasBitmapCache.Lease>(source2.Presentation.Lease);
            SharedCanvasBitmapCache.Lease preparedLease1 = Assert.IsType<SharedCanvasBitmapCache.Lease>(prepared1.Presentation.Lease);
            SharedCanvasBitmapCache.Lease preparedLease2 = Assert.IsType<SharedCanvasBitmapCache.Lease>(prepared2.Presentation.Lease);
            CanvasTextLayout sourceCaption1 = Assert.IsType<CanvasTextLayout>(source1.MeasureCustody.GetLayoutState().Caption);
            CanvasTextLayout sourceCaption2 = Assert.IsType<CanvasTextLayout>(source2.MeasureCustody.GetLayoutState().Caption);
            CanvasTextLayout preparedCaption1 = Assert.IsType<CanvasTextLayout>(prepared1.MeasureCustody.GetLayoutState().Caption);
            CanvasTextLayout preparedCaption2 = Assert.IsType<CanvasTextLayout>(prepared2.MeasureCustody.GetLayoutState().Caption);
            Rect sourceBounds1 = source1.Bounds;
            Rect sourceBounds2 = source2.Bounds;
            Rect preparedBounds1 = prepared1.Bounds;
            Rect preparedBounds2 = prepared2.Bounds;

            Assert.True(snapshot1.TryBeginInteraction());
            Assert.True(snapshot2.TryBeginInteraction());
            try
            {
                Assert.True(ImageBox.TryTransferMeasuredPlacementsFromCompletedClones(
                    new ImageMeasuredPlacementTransfer[]
                    {
                        new(source1, prepared1, expectedLease1, ExpectedSvgMetadataFence: null),
                        new(source2, prepared2, expectedLease2, ExpectedSvgMetadataFence: null),
                    },
                    currentProviderCacheGeneration: 0,
                    expectedAvailableWidth: Width));
            }
            finally
            {
                snapshot2.EndInteraction();
                snapshot1.EndInteraction();
            }

            Assert.Equal(preparedBounds1, source1.Bounds);
            Assert.Equal(preparedBounds2, source2.Bounds);
            Assert.Equal(sourceBounds1, prepared1.Bounds);
            Assert.Equal(sourceBounds2, prepared2.Bounds);
            Assert.NotSame(sourceCaption1, Assert.IsType<CanvasTextLayout>(source1.MeasureCustody.GetLayoutState().Caption));
            Assert.NotSame(sourceCaption2, Assert.IsType<CanvasTextLayout>(source2.MeasureCustody.GetLayoutState().Caption));
            Assert.Same(preparedCaption1, Assert.IsType<CanvasTextLayout>(source1.MeasureCustody.GetLayoutState().Caption));
            Assert.Same(preparedCaption2, Assert.IsType<CanvasTextLayout>(source2.MeasureCustody.GetLayoutState().Caption));
            Assert.Same(sourceCaption1, Assert.IsType<CanvasTextLayout>(prepared1.MeasureCustody.GetLayoutState().Caption));
            Assert.Same(sourceCaption2, Assert.IsType<CanvasTextLayout>(prepared2.MeasureCustody.GetLayoutState().Caption));
            Assert.Same(sourceLease1, source1.Presentation.Lease);
            Assert.Same(sourceLease2, source2.Presentation.Lease);
            Assert.Same(preparedLease1, prepared1.Presentation.Lease);
            Assert.Same(preparedLease2, prepared2.Presentation.Lease);
        }
        finally
        {
            if (prepared1 is not null && !prepared1.GetWorkState().LifetimeDisposed)
            {
                targetContext1.DiscardConstructionBlock(prepared1);
                await prepared1.RetirementTask.WaitAsync(TimeSpan.FromSeconds(10));
            }
            if (prepared2 is not null && !prepared2.GetWorkState().LifetimeDisposed)
            {
                targetContext2.DiscardConstructionBlock(prepared2);
                await prepared2.RetirementTask.WaitAsync(TimeSpan.FromSeconds(10));
            }
            await targetContext1.DrainOwnedRetirementsAsync();
            await targetContext2.DrainOwnedRetirementsAsync();
            await snapshot1.Retire().WaitAsync(TimeSpan.FromSeconds(10));
            await snapshot2.Retire().WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public async Task BatchTransferRejectsStaleLastPairWithoutSwappingEarlierPairs()
    {
        await using var fixture = new CompletedRasterPlacementCloneTests.PlacementFixture();
        ImageBox source1 = CreateStandaloneImage(fixture);
        ImageBox source2 = CreateStandaloneImage(fixture);
        source2.BlockIndex = source1.BlockIndex + 1;
        LayoutSnapshot snapshot1 = CreateSnapshot(source1, fixture.SourceContext.SourceMap);
        LayoutSnapshot snapshot2 = CreateSnapshot(source2, fixture.SourceContext.SourceMap);
        MarkdownLayoutContext targetContext1 = fixture.CreateContext();
        MarkdownLayoutContext targetContext2 = fixture.CreateContext();
        ImageBox? prepared1 = null;
        ImageBox? prepared2 = null;
        SharedCanvasBitmapCache.Lease? staleLease = null;

        try
        {
            Assert.True(source1.TryCreateCompletedRasterPlacementClone(
                targetContext1, out prepared1, out SharedCanvasBitmapCache.Lease? expectedLease1));
            Assert.True(source2.TryCreateCompletedRasterPlacementClone(
                targetContext2, out prepared2, out _));
            Assert.NotNull(prepared1);
            Assert.NotNull(prepared2);
            Assert.NotNull(expectedLease1);
            PrepareAtDifferentOrigin(source1, prepared1);
            PrepareAtDifferentOrigin(source2, prepared2);

            staleLease = ImageResourceScope.Shared.BitmapCache.CreateUncachedLease(
                CreateBitmap(fixture.Device, 7, 5), 7, 5, new Size(7, 5));
            Rect sourceBounds1 = source1.Bounds;
            Rect sourceBounds2 = source2.Bounds;
            Rect preparedBounds1 = prepared1.Bounds;
            Rect preparedBounds2 = prepared2.Bounds;
            CanvasTextLayout sourceCaption1 = Assert.IsType<CanvasTextLayout>(source1.MeasureCustody.GetLayoutState().Caption);
            CanvasTextLayout sourceCaption2 = Assert.IsType<CanvasTextLayout>(source2.MeasureCustody.GetLayoutState().Caption);
            CanvasTextLayout preparedCaption1 = Assert.IsType<CanvasTextLayout>(prepared1.MeasureCustody.GetLayoutState().Caption);
            CanvasTextLayout preparedCaption2 = Assert.IsType<CanvasTextLayout>(prepared2.MeasureCustody.GetLayoutState().Caption);
            SharedCanvasBitmapCache.Lease sourceLease1 = Assert.IsType<SharedCanvasBitmapCache.Lease>(source1.Presentation.Lease);
            SharedCanvasBitmapCache.Lease sourceLease2 = Assert.IsType<SharedCanvasBitmapCache.Lease>(source2.Presentation.Lease);

            Assert.True(snapshot1.TryBeginInteraction());
            Assert.True(snapshot2.TryBeginInteraction());
            try
            {
                Assert.False(ImageBox.TryTransferMeasuredPlacementsFromCompletedClones(
                    new ImageMeasuredPlacementTransfer[]
                    {
                        new(source1, prepared1, expectedLease1, ExpectedSvgMetadataFence: null),
                        // The final pair has an unrelated live lease identity.
                        // Validation must finish before the first swap occurs.
                        new(source2, prepared2, staleLease, ExpectedSvgMetadataFence: null),
                    },
                    currentProviderCacheGeneration: 0,
                    expectedAvailableWidth: Width));
            }
            finally
            {
                snapshot2.EndInteraction();
                snapshot1.EndInteraction();
            }

            Assert.Equal(sourceBounds1, source1.Bounds);
            Assert.Equal(sourceBounds2, source2.Bounds);
            Assert.Equal(preparedBounds1, prepared1.Bounds);
            Assert.Equal(preparedBounds2, prepared2.Bounds);
            Assert.Same(sourceCaption1, Assert.IsType<CanvasTextLayout>(source1.MeasureCustody.GetLayoutState().Caption));
            Assert.Same(sourceCaption2, Assert.IsType<CanvasTextLayout>(source2.MeasureCustody.GetLayoutState().Caption));
            Assert.Same(preparedCaption1, Assert.IsType<CanvasTextLayout>(prepared1.MeasureCustody.GetLayoutState().Caption));
            Assert.Same(preparedCaption2, Assert.IsType<CanvasTextLayout>(prepared2.MeasureCustody.GetLayoutState().Caption));
            Assert.Same(sourceLease1, source1.Presentation.Lease);
            Assert.Same(sourceLease2, source2.Presentation.Lease);
        }
        finally
        {
            staleLease?.Dispose();
            if (prepared1 is not null && !prepared1.GetWorkState().LifetimeDisposed)
            {
                targetContext1.DiscardConstructionBlock(prepared1);
                await prepared1.RetirementTask.WaitAsync(TimeSpan.FromSeconds(10));
            }
            if (prepared2 is not null && !prepared2.GetWorkState().LifetimeDisposed)
            {
                targetContext2.DiscardConstructionBlock(prepared2);
                await prepared2.RetirementTask.WaitAsync(TimeSpan.FromSeconds(10));
            }
            await targetContext1.DrainOwnedRetirementsAsync();
            await targetContext2.DrainOwnedRetirementsAsync();
            await snapshot1.Retire().WaitAsync(TimeSpan.FromSeconds(10));
            await snapshot2.Retire().WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public async Task BatchTransferRejectsCrossPairNativeLayoutAliasWithoutSwapping()
    {
        await using var fixture = new CompletedRasterPlacementCloneTests.PlacementFixture();
        ImageBox source1 = CreateStandaloneImage(fixture);
        ImageBox source2 = CreateStandaloneImage(fixture);
        source2.BlockIndex = source1.BlockIndex + 1;
        LayoutSnapshot snapshot1 = CreateSnapshot(source1, fixture.SourceContext.SourceMap);
        LayoutSnapshot snapshot2 = CreateSnapshot(source2, fixture.SourceContext.SourceMap);
        MarkdownLayoutContext targetContext1 = fixture.CreateContext();
        MarkdownLayoutContext targetContext2 = fixture.CreateContext();
        ImageBox? prepared1 = null;
        ImageBox? prepared2 = null;
        CanvasTextLayout? originalSource2Caption = null;

        try
        {
            Assert.True(source1.TryCreateCompletedRasterPlacementClone(
                targetContext1, out prepared1, out SharedCanvasBitmapCache.Lease? expectedLease1));
            Assert.True(source2.TryCreateCompletedRasterPlacementClone(
                targetContext2, out prepared2, out SharedCanvasBitmapCache.Lease? expectedLease2));
            Assert.NotNull(prepared1);
            Assert.NotNull(prepared2);
            Assert.NotNull(expectedLease1);
            Assert.NotNull(expectedLease2);
            PrepareAtDifferentOrigin(source1, prepared1);
            PrepareAtDifferentOrigin(source2, prepared2);

            CanvasTextLayout source1Caption = Assert.IsType<CanvasTextLayout>(source1.MeasureCustody.GetLayoutState().Caption);
            originalSource2Caption = Assert.IsType<CanvasTextLayout>(source2.MeasureCustody.GetLayoutState().Caption);
            Rect sourceBounds1 = source1.Bounds;
            Rect sourceBounds2 = source2.Bounds;
            Rect preparedBounds1 = prepared1.Bounds;
            Rect preparedBounds2 = prepared2.Bounds;
            CanvasTextLayout preparedCaption1 = Assert.IsType<CanvasTextLayout>(prepared1.MeasureCustody.GetLayoutState().Caption);
            CanvasTextLayout preparedCaption2 = Assert.IsType<CanvasTextLayout>(prepared2.MeasureCustody.GetLayoutState().Caption);

            // Simulate a malformed caller offering two pairs with a shared native
            // layout. The batch must reject before any pair is swapped.
            source2.MeasureCustody.Caption = source1Caption;
            Assert.True(snapshot1.TryBeginInteraction());
            Assert.True(snapshot2.TryBeginInteraction());
            try
            {
                Assert.False(ImageBox.TryTransferMeasuredPlacementsFromCompletedClones(
                    new ImageMeasuredPlacementTransfer[]
                    {
                        new(source1, prepared1, expectedLease1, ExpectedSvgMetadataFence: null),
                        new(source2, prepared2, expectedLease2, ExpectedSvgMetadataFence: null),
                    },
                    currentProviderCacheGeneration: 0,
                    expectedAvailableWidth: Width));
            }
            finally
            {
                snapshot2.EndInteraction();
                snapshot1.EndInteraction();
            }

            Assert.Equal(sourceBounds1, source1.Bounds);
            Assert.Equal(sourceBounds2, source2.Bounds);
            Assert.Equal(preparedBounds1, prepared1.Bounds);
            Assert.Equal(preparedBounds2, prepared2.Bounds);
            Assert.Same(source1Caption, Assert.IsType<CanvasTextLayout>(source1.MeasureCustody.GetLayoutState().Caption));
            Assert.Same(source1Caption, Assert.IsType<CanvasTextLayout>(source2.MeasureCustody.GetLayoutState().Caption));
            Assert.Same(preparedCaption1, Assert.IsType<CanvasTextLayout>(prepared1.MeasureCustody.GetLayoutState().Caption));
            Assert.Same(preparedCaption2, Assert.IsType<CanvasTextLayout>(prepared2.MeasureCustody.GetLayoutState().Caption));
        }
        finally
        {
            if (originalSource2Caption is not null)
                source2.MeasureCustody.Caption = originalSource2Caption;
            if (prepared1 is not null && !prepared1.GetWorkState().LifetimeDisposed)
            {
                targetContext1.DiscardConstructionBlock(prepared1);
                await prepared1.RetirementTask.WaitAsync(TimeSpan.FromSeconds(10));
            }
            if (prepared2 is not null && !prepared2.GetWorkState().LifetimeDisposed)
            {
                targetContext2.DiscardConstructionBlock(prepared2);
                await prepared2.RetirementTask.WaitAsync(TimeSpan.FromSeconds(10));
            }
            await targetContext1.DrainOwnedRetirementsAsync();
            await targetContext2.DrainOwnedRetirementsAsync();
            await snapshot1.Retire().WaitAsync(TimeSpan.FromSeconds(10));
            await snapshot2.Retire().WaitAsync(TimeSpan.FromSeconds(10));
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
            SharedCanvasBitmapCache.Lease replacementLease = ImageResourceScope.Shared.BitmapCache.CreateUncachedLease(
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

    private static ImageBox CreateStandaloneImage(CompletedRasterPlacementCloneTests.PlacementFixture fixture)
    {
        var image = new ImageBox(fixture.SourceContext, ImageResourceScope.Shared, fixture.RasterSource, "Standalone test image")
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

    private static T GetField<T>(object instance, string name)
    {
        FieldInfo field = instance.GetType().GetField(name,
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        return Assert.IsType<T>(field.GetValue(instance));
    }

    private static void SetField(object instance, string name, object? value)
    {
        FieldInfo field = instance.GetType().GetField(name,
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        field.SetValue(instance, value);
    }

}

[Collection(ProcessRasterDecodeCollection.CollectionName)]
public sealed class ImageBoxMeasuredSvgPlacementTransferTests
{
    private const float Width = 320f;
    private const string SvgMarkup =
        "<svg xmlns='http://www.w3.org/2000/svg' width='16' height='8'><text x='0' y='7'>x</text></svg>";
    [Fact]
    public async Task TransferMovesFullSvgPlacementWithoutChangingSourceIdentityOrProviderState()
    {
        var renderer = new TransferSvgRenderer(hasText: true);
        var resolver = new TransferSvgResolver(System.Text.Encoding.UTF8.GetBytes(SvgMarkup));
        MarkdownLayoutContext sourceContext = CreateSvgContext(renderer, resolver);
        var source = new ImageBox(sourceContext, ImageResourceScope.Shared, resolver.Url, "Text SVG");
        MarkdownLayoutContext targetContext = sourceContext.CreateRasterPlacementContext(
            CancellationToken.None,
            sourceContext.ImagePreparationOwner!,
            retirementSink: null,
            imageUnavailable: null);
        ImageBox? prepared = null;
        LayoutSnapshot? snapshot = null;
        byte[] sourceIdentityBytes = Array.Empty<byte>();

        try
        {
            await LoadAndMeasureAsync(source);
            sourceIdentityBytes = RetainSvgSourceIdentity(source);
            Assert.NotNull(source.Bitmap);
            Assert.DoesNotContain(renderer.Requests, request => request.TileRegion is not null);

            Assert.True(source.TryCreateCompletedRasterPlacementClone(
                targetContext,
                out prepared,
                out SharedCanvasBitmapCache.Lease? expectedLease,
                out CompletedSvgPlacementMetadataFence? svgFence));
            Assert.NotNull(prepared);
            Assert.NotNull(expectedLease);
            Assert.NotNull(svgFence);
            PrepareAtDifferentOrigin(source, prepared);
            snapshot = CreateSnapshot(source, sourceContext.SourceMap);

            SharedCanvasBitmapCache.Lease sourceLease = Assert.IsType<SharedCanvasBitmapCache.Lease>(source.Presentation.Lease);
            CanvasTextLayout sourceCaption = Assert.IsType<CanvasTextLayout>(source.MeasureCustody.GetLayoutState().Caption);
            CanvasTextLayout preparedCaption = Assert.IsType<CanvasTextLayout>(prepared.MeasureCustody.GetLayoutState().Caption);
            Rect sourceBounds = source.Bounds;
            Rect preparedBounds = prepared.Bounds;
            CanvasBitmap sourceBitmap = Assert.IsType<CanvasBitmap>(source.Bitmap);

            Assert.NotSame(sourceLease, prepared.Presentation.Lease);
            Assert.True(snapshot.TryBeginInteraction());
            try
            {
                Assert.True(source.TryTransferMeasuredPlacementFromCompletedClone(
                    prepared,
                    expectedLease,
                    svgFence,
                    renderer.CacheGeneration,
                    Width));
            }
            finally
            {
                snapshot.EndInteraction();
            }

            Assert.Same(sourceBitmap, source.Bitmap);
            Assert.Same(sourceBitmap, prepared.Bitmap);
            Assert.Equal(preparedBounds, source.Bounds);
            Assert.Equal(sourceBounds, prepared.Bounds);
            Assert.Same(preparedCaption, Assert.IsType<CanvasTextLayout>(source.MeasureCustody.GetLayoutState().Caption));
            Assert.Same(sourceCaption, Assert.IsType<CanvasTextLayout>(prepared.MeasureCustody.GetLayoutState().Caption));
            Assert.Equal(1, renderer.OpenCount);
            Assert.Equal(1, renderer.RenderCount);
            GC.KeepAlive(sourceIdentityBytes);
        }
        finally
        {
            if (snapshot is not null)
                await snapshot.Retire().WaitAsync(TimeSpan.FromSeconds(10));
            else
            {
                source.Dispose();
                await source.RetirementTask.WaitAsync(TimeSpan.FromSeconds(10));
            }
            if (prepared is not null)
            {
                targetContext.DiscardConstructionBlock(prepared);
                await prepared.RetirementTask.WaitAsync(TimeSpan.FromSeconds(10));
            }
        }
    }

    [Fact]
    public async Task TransferRejectsTextSvgAfterProviderFontGenerationChanges()
    {
        var renderer = new TransferSvgRenderer(hasText: true);
        var resolver = new TransferSvgResolver(System.Text.Encoding.UTF8.GetBytes(SvgMarkup));
        MarkdownLayoutContext sourceContext = CreateSvgContext(renderer, resolver);
        var source = new ImageBox(sourceContext, ImageResourceScope.Shared, resolver.Url, "Text SVG");
        MarkdownLayoutContext targetContext = sourceContext.CreateRasterPlacementContext(
            CancellationToken.None,
            sourceContext.ImagePreparationOwner!,
            retirementSink: null,
            imageUnavailable: null);
        ImageBox? prepared = null;
        LayoutSnapshot? snapshot = null;
        byte[] sourceIdentityBytes = Array.Empty<byte>();

        try
        {
            await LoadAndMeasureAsync(source);
            sourceIdentityBytes = RetainSvgSourceIdentity(source);
            Assert.True(source.TryCreateCompletedRasterPlacementClone(
                targetContext,
                out prepared,
                out SharedCanvasBitmapCache.Lease? expectedLease,
                out CompletedSvgPlacementMetadataFence? svgFence));
            Assert.NotNull(prepared);
            Assert.NotNull(expectedLease);
            Assert.NotNull(svgFence);
            PrepareAtDifferentOrigin(source, prepared);
            snapshot = CreateSnapshot(source, sourceContext.SourceMap);

            CanvasTextLayout sourceCaption = Assert.IsType<CanvasTextLayout>(source.MeasureCustody.GetLayoutState().Caption);
            CanvasTextLayout preparedCaption = Assert.IsType<CanvasTextLayout>(prepared.MeasureCustody.GetLayoutState().Caption);
            Rect sourceBounds = source.Bounds;
            Rect preparedBounds = prepared.Bounds;
            CanvasBitmap sourceBitmap = Assert.IsType<CanvasBitmap>(source.Bitmap);
            renderer.AdvanceGeneration();

            Assert.True(snapshot.TryBeginInteraction());
            try
            {
                Assert.False(source.TryTransferMeasuredPlacementFromCompletedClone(
                    prepared,
                    expectedLease,
                    svgFence,
                    renderer.CacheGeneration,
                    Width));
            }
            finally
            {
                snapshot.EndInteraction();
            }

            Assert.Same(sourceBitmap, source.Bitmap);
            Assert.Same(sourceBitmap, prepared.Bitmap);
            Assert.Equal(sourceBounds, source.Bounds);
            Assert.Equal(preparedBounds, prepared.Bounds);
            Assert.Same(sourceCaption, Assert.IsType<CanvasTextLayout>(source.MeasureCustody.GetLayoutState().Caption));
            Assert.Same(preparedCaption, Assert.IsType<CanvasTextLayout>(prepared.MeasureCustody.GetLayoutState().Caption));
            GC.KeepAlive(sourceIdentityBytes);
        }
        finally
        {
            if (snapshot is not null)
                await snapshot.Retire().WaitAsync(TimeSpan.FromSeconds(10));
            else
            {
                source.Dispose();
                await source.RetirementTask.WaitAsync(TimeSpan.FromSeconds(10));
            }
            if (prepared is not null)
            {
                targetContext.DiscardConstructionBlock(prepared);
                await prepared.RetirementTask.WaitAsync(TimeSpan.FromSeconds(10));
            }
        }
    }

    private static MarkdownLayoutContext CreateSvgContext(
        TransferSvgRenderer renderer,
        TransferSvgResolver resolver)
    {
        var body = new ElementStyle { FontFamily = "Segoe UI", FontSize = 16f };
        var styles = new Dictionary<string, ElementStyle>(StringComparer.Ordinal)
        {
            [MarkdownElementKeys.Body] = body,
            [MarkdownElementKeys.ImageCaption] = body,
            [MarkdownElementKeys.ListMarker] = body,
        };
        Color transparent = Color.FromArgb(0, 0, 0, 0);
        var theme = new ThemeSnapshot(
            styles,
            new Dictionary<string, ElementStyleOverride>(),
            transparent,
            transparent,
            transparent,
            transparent,
            isDark: false,
            isHighContrast: false,
            textScaleFactor: 1f);
        var context = new MarkdownLayoutContext(
            CanvasDevice.GetSharedDevice(),
            theme,
            new MarkdownSourceMap(string.Empty),
            new MarkdownExtensionRegistry(),
            FlowDirection.LeftToRight,
            language: "en-US")
        {
            ImageResolver = resolver,
            SvgRenderer = renderer,
            ImagePreparationOwner = new object(),
            CancellationToken = CancellationToken.None,
            ImageCancellationToken = CancellationToken.None,
        };
        return context;
    }

    private static async Task LoadAndMeasureAsync(ImageBox image)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<LoadCompletedEventArgs> handler = (_, _) => completion.TrySetResult();
        image.LoadCompleted += handler;
        try
        {
            _ = image.Measure(Width);
            image.Arrange(5, 8, Width);
            image.EnsureLoading();
            await completion.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await DisplaySizedRasterDecodeTests.WaitUntilAsync(
                () => image.GetWorkState().ActiveWorkCount == 0);
            Assert.NotNull(image.Bitmap);
            _ = image.Measure(Width);
            image.Arrange(5, 8, Width);
        }
        finally
        {
            image.LoadCompleted -= handler;
        }
    }

    private static void PrepareAtDifferentOrigin(ImageBox source, ImageBox prepared)
    {
        _ = source.Measure(Width);
        source.Arrange(11, 17, Width);
        _ = prepared.Measure(Width);
        prepared.Arrange(29, 43, Width);
    }

    private static LayoutSnapshot CreateSnapshot(ImageBox source, MarkdownSourceMap sourceMap) =>
        new(new BlockBox[] { source }, sourceMap, Width, (float)source.Bounds.Height);

    private static byte[] RetainSvgSourceIdentity(ImageBox image)
    {
        WeakReference<byte[]> weak = Assert.IsType<WeakReference<byte[]>>(image.Svg.SourceIdentity);
        Assert.True(weak.TryGetTarget(out byte[]? source));
        return Assert.IsType<byte[]>(source);
    }

    private static T GetField<T>(object instance, string name)
    {
        FieldInfo field = instance.GetType().GetField(name,
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        return Assert.IsType<T>(field.GetValue(instance));
    }

    private sealed class TransferSvgRenderer(bool hasText) : IMarkdownSvgRenderer
    {
        private long _cacheGeneration;
        private readonly MarkdownSvgDocumentInfo _info = new(
            IntrinsicWidthDips: 16,
            IntrinsicHeightDips: 8,
            IntrinsicAspectRatio: 2,
            Description: "Transfer test SVG",
            HasText: hasText);

        internal int OpenCount { get; private set; }
        internal int RenderCount { get; private set; }
        internal List<MarkdownSvgRenderRequest> Requests { get; } = new();
        public long CacheGeneration => Interlocked.Read(ref _cacheGeneration);
        internal void AdvanceGeneration() => Interlocked.Increment(ref _cacheGeneration);

        public MarkdownSvgSourcePreparation PrepareSource(byte[] source, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return MarkdownSvgSourcePreparation.Admit(source);
        }

        public ValueTask<IMarkdownSvgDocument> OpenAsync(
            MarkdownSvgOpenRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            OpenCount++;
            return ValueTask.FromResult<IMarkdownSvgDocument>(new TransferSvgDocument(
                _info,
                Requests,
                () => RenderCount++));
        }
    }

    private sealed class TransferSvgResolver(byte[] bytes) : IMarkdownImageResolver
    {
        private readonly string _cacheKey = "placement-transfer-svg:" + Guid.NewGuid().ToString("N");
        internal string Url { get; } = "https://images.example/transfer-placement-" + Guid.NewGuid().ToString("N") + ".svg";

        public ValueTask<MarkdownImageResolution> ResolveAsync(
            string source,
            MarkdownImageResolveContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!string.Equals(source, Url, StringComparison.Ordinal))
                throw new InvalidOperationException("The transfer test resolver received an unexpected URL.");

            return ValueTask.FromResult(MarkdownImageResolution.Resolved(
                new MarkdownImageAsset(bytes, "image/svg+xml", CacheKey: _cacheKey)));
        }
    }

    private sealed class TransferSvgDocument(
        MarkdownSvgDocumentInfo info,
        List<MarkdownSvgRenderRequest> requests,
        Action rendered) : IMarkdownSvgDocument
    {
        public MarkdownSvgDocumentInfo Info { get; } = info;
        public void Dispose() { }

        public ValueTask<MarkdownSvgRaster> RenderAsync(
            MarkdownSvgRenderRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            requests.Add(request);
            rendered();
            int width = request.TileRegion?.Width ?? request.TargetWidthPixels;
            int height = request.TileRegion?.Height ?? request.TargetHeightPixels;
            int stride = checked(width * 4);
            int byteCount = checked(stride * height);
            IMemoryOwner<byte> owner = MemoryPool<byte>.Shared.Rent(byteCount);
            owner.Memory.Span[..byteCount].Clear();
            return ValueTask.FromResult(new MarkdownSvgRaster(
                owner,
                byteCount,
                width,
                height,
                stride,
                MarkdownSvgPixelFormat.Rgba8Premultiplied));
        }
    }
}
