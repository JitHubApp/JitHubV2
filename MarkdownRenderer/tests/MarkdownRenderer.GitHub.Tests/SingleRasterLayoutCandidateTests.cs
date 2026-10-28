using System.Reflection;
using MarkdownRenderer.Accessibility;
using MarkdownRenderer.Document;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Theming;
using Microsoft.Graphics.Canvas;
using Windows.Foundation;
using Windows.Graphics.DirectX;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

[Collection(SharedCanvasBitmapCacheTestCollection.Name)]
public sealed class SingleRasterLayoutCandidateTests
{
    private const float Width = 320f;

    [Fact]
    public async Task CandidatePreservesSemanticIdentityAndAdvancesOnlyLayoutRevision()
    {
        await using var fixture = new CompletedRasterPlacementCloneTests.PlacementFixture();
        using var source = CreateSourceSnapshot(fixture, automationGeneration: 41);
        MarkdownSourceMap sourceMap = source.SourceMap;
        DocumentRange textRange = new(
            new DocumentPosition(fixture.Source.BlockIndex, 0, 0),
            new DocumentPosition(fixture.Source.BlockIndex, 0, "caption ".Length));
        string mappedBefore = sourceMap.Slice(textRange);
        Assert.Equal("caption ", mappedBefore);
        MarkdownAutomationTextFingerprint sourceFingerprint = source.SourceFingerprint;
        MarkdownAutomationTextFingerprint textFingerprint =
            source.SemanticDocument.TextProjectionFingerprint;
        MarkdownAutomationTextFingerprint semanticFingerprint =
            source.SemanticDocument.SemanticRangeFingerprint;
        long sourceRevision = source.LayoutRevision;

        (SingleRasterLayoutCandidate candidate, MarkdownLayoutContext constructionContext) =
            CreateCandidate(source, fixture, fixture.Source.BlockIndex);
        using (candidate)
        {
            LayoutSnapshot replacement = candidate.Snapshot;
            Assert.NotSame(source, replacement);
            Assert.NotSame(source.SemanticDocument, replacement.SemanticDocument);
            Assert.Same(sourceMap, replacement.SourceMap);
            Assert.Equal(sourceFingerprint, replacement.SourceFingerprint);
            Assert.Equal(textFingerprint, replacement.SemanticDocument.TextProjectionFingerprint);
            Assert.Equal(semanticFingerprint, replacement.SemanticDocument.SemanticRangeFingerprint);
            Assert.Equal(mappedBefore, replacement.SourceMap.Slice(textRange));
            Assert.Equal(sourceRevision, candidate.SourceRevision);
            Assert.Equal(sourceRevision + 1, replacement.LayoutRevision);
            Assert.Equal(41, replacement.AutomationDocumentGeneration);
            Assert.Equal(41, source.AutomationDocumentGeneration);
            Assert.Equal(Width, candidate.Width);
            Assert.False(replacement.IsLazyLayoutEnabled);
            Assert.Equal(1, replacement.TopLevelBlockCount);
            Assert.Equal(1, replacement.MeasuredTopLevelBlockCount);
            Assert.True(candidate.HasCurrentSourceRaster());

            InlineContainerBox replacementRoot = Assert.IsType<InlineContainerBox>(
                Assert.Single(replacement.Blocks));
            InlineImageRun replacementImageRun = replacementRoot.Runs.OfType<InlineImageRun>().Single();
            Assert.NotSame(fixture.ImageRun.Image, replacementImageRun.Image);
            Assert.Same(fixture.ImageRun.Image.Bitmap, replacementImageRun.Image.Bitmap);
        }

        await constructionContext.DrainOwnedRetirementsAsync();
        Assert.Equal(mappedBefore, source.SourceMap.Slice(textRange));
        Assert.Equal(sourceRevision, source.LayoutRevision);
        Assert.Equal(41, source.AutomationDocumentGeneration);
    }

