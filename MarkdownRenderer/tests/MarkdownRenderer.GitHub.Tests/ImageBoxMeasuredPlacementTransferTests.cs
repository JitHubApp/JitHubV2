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

            SharedCanvasBitmapCache.Lease sourceLease = GetField<SharedCanvasBitmapCache.Lease>(source, "_bitmapLease");
            SharedCanvasBitmapCache.Lease preparedLease = GetField<SharedCanvasBitmapCache.Lease>(prepared, "_bitmapLease");
            CanvasBitmap sourceBitmap = Assert.IsType<CanvasBitmap>(source.Bitmap);
            CanvasTextLayout sourceCaption = GetField<CanvasTextLayout>(source, "_caption");
            CanvasTextLayout preparedCaption = GetField<CanvasTextLayout>(prepared, "_caption");
            Rect sourceBounds = source.Bounds;
            Rect preparedBounds = prepared.Bounds;
            CancellationTokenSource sourceImageLifetime = GetAssignableField<CancellationTokenSource>(
                source, "_imageLifetimeCancellation");
            CanvasTextLayout? sourcePlaceholder = GetNullableField<CanvasTextLayout>(source, "_placeholder");
            CanvasTextLayout? preparedPlaceholder = GetNullableField<CanvasTextLayout>(prepared, "_placeholder");

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
            Assert.Same(sourceLease, GetField<SharedCanvasBitmapCache.Lease>(source, "_bitmapLease"));
            Assert.Same(preparedLease, GetField<SharedCanvasBitmapCache.Lease>(prepared, "_bitmapLease"));
            Assert.Same(sourceImageLifetime, GetField<CancellationTokenSource>(source, "_imageLifetimeCancellation"));
            Assert.Same(sourceBitmap, source.Bitmap);
            Assert.Same(sourceBitmap, prepared.Bitmap);
            Assert.Equal(preparedBounds, source.Bounds);
            Assert.Equal(sourceBounds, prepared.Bounds);
            Assert.Same(preparedCaption, GetField<CanvasTextLayout>(source, "_caption"));
            Assert.Same(sourceCaption, GetField<CanvasTextLayout>(prepared, "_caption"));
            Assert.Same(preparedPlaceholder, GetNullableField<CanvasTextLayout>(source, "_placeholder"));
            Assert.Same(sourcePlaceholder, GetNullableField<CanvasTextLayout>(prepared, "_placeholder"));

            targetContext.DiscardConstructionBlock(prepared);
            await prepared.RetirementTask.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Null(prepared.Bitmap);
            Assert.Same(sourceBitmap, source.Bitmap);
            Assert.False(source.ImageLifetimeDisposedForTests);
        }
        finally
        {
            if (prepared is not null && !prepared.ImageLifetimeDisposedForTests)
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
            Assert.False(source.ImageLifetimeDisposedForTests);
        }
        finally
        {
            if (prepared is not null && !prepared.ImageLifetimeDisposedForTests)
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

            SetField(source, "_bitmapOutputWaiterEpoch", 11L);
            SetField(source, "_bitmapOutputRecoverySignaledEpoch", 7L);
            SetField(source, "_bitmapOutputVisible", 1);
            SetField(source, "_bitmapOutputReplacement", 1);
            SetField(source, "_bitmapOutputExistingTile", 1);
            Rect preparedBounds = prepared.Bounds;
            CanvasTextLayout preparedCaption = GetField<CanvasTextLayout>(prepared, "_caption");

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

            Assert.Equal(11L, GetField<long>(source, "_bitmapOutputWaiterEpoch"));
            Assert.Equal(7L, GetField<long>(source, "_bitmapOutputRecoverySignaledEpoch"));
            Assert.Equal(1, GetField<int>(source, "_bitmapOutputVisible"));
            Assert.Equal(1, GetField<int>(source, "_bitmapOutputReplacement"));
            Assert.Equal(1, GetField<int>(source, "_bitmapOutputExistingTile"));
            Assert.Equal(preparedBounds, source.Bounds);
            Assert.Same(preparedCaption, GetField<CanvasTextLayout>(source, "_caption"));
        }
        finally
        {
            if (prepared is not null && !prepared.ImageLifetimeDisposedForTests)
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
            CanvasTextLayout sourceCaption = GetField<CanvasTextLayout>(source, "_caption");
            CanvasTextLayout preparedCaption = GetField<CanvasTextLayout>(prepared, "_caption");
            var fakeOwner = CreateUnattachedBitmapOutputWaiterOwner(source);
            var liveStates = new (string Field, object? Value)[]
            {
                ("_bitmapOutputDeferred", 1),
                ("_bitmapOutputRetryOfferPending", 1),
                ("_bitmapOutputDemand", new MarkdownSvgRenderDemand(MarkdownSvgRenderPriority.Overscan)),
                ("_bitmapOutputWaiterOwner", fakeOwner),
            };

            foreach ((string field, object? value) in liveStates)
            {
                SetField(source, field, value);
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
                    Assert.Same(sourceCaption, GetField<CanvasTextLayout>(source, "_caption"));
                    Assert.Same(preparedCaption, GetField<CanvasTextLayout>(prepared, "_caption"));
                }
                finally
                {
                    if (field == "_bitmapOutputDeferred" || field == "_bitmapOutputRetryOfferPending")
                        SetField(source, field, 0);
                    else
                        SetField(source, field, null);
                }
            }
        }
        finally
        {
            if (prepared is not null && !prepared.ImageLifetimeDisposedForTests)
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
        SharedCanvasBitmapCache.Lease retainedLease = GetField<SharedCanvasBitmapCache.Lease>(
            sourceImage, "_bitmapLease");
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

            SetField(sourceImage, "_bitmapOutputDeferred", 1);
            try
            {
                Assert.False(sourceImage.IsCurrentCompletedRasterPlacement(retainedLease));
                Assert.False(sourceImage.TryCreateCompletedRasterPlacementClone(
                    candidate.Context,
                    out ImageBox? privateClone,
                    out SharedCanvasBitmapCache.Lease? privateLease,
                    out ImageBox.CompletedSvgPlacementMetadataFence? svgFence));
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
                Assert.Same(retainedLease, GetField<SharedCanvasBitmapCache.Lease>(
                    sourceImage, "_bitmapLease"));
            }
            finally
            {
                SetField(sourceImage, "_bitmapOutputDeferred", 0);
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

            SharedCanvasBitmapCache.Lease sourceLease1 = GetField<SharedCanvasBitmapCache.Lease>(source1, "_bitmapLease");
            SharedCanvasBitmapCache.Lease sourceLease2 = GetField<SharedCanvasBitmapCache.Lease>(source2, "_bitmapLease");
            SharedCanvasBitmapCache.Lease preparedLease1 = GetField<SharedCanvasBitmapCache.Lease>(prepared1, "_bitmapLease");
            SharedCanvasBitmapCache.Lease preparedLease2 = GetField<SharedCanvasBitmapCache.Lease>(prepared2, "_bitmapLease");
            CanvasTextLayout sourceCaption1 = GetField<CanvasTextLayout>(source1, "_caption");
            CanvasTextLayout sourceCaption2 = GetField<CanvasTextLayout>(source2, "_caption");
            CanvasTextLayout preparedCaption1 = GetField<CanvasTextLayout>(prepared1, "_caption");
            CanvasTextLayout preparedCaption2 = GetField<CanvasTextLayout>(prepared2, "_caption");
            Rect sourceBounds1 = source1.Bounds;
            Rect sourceBounds2 = source2.Bounds;
            Rect preparedBounds1 = prepared1.Bounds;
            Rect preparedBounds2 = prepared2.Bounds;

            Assert.True(snapshot1.TryBeginInteraction());
            Assert.True(snapshot2.TryBeginInteraction());
            try
            {
                Assert.True(ImageBox.TryTransferMeasuredPlacementsFromCompletedClones(
                    new ImageBox.MeasuredPlacementTransfer[]
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
            Assert.NotSame(sourceCaption1, GetField<CanvasTextLayout>(source1, "_caption"));
            Assert.NotSame(sourceCaption2, GetField<CanvasTextLayout>(source2, "_caption"));
            Assert.Same(preparedCaption1, GetField<CanvasTextLayout>(source1, "_caption"));
            Assert.Same(preparedCaption2, GetField<CanvasTextLayout>(source2, "_caption"));
            Assert.Same(sourceCaption1, GetField<CanvasTextLayout>(prepared1, "_caption"));
            Assert.Same(sourceCaption2, GetField<CanvasTextLayout>(prepared2, "_caption"));
            Assert.Same(sourceLease1, GetField<SharedCanvasBitmapCache.Lease>(source1, "_bitmapLease"));
            Assert.Same(sourceLease2, GetField<SharedCanvasBitmapCache.Lease>(source2, "_bitmapLease"));
            Assert.Same(preparedLease1, GetField<SharedCanvasBitmapCache.Lease>(prepared1, "_bitmapLease"));
            Assert.Same(preparedLease2, GetField<SharedCanvasBitmapCache.Lease>(prepared2, "_bitmapLease"));
        }
        finally
        {
            if (prepared1 is not null && !prepared1.ImageLifetimeDisposedForTests)
            {
                targetContext1.DiscardConstructionBlock(prepared1);
                await prepared1.RetirementTask.WaitAsync(TimeSpan.FromSeconds(10));
            }
            if (prepared2 is not null && !prepared2.ImageLifetimeDisposedForTests)
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

            staleLease = SharedCanvasBitmapCache.CreateUncachedLease(
                CreateBitmap(fixture.Device, 7, 5), 7, 5, new Size(7, 5));
            Rect sourceBounds1 = source1.Bounds;
            Rect sourceBounds2 = source2.Bounds;
            Rect preparedBounds1 = prepared1.Bounds;
            Rect preparedBounds2 = prepared2.Bounds;
            CanvasTextLayout sourceCaption1 = GetField<CanvasTextLayout>(source1, "_caption");
            CanvasTextLayout sourceCaption2 = GetField<CanvasTextLayout>(source2, "_caption");
            CanvasTextLayout preparedCaption1 = GetField<CanvasTextLayout>(prepared1, "_caption");
            CanvasTextLayout preparedCaption2 = GetField<CanvasTextLayout>(prepared2, "_caption");
            SharedCanvasBitmapCache.Lease sourceLease1 = GetField<SharedCanvasBitmapCache.Lease>(source1, "_bitmapLease");
            SharedCanvasBitmapCache.Lease sourceLease2 = GetField<SharedCanvasBitmapCache.Lease>(source2, "_bitmapLease");

            Assert.True(snapshot1.TryBeginInteraction());
            Assert.True(snapshot2.TryBeginInteraction());
            try
            {
                Assert.False(ImageBox.TryTransferMeasuredPlacementsFromCompletedClones(
                    new ImageBox.MeasuredPlacementTransfer[]
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
            Assert.Same(sourceCaption1, GetField<CanvasTextLayout>(source1, "_caption"));
            Assert.Same(sourceCaption2, GetField<CanvasTextLayout>(source2, "_caption"));
            Assert.Same(preparedCaption1, GetField<CanvasTextLayout>(prepared1, "_caption"));
            Assert.Same(preparedCaption2, GetField<CanvasTextLayout>(prepared2, "_caption"));
            Assert.Same(sourceLease1, GetField<SharedCanvasBitmapCache.Lease>(source1, "_bitmapLease"));
            Assert.Same(sourceLease2, GetField<SharedCanvasBitmapCache.Lease>(source2, "_bitmapLease"));
        }
        finally
        {
            staleLease?.Dispose();
            if (prepared1 is not null && !prepared1.ImageLifetimeDisposedForTests)
            {
                targetContext1.DiscardConstructionBlock(prepared1);
                await prepared1.RetirementTask.WaitAsync(TimeSpan.FromSeconds(10));
            }
            if (prepared2 is not null && !prepared2.ImageLifetimeDisposedForTests)
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

            CanvasTextLayout source1Caption = GetField<CanvasTextLayout>(source1, "_caption");
            originalSource2Caption = GetField<CanvasTextLayout>(source2, "_caption");
            Rect sourceBounds1 = source1.Bounds;
            Rect sourceBounds2 = source2.Bounds;
            Rect preparedBounds1 = prepared1.Bounds;
            Rect preparedBounds2 = prepared2.Bounds;
            CanvasTextLayout preparedCaption1 = GetField<CanvasTextLayout>(prepared1, "_caption");
            CanvasTextLayout preparedCaption2 = GetField<CanvasTextLayout>(prepared2, "_caption");

            // Simulate a malformed caller offering two pairs with a shared native
            // layout. The batch must reject before any pair is swapped.
            SetField(source2, "_caption", source1Caption);
            Assert.True(snapshot1.TryBeginInteraction());
            Assert.True(snapshot2.TryBeginInteraction());
            try
            {
                Assert.False(ImageBox.TryTransferMeasuredPlacementsFromCompletedClones(
                    new ImageBox.MeasuredPlacementTransfer[]
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
            Assert.Same(source1Caption, GetField<CanvasTextLayout>(source1, "_caption"));
            Assert.Same(source1Caption, GetField<CanvasTextLayout>(source2, "_caption"));
            Assert.Same(preparedCaption1, GetField<CanvasTextLayout>(prepared1, "_caption"));
            Assert.Same(preparedCaption2, GetField<CanvasTextLayout>(prepared2, "_caption"));
        }
        finally
        {
            if (originalSource2Caption is not null)
                SetField(source2, "_caption", originalSource2Caption);
            if (prepared1 is not null && !prepared1.ImageLifetimeDisposedForTests)
            {
                targetContext1.DiscardConstructionBlock(prepared1);
                await prepared1.RetirementTask.WaitAsync(TimeSpan.FromSeconds(10));
            }
            if (prepared2 is not null && !prepared2.ImageLifetimeDisposedForTests)
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

            CanvasTextLayout sourceCaption = GetField<CanvasTextLayout>(source, "_caption");
            CanvasTextLayout preparedCaption = GetField<CanvasTextLayout>(prepared, "_caption");
            Rect sourceBounds = source.Bounds;
            Rect preparedBounds = prepared.Bounds;
            CanvasBitmap preparedBitmap = Assert.IsType<CanvasBitmap>(prepared.Bitmap);
            CanvasBitmap replacementBitmap = CreateBitmap(fixture.Device, 24, 12);
            SharedCanvasBitmapCache.Lease replacementLease = SharedCanvasBitmapCache.CreateUncachedLease(
                replacementBitmap,
                24,
                12,
                new Size(24, 12));
            InvokeReplaceBitmap(source, replacementLease);
            _ = source.Measure(Width);
            source.Arrange(7, 9, Width);
            sourceCaption = GetField<CanvasTextLayout>(source, "_caption");
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
            Assert.Same(sourceCaption, GetField<CanvasTextLayout>(source, "_caption"));
            Assert.Same(preparedCaption, GetField<CanvasTextLayout>(prepared, "_caption"));
            Assert.Equal(sourceBounds, source.Bounds);
            Assert.Equal(preparedBounds, prepared.Bounds);
        }
        finally
        {
            if (prepared is not null && !prepared.ImageLifetimeDisposedForTests)
            {
                targetContext.DiscardConstructionBlock(prepared);
                await prepared.RetirementTask.WaitAsync(TimeSpan.FromSeconds(10));
            }
            await snapshot.Retire().WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    private static ImageBox CreateStandaloneImage(CompletedRasterPlacementCloneTests.PlacementFixture fixture)
    {
        var image = new ImageBox(fixture.SourceContext, fixture.RasterSource, "Standalone test image")
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
        MethodInfo method = typeof(ImageBox).GetMethod(
            "ReplaceBitmap",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        method.Invoke(image, new object?[] { lease.Bitmap, lease, null });
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

    private static T? GetNullableField<T>(object instance, string name) where T : class
    {
        FieldInfo field = instance.GetType().GetField(name,
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        return field.GetValue(instance) as T;
    }

    private static void SetField(object instance, string name, object? value)
    {
        FieldInfo field = instance.GetType().GetField(name,
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        field.SetValue(instance, value);
    }

    private static object CreateUnattachedBitmapOutputWaiterOwner(ImageBox image)
    {
        Type ownerType = typeof(ImageBox).GetNestedType("BitmapOutputWaiter", BindingFlags.NonPublic)!;
        ConstructorInfo constructor = ownerType.GetConstructor(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            types: [typeof(ImageBox), typeof(long)],
            modifiers: null)!;
        return constructor.Invoke([image, 17L]);
    }

    private static T GetAssignableField<T>(object instance, string name) where T : class
    {
        FieldInfo field = instance.GetType().GetField(name,
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        return Assert.IsAssignableFrom<T>(field.GetValue(instance));
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
        var source = new ImageBox(sourceContext, resolver.Url, "Text SVG");
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
                out ImageBox.CompletedSvgPlacementMetadataFence? svgFence));
            Assert.NotNull(prepared);
            Assert.NotNull(expectedLease);
            Assert.NotNull(svgFence);
            PrepareAtDifferentOrigin(source, prepared);
            snapshot = CreateSnapshot(source, sourceContext.SourceMap);

            SharedCanvasBitmapCache.Lease sourceLease = GetField<SharedCanvasBitmapCache.Lease>(source, "_bitmapLease");
            CanvasTextLayout sourceCaption = GetField<CanvasTextLayout>(source, "_caption");
            CanvasTextLayout preparedCaption = GetField<CanvasTextLayout>(prepared, "_caption");
            Rect sourceBounds = source.Bounds;
            Rect preparedBounds = prepared.Bounds;
            CanvasBitmap sourceBitmap = Assert.IsType<CanvasBitmap>(source.Bitmap);

            Assert.NotSame(sourceLease, GetField<SharedCanvasBitmapCache.Lease>(prepared, "_bitmapLease"));
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
            Assert.Same(preparedCaption, GetField<CanvasTextLayout>(source, "_caption"));
            Assert.Same(sourceCaption, GetField<CanvasTextLayout>(prepared, "_caption"));
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
        var source = new ImageBox(sourceContext, resolver.Url, "Text SVG");
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
                out ImageBox.CompletedSvgPlacementMetadataFence? svgFence));
            Assert.NotNull(prepared);
            Assert.NotNull(expectedLease);
            Assert.NotNull(svgFence);
            PrepareAtDifferentOrigin(source, prepared);
            snapshot = CreateSnapshot(source, sourceContext.SourceMap);

            CanvasTextLayout sourceCaption = GetField<CanvasTextLayout>(source, "_caption");
            CanvasTextLayout preparedCaption = GetField<CanvasTextLayout>(prepared, "_caption");
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
            Assert.Same(sourceCaption, GetField<CanvasTextLayout>(source, "_caption"));
            Assert.Same(preparedCaption, GetField<CanvasTextLayout>(prepared, "_caption"));
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
                () => image.ActiveImageWorkCountForTests == 0);
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
        WeakReference<byte[]> weak = GetField<WeakReference<byte[]>>(image, "_svgSourceIdentity");
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
