using System.Reflection;
using MarkdownRenderer.Document;
using MarkdownRenderer.Images;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Performance;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

[Collection(SharedCanvasBitmapCacheTestCollection.Name)]
public sealed class LayoutSnapshotReentrantGeometryReadTests
{
    private const float LayoutWidth = 320f;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ImageRelayoutAvoidsSuffixCallbacksOrFencesFallbackFailure(bool usePreparedRootBatch)
    {
        await using var seed = new CompletedRasterPlacementCloneTests.PlacementFixture();
        using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        var sourceMap = new MarkdownSourceMap(string.Empty);
        var resolver = new FixedRasterResolver(DisplaySizedRasterDecodeTests.CreatePng(96, 192));
        var context = new MarkdownLayoutContext(
            seed.Device,
            seed.SourceContext.ThemeSnapshot,
            sourceMap,
            seed.SourceContext.Registry,
            FlowDirection.LeftToRight,
            language: "en-US")
        {
            ImagePreparationOwner = seed.ImagePreparationOwnerToken,
            PerformanceDocumentOwner = seed.PerformanceDocumentOwner,
            PerformanceSession = session,
            ImageResolver = resolver,
            AllowThirdPartyRemoteImages = true,
            RasterizationScale = 1,
        };

        var first = new ImageBox(context, resolver.FirstUrl, "First completed image") { BlockIndex = 701 };
        var second = new ImageBox(context, resolver.SecondUrl, "Second completed image") { BlockIndex = 702 };
        var suffix = new ReentrantFailureBlock { BlockIndex = 703 };
        LayoutSnapshot? snapshot = null;
        MarkdownLayoutContext? constructionContext = null;

        try
        {
            first.Measure(LayoutWidth);
            first.Arrange(0, 0, LayoutWidth);
            Rect firstPlaceholder = first.Bounds;
            second.Measure(LayoutWidth);
            second.Arrange(0, (float)firstPlaceholder.Bottom, LayoutWidth);
            Rect secondPlaceholder = second.Bounds;

            // Load both rasters without applying their completed dimensions to
            // the committed roots. The following batch therefore has a real
            // multi-entry suffix to publish before the injected tail failure.
            await LoadTargetAsync(first);
            await LoadTargetAsync(second);
            Assert.Equal(firstPlaceholder, first.Bounds);
            Assert.Equal(secondPlaceholder, second.Bounds);

            float suffixHeight = suffix.Measure(LayoutWidth);
            suffix.Arrange(0, (float)secondPlaceholder.Bottom, LayoutWidth);
            snapshot = new LayoutSnapshot(
                new BlockBox[] { first, second, suffix },
                sourceMap,
                LayoutWidth,
                (float)(secondPlaceholder.Bottom + suffixHeight),
                blockSpacing: 0);
            LayoutSnapshot activeSnapshot = snapshot;

            Assert.False(activeSnapshot.GeometryRecoveryRequired);
            Assert.True(activeSnapshot.TryBeginPaint());
            activeSnapshot.EndPaint();
            Assert.True(activeSnapshot.TryBeginInteraction());
            activeSnapshot.EndInteraction();

            long revisionBefore = activeSnapshot.LayoutRevision;
            Size sizeBefore = activeSnapshot.Size;
            Rect suffixBefore = suffix.Bounds;
            var injected = new InvalidOperationException("Injected after the preceding roots were arranged.");
            int suffixArrangeCallbacks = 0;
            bool reentrantPaintAccepted = false;
            bool reentrantInteractionAccepted = false;
            suffix.DuringArrange = () =>
            {
                suffixArrangeCallbacks++;
                reentrantPaintAccepted = activeSnapshot.TryBeginPaint();
                if (reentrantPaintAccepted)
                    activeSnapshot.EndPaint();

                reentrantInteractionAccepted = activeSnapshot.TryBeginInteraction();
                if (reentrantInteractionAccepted)
                    activeSnapshot.EndInteraction();
            };
            suffix.FailAfterArrange = injected;

            PreparedImageRelayoutResult result = PreparedImageRelayoutResult.Unavailable;
            Exception? observed = Record.Exception(() =>
            {
                if (usePreparedRootBatch)
                {
                    result = activeSnapshot.TryRelayoutCompletedImageRoots(
                        [second.BlockIndex, first.BlockIndex],
                        LayoutWidth,
                        CancellationToken.None,
                        out constructionContext,
                        expectedPointerInputEpoch: activeSnapshot.PointerInputEpoch);
                }
                else
                {
                    activeSnapshot.RelayoutChangedBlocks(
                        [second.BlockIndex, first.BlockIndex], LayoutWidth, CancellationToken.None);
                }
            });

            if (usePreparedRootBatch)
            {
                // The optimized publisher moves an immutable placement index,
                // not every retained suffix box. An arbitrary suffix Arrange
                // callback must therefore never run on this commit path.
                Assert.Null(observed);
                Assert.Equal(PreparedImageRelayoutResult.Committed, result);
                Assert.Equal(0, suffixArrangeCallbacks);
                Assert.Equal(suffixBefore, suffix.Bounds);
                Assert.Equal(revisionBefore + 1, activeSnapshot.LayoutRevision);
                Assert.True(activeSnapshot.Size.Height > sizeBefore.Height);
                Assert.False(activeSnapshot.GeometryRecoveryRequired);
                Assert.False(activeSnapshot.TryClaimGeometryRecoveryRequest());
                Assert.True(activeSnapshot.TryCaptureScrollAnchorTarget(suffix.BlockIndex, 0, out double suffixTop));
                Assert.Equal((double)(float)(first.Bounds.Height + second.Bounds.Height), suffixTop);
                Assert.True(activeSnapshot.TryBeginPaint());
                activeSnapshot.EndPaint();
                Assert.True(activeSnapshot.TryBeginInteraction());
                activeSnapshot.EndInteraction();
                return;
            }

            Assert.Same(injected, observed);
            Assert.Equal(1, suffixArrangeCallbacks);
            Assert.False(reentrantPaintAccepted,
                "A same-thread Arrange callback obtained a paint lease during suffix-index mutation.");
            Assert.False(reentrantInteractionAccepted,
                "A same-thread Arrange callback obtained an interaction lease during suffix-index mutation.");
            Assert.True(activeSnapshot.GeometryRecoveryRequired);
            Assert.Equal(revisionBefore, activeSnapshot.LayoutRevision);
            Assert.Equal(sizeBefore, activeSnapshot.Size);
            Assert.NotEqual(firstPlaceholder.Height, first.Bounds.Height);
            Assert.NotEqual(secondPlaceholder.Height, second.Bounds.Height);
            AssertReadLeasesDenied(activeSnapshot);
            Assert.True(activeSnapshot.TryClaimGeometryRecoveryRequest());
            Assert.False(activeSnapshot.TryClaimGeometryRecoveryRequest());
        }
        finally
        {
            if (snapshot is not null)
            {
                await snapshot.Retire().WaitAsync(TimeSpan.FromSeconds(10));
            }
            else
            {
                context.DiscardConstructionBlock(first);
                context.DiscardConstructionBlock(second);
                suffix.Dispose();
                await Task.WhenAll(first.RetirementTask, second.RetirementTask)
                    .WaitAsync(TimeSpan.FromSeconds(10));
            }

            await context.DrainOwnedRetirementsAsync().WaitAsync(TimeSpan.FromSeconds(10));
            if (constructionContext is not null)
                await constructionContext.DrainOwnedRetirementsAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    private static async Task LoadTargetAsync(ImageBox image)
    {
        await DisplaySizedRasterDecodeTests.WaitForLoadAsync(image).WaitAsync(TimeSpan.FromSeconds(10));
        await DisplaySizedRasterDecodeTests.WaitUntilAsync(
            () => image.ActiveImageWorkCountForTests == 0);
        Assert.NotNull(image.Bitmap);
    }

    private static void AssertReadLeasesDenied(LayoutSnapshot snapshot)
    {
        bool paint = snapshot.TryBeginPaint();
        if (paint)
            snapshot.EndPaint();
        Assert.False(paint);

        bool interaction = snapshot.TryBeginInteraction();
        if (interaction)
            snapshot.EndInteraction();
        Assert.False(interaction);
    }

    private sealed class ReentrantFailureBlock : BlockBox
    {
        internal Action? DuringArrange { get; set; }
        internal Exception? FailAfterArrange { get; set; }

        public override float Measure(float availableWidth)
        {
            Bounds = new Rect(0, 0, availableWidth, 18);
            return 18;
        }

        public override void Arrange(float x, float y, float width)
        {
            base.Arrange(x, y, width);
            DuringArrange?.Invoke();
            if (FailAfterArrange is { } failure)
                throw failure;
        }

        public override void Paint(CanvasDrawingSession drawingSession, Rect viewport) { }
    }

    private sealed class FixedRasterResolver(byte[] png) : IMarkdownImageResolver
    {
        private readonly string _cachePrefix = "geometry-reentry:" + Guid.NewGuid().ToString("N") + ":";

        internal string FirstUrl { get; } = "https://images.example.test/first-" + Guid.NewGuid().ToString("N") + ".png";
        internal string SecondUrl { get; } = "https://images.example.test/second-" + Guid.NewGuid().ToString("N") + ".png";

        public ValueTask<MarkdownImageResolution> ResolveAsync(
            string source,
            MarkdownImageResolveContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!string.Equals(source, FirstUrl, StringComparison.Ordinal) &&
                !string.Equals(source, SecondUrl, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("The test resolver received an unexpected image source.");
            }

            return ValueTask.FromResult(MarkdownImageResolution.Resolved(new MarkdownImageAsset(
                png,
                "image/png",
                CacheKey: _cachePrefix + source)));
        }
    }
}