    [Fact]
    public async Task ExistingReadLeaseSurvivesCandidateBuildAndBlocksOriginalRetirement()
    {
        await using var fixture = new CompletedRasterPlacementCloneTests.PlacementFixture();
        using var source = CreateSourceSnapshot(fixture, automationGeneration: 73);
        InlineContainerBox sourceRoot = fixture.Source;
        CanvasBitmap originalBitmap = Assert.IsType<CanvasBitmap>(fixture.ImageRun.Image.Bitmap);
        Rect originalBounds = sourceRoot.Bounds;

        Assert.True(source.TryBeginInteraction());
        Task<(SingleRasterLayoutCandidate? Candidate, MarkdownLayoutContext? Context)> buildTask = Task.Run(() =>
        {
            SingleRasterLayoutCandidate? candidate = source.CreateSingleRasterCandidate(
                new[] { sourceRoot.BlockIndex },
                Width,
                CancellationToken.None,
                CancellationToken.None,
                fixture.ImagePreparationOwnerToken,
                retirementSink: null,
                imageUnavailable: null,
                out MarkdownLayoutContext? context);
            return (candidate, context);
        });

        try
        {
            Assert.False(buildTask.IsCompleted);
            Assert.Same(sourceRoot, Assert.Single(source.GetMeasuredTopLevelBlocks()));
            Assert.Equal(originalBounds, sourceRoot.Bounds);
            Assert.Same(originalBitmap, fixture.ImageRun.Image.Bitmap);
        }
        finally
        {
            source.EndInteraction();
        }

        (SingleRasterLayoutCandidate? builtCandidate, MarkdownLayoutContext? builtContext) =
            await buildTask.WaitAsync(TimeSpan.FromSeconds(10));
        SingleRasterLayoutCandidate candidateOwner = Assert.IsType<SingleRasterLayoutCandidate>(builtCandidate);
        MarkdownLayoutContext constructionContext = Assert.IsType<MarkdownLayoutContext>(builtContext);
        using (candidateOwner)
        {
            InlineContainerBox candidateRoot = Assert.IsType<InlineContainerBox>(
                Assert.Single(candidateOwner.Snapshot.Blocks));
            InlineImageRun candidateImageRun = candidateRoot.Runs.OfType<InlineImageRun>().Single();
            Assert.Same(originalBitmap, candidateImageRun.Image.Bitmap);

            Assert.True(source.TryBeginInteraction());
            Task originalRetirement;
            try
            {
                Assert.Same(sourceRoot, Assert.Single(source.GetMeasuredTopLevelBlocks()));
                Assert.True(sourceRoot.Bounds.Width > 0);
                originalRetirement = source.Retire();
                Assert.False(originalRetirement.IsCompleted);
                Assert.False(source.TryBeginInteraction());
                Assert.Equal(originalBounds, sourceRoot.Bounds);
                Assert.Same(originalBitmap, fixture.ImageRun.Image.Bitmap);
                Assert.Same(originalBitmap, candidateImageRun.Image.Bitmap);
            }
            finally
            {
                source.EndInteraction();
            }

            await originalRetirement.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Null(fixture.ImageRun.Image.Bitmap);
            Assert.True(fixture.ImageRun.Image.ImageLifetimeDisposedForTests);
            Assert.Same(originalBitmap, candidateImageRun.Image.Bitmap);
            Assert.False(candidateOwner.HasCurrentSourceRaster());

            candidateOwner.Dispose();
            await constructionContext.DrainOwnedRetirementsAsync();
            Assert.Null(candidateImageRun.Image.Bitmap);
            Assert.True(candidateImageRun.Image.ImageLifetimeDisposedForTests);
        }
    }

    [Fact]
    public async Task TransferredSnapshotOutlivesCandidateOwnerAndRetiresThroughConstructionDrain()
    {
        await using var fixture = new CompletedRasterPlacementCloneTests.PlacementFixture();
        using var source = CreateSourceSnapshot(fixture, automationGeneration: 101);
        (SingleRasterLayoutCandidate candidate, MarkdownLayoutContext constructionContext) =
            CreateCandidate(source, fixture, fixture.Source.BlockIndex);
        LayoutSnapshot transferred = candidate.TransferToControl();
        InlineImageRun transferredImageRun = Assert.IsType<InlineContainerBox>(
            Assert.Single(transferred.Blocks)).Runs.OfType<InlineImageRun>().Single();

        candidate.Dispose();
        Assert.Throws<ObjectDisposedException>(() => _ = candidate.Snapshot);
        Assert.NotNull(transferredImageRun.Image.Bitmap);
        Assert.Equal(101, transferred.AutomationDocumentGeneration);

        Task retirement = transferred.Retire();
        constructionContext.RegisterRetirement(retirement);
        await constructionContext.DrainOwnedRetirementsAsync();
        Assert.Null(transferredImageRun.Image.Bitmap);
        Assert.True(transferredImageRun.Image.ImageLifetimeDisposedForTests);
        Assert.True(transferred.IsRetiringOrDisposed);
    }

    [Fact]
    public async Task DiscardedCandidateDrainsItsPrivateImageRetirement()
    {
        await using var fixture = new CompletedRasterPlacementCloneTests.PlacementFixture();
        using var source = CreateSourceSnapshot(fixture, automationGeneration: 113);
        (SingleRasterLayoutCandidate candidate, MarkdownLayoutContext constructionContext) =
            CreateCandidate(source, fixture, fixture.Source.BlockIndex);
        InlineImageRun candidateImageRun = Assert.IsType<InlineContainerBox>(
            Assert.Single(candidate.Snapshot.Blocks)).Runs.OfType<InlineImageRun>().Single();

        candidate.Dispose();
        Assert.Throws<ObjectDisposedException>(() => _ = candidate.Snapshot);
        await constructionContext.DrainOwnedRetirementsAsync();
        Assert.Null(candidateImageRun.Image.Bitmap);
        Assert.True(candidateImageRun.Image.ImageLifetimeDisposedForTests);
        Assert.NotNull(fixture.ImageRun.Image.Bitmap);
        Assert.False(fixture.ImageRun.Image.ImageLifetimeDisposedForTests);
    }

    [Fact]
    public async Task ReplacedSourceRasterLeaseInvalidatesCandidatePublicationFence()
    {
        await using var fixture = new CompletedRasterPlacementCloneTests.PlacementFixture();
        using var source = CreateSourceSnapshot(fixture, automationGeneration: 119);
        (SingleRasterLayoutCandidate candidate, MarkdownLayoutContext constructionContext) =
            CreateCandidate(source, fixture, fixture.Source.BlockIndex);
        using (candidate)
        {
            InlineImageRun candidateImageRun = Assert.IsType<InlineContainerBox>(
                Assert.Single(candidate.Snapshot.Blocks)).Runs.OfType<InlineImageRun>().Single();
            CanvasBitmap previousBitmap = Assert.IsType<CanvasBitmap>(candidateImageRun.Image.Bitmap);
            Assert.True(candidate.HasCurrentSourceRaster());

            CanvasBitmap replacementBitmap = CanvasBitmap.CreateFromBytes(
                fixture.Device,
                new byte[40 * 20 * 4],
                40,
                20,
                DirectXPixelFormat.B8G8R8A8UIntNormalized);
            SharedCanvasBitmapCache.Lease replacementLease =
                SharedCanvasBitmapCache.CreateUncachedLease(replacementBitmap);
            MethodInfo replaceBitmap = typeof(ImageBox).GetMethod(
                "ReplaceBitmap",
                BindingFlags.Instance | BindingFlags.NonPublic)!;
            _ = replaceBitmap.Invoke(fixture.ImageRun.Image, [replacementBitmap, replacementLease, null]);

            Assert.False(candidate.HasCurrentSourceRaster());
            Assert.Same(replacementBitmap, fixture.ImageRun.Image.Bitmap);
            Assert.Same(previousBitmap, candidateImageRun.Image.Bitmap);
        }

        await constructionContext.DrainOwnedRetirementsAsync();
    }

    [Fact]
    public async Task CanceledImageGenerationAndLayoutPassDoNotReplaceOriginalSnapshot()
    {
        await using var fixture = new CompletedRasterPlacementCloneTests.PlacementFixture();
        using var source = CreateSourceSnapshot(fixture, automationGeneration: 127);
        CanvasBitmap originalBitmap = Assert.IsType<CanvasBitmap>(fixture.ImageRun.Image.Bitmap);
        using var imageLifetime = new CancellationTokenSource();
        imageLifetime.Cancel();

        SingleRasterLayoutCandidate? candidate = source.CreateSingleRasterCandidate(
            new[] { fixture.Source.BlockIndex },
            Width,
            CancellationToken.None,
            imageLifetime.Token,
            fixture.ImagePreparationOwnerToken,
            retirementSink: null,
            imageUnavailable: null,
            out MarkdownLayoutContext? canceledImageContext);
        Assert.Null(candidate);
        MarkdownLayoutContext canceledContext = Assert.IsType<MarkdownLayoutContext>(canceledImageContext);
        await canceledContext.DrainOwnedRetirementsAsync();

        using var canceledBuild = new CancellationTokenSource();
        canceledBuild.Cancel();
        MarkdownLayoutContext? canceledBuildContext = null;
        Assert.Throws<OperationCanceledException>(() => source.CreateSingleRasterCandidate(
            new[] { fixture.Source.BlockIndex },
            Width,
            canceledBuild.Token,
            CancellationToken.None,
            fixture.ImagePreparationOwnerToken,
            retirementSink: null,
            imageUnavailable: null,
            out canceledBuildContext));
        Assert.Null(canceledBuildContext);

        Assert.Same(fixture.Source, Assert.Single(source.Blocks));
        Assert.Equal(0, source.LayoutRevision);
        Assert.Equal(127, source.AutomationDocumentGeneration);
        Assert.Same(originalBitmap, fixture.ImageRun.Image.Bitmap);
        Assert.False(fixture.ImageRun.Image.ImageLifetimeDisposedForTests);
    }

    [Fact]
    public async Task CandidateRejectsWrongBlockWidthAndUnsupportedRootsButPreservesSupportedLazyLayout()
    {
        await using var fixture = new CompletedRasterPlacementCloneTests.PlacementFixture();
        using var source = CreateSourceSnapshot(fixture, automationGeneration: 149);

        SingleRasterLayoutCandidate? candidate = source.CreateSingleRasterCandidate(
            new[] { fixture.Source.BlockIndex + 1 },
            Width,
            CancellationToken.None,
            CancellationToken.None,
            fixture.ImagePreparationOwnerToken,
            retirementSink: null,
            imageUnavailable: null,
            out MarkdownLayoutContext? wrongBlockContext);
        Assert.Null(candidate);
        Assert.Null(wrongBlockContext);

        candidate = source.CreateSingleRasterCandidate(
            new[] { fixture.Source.BlockIndex },
            Width + 1,
            CancellationToken.None,
            CancellationToken.None,
            fixture.ImagePreparationOwnerToken,
            retirementSink: null,
            imageUnavailable: null,
            out MarkdownLayoutContext? wrongWidthContext);
        Assert.Null(candidate);
        Assert.Null(wrongWidthContext);

        source.EnableLazyLayout(Width, viewportTop: 0, viewportHeight: 200, overscan: 0,
            CancellationToken.None);
        candidate = source.CreateSingleRasterCandidate(
            new[] { fixture.Source.BlockIndex },
            Width,
            CancellationToken.None,
            CancellationToken.None,
            fixture.ImagePreparationOwnerToken,
            retirementSink: null,
            imageUnavailable: null,
            out MarkdownLayoutContext? lazyContext);
        Assert.NotNull(candidate);
        Assert.NotNull(lazyContext);
        Assert.True(candidate.Snapshot.IsLazyLayoutEnabled);
        Assert.Equal(source.MeasuredTopLevelBlockCount, candidate.Snapshot.MeasuredTopLevelBlockCount);
        candidate.Dispose();
        await lazyContext.DrainOwnedRetirementsAsync();

        await using var multiFixture = new CompletedRasterPlacementCloneTests.PlacementFixture();
        InlineContainerBox secondRoot = new(multiFixture.SourceContext, MarkdownElementKeys.Body)
        {
            BlockIndex = multiFixture.SourceContext.NextBlockIndex(),
        };
        // Multiple ordinary inline roots are supported. Keep this negative
        // case a genuinely unsupported second root, not the former root limit.
        var extraText = new OpaqueTestRun("second") { SourceSpan = new SourceSpan(0, 6) };
        secondRoot.Add(extraText);
        multiFixture.SourceContext.SourceMap.Add(
            secondRoot.BlockIndex,
            extraText.InlineIndex,
            extraText.RenderedLength,
            extraText.SourceSpan,
            extraText.Text);
        float secondHeight = secondRoot.Measure(Width);
        secondRoot.Arrange(0, 0, Width);
        using var multipleRootSnapshot = new LayoutSnapshot(
            new BlockBox[] { multiFixture.Source, secondRoot },
            multiFixture.SourceContext.SourceMap,
            Width,
            (float)multiFixture.Source.Bounds.Height + secondHeight);
        multiFixture.TransferSourceOwnershipToSnapshot(multipleRootSnapshot);
        candidate = multipleRootSnapshot.CreateSingleRasterCandidate(
            new[] { multiFixture.Source.BlockIndex },
            Width,
            CancellationToken.None,
            CancellationToken.None,
            multiFixture.ImagePreparationOwnerToken,
            retirementSink: null,
            imageUnavailable: null,
            out MarkdownLayoutContext? multipleRootContext);
        Assert.Null(candidate);
        Assert.NotNull(multipleRootContext);
        await multipleRootContext.DrainOwnedRetirementsAsync();
    }

    [Fact]
    public async Task UnsupportedInlineShapeDrainsConstructionContextWithoutMutatingSource()
    {
        await using var fixture = new CompletedRasterPlacementCloneTests.PlacementFixture(
            extraTextRun: true);
        // Built-in stateless text and inline code are supported. Unknown
        // extension-owned runs still have no safe private replay contract.
        fixture.Source.Add(new OpaqueTestRun(string.Empty));
        _ = fixture.Source.Measure(Width);
        fixture.Source.Arrange(0, 0, Width);
        using var source = CreateSourceSnapshot(fixture, automationGeneration: 157);
        MarkdownAutomationTextFingerprint semanticFingerprint =
            source.SemanticDocument.SemanticRangeFingerprint;
        CanvasBitmap sourceBitmap = Assert.IsType<CanvasBitmap>(fixture.ImageRun.Image.Bitmap);

        SingleRasterLayoutCandidate? candidate = source.CreateSingleRasterCandidate(
            new[] { fixture.Source.BlockIndex },
            Width,
            CancellationToken.None,
            CancellationToken.None,
            fixture.ImagePreparationOwnerToken,
            retirementSink: null,
            imageUnavailable: null,
            out MarkdownLayoutContext? constructionContext);
        Assert.Null(candidate);
        MarkdownLayoutContext failedContext = Assert.IsType<MarkdownLayoutContext>(constructionContext);
        await failedContext.DrainOwnedRetirementsAsync();

        Assert.Same(fixture.Source, Assert.Single(source.Blocks));
        Assert.Equal(0, source.LayoutRevision);
        Assert.Equal(157, source.AutomationDocumentGeneration);
        Assert.Equal(semanticFingerprint, source.SemanticDocument.SemanticRangeFingerprint);
        Assert.Same(sourceBitmap, fixture.ImageRun.Image.Bitmap);
        Assert.False(fixture.ImageRun.Image.ImageLifetimeDisposedForTests);
    }

    private sealed class OpaqueTestRun : InlineRun
    {
        private readonly string _text;
        internal OpaqueTestRun(string text)
        {
            _text = text;
            RenderedLength = text.Length;
        }
        public override string Text => _text;
    }

    private static LayoutSnapshot CreateSourceSnapshot(
        CompletedRasterPlacementCloneTests.PlacementFixture fixture,
        long automationGeneration)
    {
        var snapshot = new LayoutSnapshot(
            new BlockBox[] { fixture.Source },
            fixture.SourceContext.SourceMap,
            Width,
            (float)fixture.Source.Bounds.Height);
        fixture.TransferSourceOwnershipToSnapshot(snapshot);
        snapshot.AssignAutomationDocumentGeneration(automationGeneration);
        return snapshot;
    }

    private static (SingleRasterLayoutCandidate Candidate, MarkdownLayoutContext ConstructionContext)
        CreateCandidate(LayoutSnapshot source,
            CompletedRasterPlacementCloneTests.PlacementFixture fixture, int changedBlockIndex)
    {
        SingleRasterLayoutCandidate? candidate = source.CreateSingleRasterCandidate(
            new[] { changedBlockIndex },
            Width,
            CancellationToken.None,
            CancellationToken.None,
            fixture.ImagePreparationOwnerToken,
            retirementSink: null,
            imageUnavailable: null,
            out MarkdownLayoutContext? constructionContext);
        return (
            Assert.IsType<SingleRasterLayoutCandidate>(candidate),
            Assert.IsType<MarkdownLayoutContext>(constructionContext));
    }
}
