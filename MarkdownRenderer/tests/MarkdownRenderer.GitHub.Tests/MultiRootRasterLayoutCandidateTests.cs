using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using MarkdownRenderer.Accessibility;
using MarkdownRenderer.Document;
using MarkdownRenderer.GitHub;
using MarkdownRenderer.Images;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Parsing;
using MarkdownRenderer.Performance;
using MarkdownRenderer.Theming;
using Markdig;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

[Collection(SharedCanvasBitmapCacheTestCollection.Name)]
public sealed class MultiRootRasterLayoutCandidateTests
{
    private sealed class PrivatePlacementRasterResolver(byte[] png) : IMarkdownImageResolver
    {
        private readonly string _cacheKey = "private-placement-raster:" + Guid.NewGuid().ToString("N");
        private int _resolveCount;
        internal int ResolveCount => Volatile.Read(ref _resolveCount);

        public ValueTask<MarkdownImageResolution> ResolveAsync(
            string source, MarkdownImageResolveContext context, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _resolveCount);
            return ValueTask.FromResult(MarkdownImageResolution.Resolved(
                new MarkdownImageAsset(png, "image/png", CacheKey: _cacheKey)));
        }
    }

    private const float Width = 320f;
    private static readonly Thickness DocumentPadding = new(9, 11, 13, 15);
    private const double BlockSpacing = 7.5;

    [Theory]
    [InlineData(1f, false)]
    [InlineData(1.5f, true)]
    [InlineData(2f, false)]
    [InlineData(4f, true)]
    public async Task DefaultProgressiveBuildLazyCandidateClonesMeasuredAndEstimatedTextRoots(
        float rasterizationScale, bool rightToLeft)
    {
        await using var seed = new CompletedRasterPlacementCloneTests.PlacementFixture();
        using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        var resolver = new PrivatePlacementRasterResolver(DisplaySizedRasterDecodeTests.CreatePng(400, 200));
        string sourceText = "![front alt](https://images.example/private-placement.png)\n\n" +
            "Measured `code`, **bold**, *emphasis*, [link](https://example.test/measured), and  \n" +
            "a hard line break.\n\n" +
            string.Join("\n\n", Enumerable.Range(0, 18).Select(index =>
                $"Tail {index}: `code-{index}`, **bold-{index}**, *emphasis-{index}*, " +
                $"[link-{index}](https://example.test/tail/{index}), and  \ncontinued-{index}."));
        MarkdownExtensionRegistry registry = new MarkdownExtensionRegistry().ConfigureGitHubReadmeRegistry();
        Markdig.Syntax.MarkdownDocument parsed = Markdown.Parse(sourceText, registry.BuildPipeline());
        MarkdownDocument parsedDocument = MarkdownDocument.FromParsed(sourceText, parsed);
        var sourceMap = new MarkdownSourceMap(sourceText);
        var documentOwner = new object();
        var sourceContext = new MarkdownLayoutContext(
            seed.Device,
            seed.SourceContext.ThemeSnapshot,
            sourceMap,
            registry,
            rightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight,
            language: "en-US")
        {
            CancellationToken = CancellationToken.None,
            ImageCancellationToken = CancellationToken.None,
            ImagePreparationOwner = seed.ImagePreparationOwnerToken,
            PerformanceDocumentOwner = documentOwner,
            PerformanceSession = session,
            RasterizationScale = rasterizationScale,
            ImageResolver = resolver,
            AllowThirdPartyRemoteImages = true,
        };

        LayoutSnapshot? source = null;
        SingleRasterLayoutCandidate? candidate = null;
        MarkdownLayoutContext? candidateContext = null;
        try
        {
            source = new LayoutBuilder(sourceContext, semanticDocument: parsedDocument).BuildLazy(
                parsed,
                Width,
                viewportTop: 0,
                viewportHeight: 120,
                overscan: 0,
                CancellationToken.None);
            source.AssignAutomationDocumentGeneration(2_407);

            Assert.True(source.IsLazyLayoutEnabled);
            Assert.True(MarkdownVectorSceneResidencyPolicy.CanOmitForPrivatePlacement(source.Blocks),
                string.Join("; ", source.Blocks.Select(block => block is InlineContainerBox inline
                    ? $"{inline.ElementKey}: index={inline.BlockIndex}, text={inline.PrivateRasterPlacementTextLength}, " +
                      $"runs={inline.Runs.Count}, html={inline.SafeHtmlInlineFlowScopeId}, " +
                      $"types={string.Join(',', inline.Runs.Select(run => run.GetType().Name))}"
                    : block.GetType().Name)));
            Assert.Null(source.VectorSceneResidencyPolicy);
            Assert.InRange(source.MeasuredTopLevelBlockCount, 1, source.TopLevelBlockCount - 1);
            Assert.Same(sourceMap, source.SourceMap);
            MarkdownSemanticDocument sourceSemantic = source.SemanticDocument;

            ImageBox sourceImage = Assert.IsType<ImageBox>(source.Blocks[0]);
            InlineContainerBox[] sourceRoots = source.Blocks.Skip(1)
                .Select(static block => Assert.IsType<InlineContainerBox>(block))
                .ToArray();
            Assert.Null(sourceImage.Bitmap);
            Assert.True(source.IsTopLevelBlockMeasured(sourceImage));
            Assert.True(sourceRoots.Sum(root => root.Runs.Count) <= InlineContainerBox.MaximumPrivateRasterPlacementRuns);
            Rect imageBoundsBeforeCompletion = sourceImage.Bounds;
            Rect sourceFarBounds = sourceRoots[^1].Bounds;
            await DisplaySizedRasterDecodeTests.WaitForLoadAsync(sourceImage);
            await DisplaySizedRasterDecodeTests.WaitUntilAsync(
                () => sourceImage.ActiveImageWorkCountForTests == 0);
            Assert.NotNull(sourceImage.Bitmap);
            Assert.Equal(1, resolver.ResolveCount);
            Assert.Equal(imageBoundsBeforeCompletion, sourceImage.Bounds);

            InlineContainerBox measuredTextRoot = sourceRoots[0];
            Assert.True(measuredTextRoot.HasMeasuredLayout);
            Assert.Contains(measuredTextRoot.Runs, static run => run is CodeInlineRun);
            Assert.Contains(measuredTextRoot.Runs, static run => run is StrongRun);
            Assert.Contains(measuredTextRoot.Runs, static run => run is EmphasisRun);
            Assert.Contains(measuredTextRoot.Runs, static run => run is LinkRun);
            Assert.Contains(measuredTextRoot.Runs, static run => run is LineBreakRun);

            InlineContainerBox sourceFarRoot = sourceRoots[^1];
            Assert.False(sourceFarRoot.HasMeasuredLayout);
            Assert.Contains(sourceFarRoot.Runs, static run => run is CodeInlineRun);
            Assert.Contains(sourceFarRoot.Runs, static run => run is StrongRun);
            Assert.Contains(sourceFarRoot.Runs, static run => run is EmphasisRun);
            Assert.Contains(sourceFarRoot.Runs, static run => run is LinkRun);
            Assert.Contains(sourceFarRoot.Runs, static run => run is LineBreakRun);

            long sourceRevision = source.LayoutRevision;
            int sourceMeasuredCount = source.MeasuredTopLevelBlockCount;
            candidate = source.CreateSingleRasterCandidate(
                [sourceImage.BlockIndex],
                Width,
                CancellationToken.None,
                CancellationToken.None,
                seed.ImagePreparationOwnerToken,
                retirementSink: null,
                imageUnavailable: null,
                out candidateContext);

            SingleRasterLayoutCandidate ownedCandidate = Assert.IsType<SingleRasterLayoutCandidate>(candidate);
            MarkdownLayoutContext ownedCandidateContext = Assert.IsType<MarkdownLayoutContext>(candidateContext);
            await ownedCandidate.DrainConstructionAsync().WaitAsync(TimeSpan.FromSeconds(10));
            LayoutSnapshot replacement = ownedCandidate.Snapshot;
            ImageBox replacementImage = Assert.IsType<ImageBox>(replacement.Blocks[0]);
            InlineContainerBox[] replacementRoots = replacement.Blocks.Skip(1)
                .Select(static block => Assert.IsType<InlineContainerBox>(block))
                .ToArray();

            Assert.True(replacement.IsLazyLayoutEnabled);
            Assert.Null(replacement.VectorSceneResidencyPolicy);
            Assert.Equal(source.TopLevelBlockCount, replacement.TopLevelBlockCount);
            Assert.Equal(sourceMeasuredCount, replacement.MeasuredTopLevelBlockCount);
            Assert.Equal(
                GetField<int>(source, "_generatedBlockIndexUpperBound"),
                GetField<int>(replacement, "_generatedBlockIndexUpperBound"));
            IReadOnlyDictionary<int, int> sourceOrdinalIndex = GetAssignableField<IReadOnlyDictionary<int, int>>(
                source,
                "_topLevelOrdinalByBlockIndex");
            IReadOnlyDictionary<int, int> replacementOrdinalIndex = GetAssignableField<IReadOnlyDictionary<int, int>>(
                replacement,
                "_topLevelOrdinalByBlockIndex");
            Assert.Equal(
                GetField<int>(GetAssignableField<BlockOrdinalIndex>(source, "_topLevelOrdinalByBlockIndex"),
                    "_denseGeneratedUpperBound"),
                GetField<int>(GetAssignableField<BlockOrdinalIndex>(replacement, "_topLevelOrdinalByBlockIndex"),
                    "_denseGeneratedUpperBound"));
            Assert.Equal(
                sourceOrdinalIndex.OrderBy(static pair => pair.Key),
                replacementOrdinalIndex.OrderBy(static pair => pair.Key));
            Assert.Equal(2_407, replacement.AutomationDocumentGeneration);
            Assert.Same(sourceMap, replacement.SourceMap);
            Assert.Equal(sourceSemantic.TextProjectionFingerprint, replacement.SemanticDocument.TextProjectionFingerprint);
            Assert.Equal(sourceSemantic.SemanticRangeFingerprint, replacement.SemanticDocument.SemanticRangeFingerprint);
            Assert.Same(sourceSemantic.Text, replacement.SemanticDocument.Text);
            Assert.Same(sourceSemantic.TextElementBoundaries, replacement.SemanticDocument.TextElementBoundaries);
            Assert.Equal(source.LayoutRevision + 1, replacement.LayoutRevision);
            Assert.NotSame(sourceImage, replacementImage);
            Assert.Same(sourceImage.Bitmap, replacementImage.Bitmap);
            Assert.NotEqual(sourceImage.Bounds.Height, replacementImage.Bounds.Height);
            Assert.Equal(sourceImage.Bounds.Width, replacementImage.Bounds.Width);
            Assert.Equal(replacementImage.MeasuredImageWidth, replacementImage.AutomationRenderDestination.Width);
            Assert.Equal(replacementImage.MeasuredImageHeight, replacementImage.AutomationRenderDestination.Height);
            Assert.True(replacementImage.Bounds.Height > replacementImage.AutomationRenderDestination.Height);
            Assert.Equal(sourceImage.Alt, replacementImage.Alt);
            Assert.Equal(sourceImage.BlockIndex, replacementImage.BlockIndex);
            Assert.Equal(sourceImage.Context.FlowDirection, replacementImage.Context.FlowDirection);
            Assert.Equal(rasterizationScale, replacementImage.Context.RasterizationScale);
            var imagePlan = new RasterPlacementImagePlan(replacement.Blocks, ownedCandidate.SourceImageCount);
            Assert.Same(replacementImage, Assert.Single(imagePlan.Images));
            Assert.True(ownedCandidate.MatchesSourceImages([sourceImage], new HashSet<ImageBox> { sourceImage }));
            Assert.All(sourceRoots.Zip(replacementRoots), pair => Assert.NotSame(pair.First, pair.Second));
            Assert.True(replacementRoots[0].HasMeasuredLayout);
            Assert.False(replacementRoots[^1].HasMeasuredLayout);
            Assert.Contains(replacementRoots[0].Runs, static run => run is CodeInlineRun);
            Assert.Contains(replacementRoots[0].Runs, static run => run is StrongRun);
            Assert.Contains(replacementRoots[0].Runs, static run => run is EmphasisRun);
            Assert.Contains(replacementRoots[0].Runs, static run => run is LinkRun);
            Assert.Contains(replacementRoots[0].Runs, static run => run is LineBreakRun);
            Assert.Contains(replacementRoots[^1].Runs, static run => run is CodeInlineRun);
            Assert.Contains(replacementRoots[^1].Runs, static run => run is LineBreakRun);

            using (var target = new CanvasRenderTarget(seed.Device, Width, 180, 96))
            using (CanvasDrawingSession drawing = target.CreateDrawingSession())
                source.Blocks[0].Paint(drawing, new Rect(0, 0, Width, 180));

            int replacementMeasuredCount = replacement.MeasuredTopLevelBlockCount;
            LazyLayoutCommit farCommit = replacement.EnsureMeasuredViewport(
                replacementRoots[^1].Bounds.Top,
                System.Math.Max(48, replacementRoots[^1].Bounds.Height),
                overscan: 0,
                CancellationToken.None);
            Assert.True(farCommit.Changed);
            Assert.True(replacementRoots[^1].HasMeasuredLayout);
            Assert.True(replacement.MeasuredTopLevelBlockCount > replacementMeasuredCount);

            // The candidate owns only its private measurements. The source's
            // far paragraph remains an estimated, unmeasured root.
            Assert.Equal(sourceRevision, source.LayoutRevision);
            Assert.Equal(sourceMeasuredCount, source.MeasuredTopLevelBlockCount);
            Assert.False(sourceFarRoot.HasMeasuredLayout);
            Assert.Equal(sourceFarBounds, sourceFarRoot.Bounds);
            Assert.Equal(imageBoundsBeforeCompletion, sourceImage.Bounds);
            Assert.Equal(1, resolver.ResolveCount);
            Assert.NotNull(sourceImage.Bitmap);
            Assert.NotNull(replacementImage.Bitmap);

            await source.Retire().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Null(sourceImage.Bitmap);
            Assert.NotNull(replacementImage.Bitmap);
            Assert.False(ownedCandidate.HasCurrentSourceRaster());
            using (var target = new CanvasRenderTarget(seed.Device, Width, 180, 96))
            using (CanvasDrawingSession drawing = target.CreateDrawingSession())
                replacementImage.Paint(drawing, new Rect(0, 0, Width, 180));

            _ = ownedCandidateContext;
        }
        finally
        {
            try
            {
                candidate?.Dispose();
            }
            finally
            {
                if (candidateContext is not null)
                    await candidateContext.DrainOwnedRetirementsAsync().WaitAsync(TimeSpan.FromSeconds(10));
            }

            try
            {
                if (source is not null)
                    await source.Retire().WaitAsync(TimeSpan.FromSeconds(10));
            }
            finally
            {
                await sourceContext.DrainOwnedRetirementsAsync().WaitAsync(TimeSpan.FromSeconds(10));
            }
        }
    }

    [Fact]
    public async Task CandidatePreservesMixedRootSemanticsRangesAndMeasuredSourceGeometry()
    {
        await using var seed = new MultiRasterPlacementCloneTests.MultiRasterFixture(imageCount: 3);
        await using var document = new MultiRootDocumentFixture(seed, CreateMixedRoots());
        LayoutSnapshot source = document.Snapshot;
        source.AssignAutomationDocumentGeneration(1_207);

        MarkdownSemanticDocument sourceSemantic = source.SemanticDocument;
        MarkdownSourceMap sourceMap = source.SourceMap;
        MarkdownAutomationTextFingerprint sourceFingerprint = source.SourceFingerprint;
        MarkdownAutomationTextFingerprint sourceTextFingerprint = sourceSemantic.TextProjectionFingerprint;
        MarkdownAutomationTextFingerprint sourceRangeFingerprint = sourceSemantic.SemanticRangeFingerprint;
        InlineContainerBox[] sourceRoots = document.Roots.ToArray();
        Rect[] sourceBounds = sourceRoots.Select(static root => root.Bounds).ToArray();
        CanvasTextLayout[] sourceLayouts = sourceRoots
            .Select(static root => GetField<CanvasTextLayout>(root, "_layout"))
            .ToArray();
        ImageBox[] sourceImages = document.Images.ToArray();
        CanvasBitmap[] sourceBitmaps = sourceImages
            .Select(static image => Assert.IsType<CanvasBitmap>(image.Bitmap))
            .ToArray();
        string[] sourceSlices = sourceRoots.Select(root => sourceMap.Slice(WholeBlockRange(root))).ToArray();
        int[] changedBlocks = document.ImageBearingBlockIndices;

        SingleRasterLayoutCandidate? candidate = null;
        MarkdownLayoutContext? constructionContext = null;
        try
        {
            candidate = source.CreateSingleRasterCandidate(
                changedBlocks,
                Width,
                CancellationToken.None,
                CancellationToken.None,
                document.ImagePreparationOwner,
                retirementSink: null,
                imageUnavailable: null,
                out constructionContext);
            SingleRasterLayoutCandidate owner = Assert.IsType<SingleRasterLayoutCandidate>(candidate);
            MarkdownLayoutContext buildContext = Assert.IsType<MarkdownLayoutContext>(constructionContext);
            LayoutSnapshot replacement = owner.Snapshot;
            InlineContainerBox[] replacementRoots = replacement.Blocks
                .Select(static block => Assert.IsType<InlineContainerBox>(block))
                .ToArray();
            Assert.Equal(sourceRoots.Length, replacementRoots.Length);
            Assert.Equal(sourceRoots.Length, replacement.TopLevelBlockCount);
            Assert.Equal(sourceRoots.Length, replacement.MeasuredTopLevelBlockCount);
            Assert.Equal(sourceRoots.Length, owner.Snapshot.Blocks.Count);
            Assert.Equal(3, owner.SourceImageCount);
            Assert.Same(sourceMap, replacement.SourceMap);
            Assert.Equal(sourceFingerprint, replacement.SourceFingerprint);
            Assert.Equal(sourceTextFingerprint, replacement.SemanticDocument.TextProjectionFingerprint);
            Assert.Equal(sourceRangeFingerprint, replacement.SemanticDocument.SemanticRangeFingerprint);
            Assert.Same(sourceSemantic.Text, replacement.SemanticDocument.Text);
            Assert.Same(sourceSemantic.TextElementBoundaries, replacement.SemanticDocument.TextElementBoundaries);
            Assert.Equal(1_207, replacement.AutomationDocumentGeneration);
            Assert.Equal(1_207, source.AutomationDocumentGeneration);
            Assert.Equal(source.LayoutRevision + 1, replacement.LayoutRevision);
            Assert.Equal(Width, owner.Width);
            Assert.Equal(0, source.LayoutRevision);

            for (int index = 0; index < sourceRoots.Length; index++)
            {
                InlineContainerBox original = sourceRoots[index];
                InlineContainerBox clone = replacementRoots[index];
                Assert.NotSame(original, clone);
                Assert.Equal(original.BlockIndex, clone.BlockIndex);
                Assert.Equal(original.ElementKey, clone.ElementKey);
                Assert.True(clone.HasMeasuredLayout);
                Assert.False(clone.IsDirty);
                Assert.NotSame(sourceLayouts[index], GetField<CanvasTextLayout>(clone, "_layout"));
                Assert.Equal(sourceBounds[index], original.Bounds);
                Assert.Equal(sourceLayouts[index], GetField<CanvasTextLayout>(original, "_layout"));

                // The private tree keeps both the raw margin inputs and the
                // effective, measured margin state rather than reconstructing
                // a collapsed margin from its public getter.
                Assert.Equal(original.Margin, clone.Margin);
                Assert.Equal(original.CollapsedTopMargin, clone.CollapsedTopMargin);
                Assert.Equal(original.SuppressTopMargin, clone.SuppressTopMargin);
                Assert.Equal(original.SuppressBottomMargin, clone.SuppressBottomMargin);
                Assert.Equal(original.Bounds.Height, clone.Bounds.Height, precision: 3);
                Assert.True(double.IsFinite(clone.Bounds.X));
                Assert.True(double.IsFinite(clone.Bounds.Y));
                Assert.True(double.IsFinite(clone.Bounds.Width));
                Assert.True(double.IsFinite(clone.Bounds.Height));
                Assert.Equal(sourceSlices[index], replacement.SourceMap.Slice(WholeBlockRange(clone)));

                InlineRun[] originalRuns = original.Runs.ToArray();
                InlineRun[] clonedRuns = clone.Runs.ToArray();
                Assert.Equal(originalRuns.Length, clonedRuns.Length);
                for (int runIndex = 0; runIndex < originalRuns.Length; runIndex++)
                {
                    Assert.NotSame(originalRuns[runIndex], clonedRuns[runIndex]);
                    Assert.Equal(originalRuns[runIndex].InlineIndex, clonedRuns[runIndex].InlineIndex);
                    Assert.Equal(originalRuns[runIndex].RenderedLength, clonedRuns[runIndex].RenderedLength);
                    Assert.Equal(originalRuns[runIndex].SourceSpan, clonedRuns[runIndex].SourceSpan);
                    Assert.Equal(originalRuns[runIndex].ElementKey, clonedRuns[runIndex].ElementKey);
                    Assert.Equal(originalRuns[runIndex].StyleAliases, clonedRuns[runIndex].StyleAliases);
                    Assert.Equal(originalRuns[runIndex].StyleModifierKeys, clonedRuns[runIndex].StyleModifierKeys);
                    Assert.Equal(originalRuns[runIndex].SemanticHeadingKey, clonedRuns[runIndex].SemanticHeadingKey);
                    if (originalRuns[runIndex] is LinkRun originalLink)
                    {
                        LinkRun clonedLink = Assert.IsType<LinkRun>(clonedRuns[runIndex]);
                        Assert.Equal(originalLink.Url, clonedLink.Url);
                        Assert.Equal(originalLink.Title, clonedLink.Title);
                        Assert.Equal(originalLink.AccessibilityName, clonedLink.AccessibilityName);
                        Assert.Equal(originalLink.IsSuperscript, clonedLink.IsSuperscript);
                        Assert.False(clonedLink.IsDisclosure);
                        Assert.False(clonedLink.IsExpanded);
                    }
                    else if (originalRuns[runIndex] is InlineImageRun originalImageRun)
                    {
                        InlineImageRun clonedImageRun = Assert.IsType<InlineImageRun>(clonedRuns[runIndex]);
                        Assert.NotSame(originalImageRun.Image, clonedImageRun.Image);
                        Assert.Same(originalImageRun.Image.Bitmap, clonedImageRun.Image.Bitmap);
                        Assert.Equal(originalImageRun.Url, clonedImageRun.Url);
                        Assert.Equal(originalImageRun.AltText, clonedImageRun.AltText);
                        Assert.Equal(originalImageRun.LinkUrl, clonedImageRun.LinkUrl);
                    }
                }
            }

            Assert.Equal(MarkdownElementKeys.Body, replacementRoots[0].ElementKey);
            Assert.Equal(MarkdownElementKeys.Heading2, replacementRoots[1].ElementKey);
            Assert.Equal(MarkdownElementKeys.Heading3, replacementRoots[2].ElementKey);
            Assert.Equal(
                new[] { 2, 3 },
                MarkdownSemanticDocument.EnumerateDepthFirst(replacement.SemanticDocument.Root)
                    .Where(static node => node.Role == MarkdownSemanticRole.Heading)
                    .Select(static node => node.HeadingLevel));

            InlineImageRun[] candidateImageRuns = replacementRoots
                .SelectMany(static root => root.Runs.OfType<InlineImageRun>())
                .ToArray();
            Assert.Equal(sourceImages.Length, candidateImageRuns.Length);
            ImageBox[] candidateImages = candidateImageRuns.Select(static run => run.Image).ToArray();
            Assert.Equal(
                sourceRoots.SelectMany(static root => root.Runs.OfType<InlineImageRun>())
                    .Select(static run => run.Url),
                candidateImageRuns.Select(static run => run.Url));
            for (int index = 0; index < sourceImages.Length; index++)
                Assert.NotSame(sourceImages[index], candidateImages[index]);
            var subscriptions = new HashSet<ImageBox>(sourceImages, ReferenceEqualityComparer.Instance);
            Assert.True(owner.MatchesSourceImages(sourceImages, subscriptions));
            Assert.True(owner.HasCurrentSourceRaster());

            MarkdownSemanticNode[] sourceImageNodes = MarkdownSemanticDocument
                .EnumerateDepthFirst(sourceSemantic.Root)
                .Where(static node => node.Role == MarkdownSemanticRole.Image)
                .ToArray();
            MarkdownSemanticNode[] candidateImageNodes = MarkdownSemanticDocument
                .EnumerateDepthFirst(replacement.SemanticDocument.Root)
                .Where(static node => node.Role == MarkdownSemanticRole.Image)
                .ToArray();
            Assert.Equal(3, sourceImageNodes.Length);
            Assert.Equal(3, candidateImageNodes.Length);
            for (int index = 0; index < candidateImageNodes.Length; index++)
            {
                Assert.Same(candidateImageRuns[index].Image, candidateImageNodes[index].ImageBox);
                Assert.Same(candidateImageRuns[index], candidateImageNodes[index].InlineRun);
                InlineContainerBox imageRoot = replacementRoots.Single(
                    root => root.Runs.Contains(candidateImageRuns[index]));
                Assert.Same(imageRoot, candidateImageNodes[index].InlineBox);
                Assert.Equal(sourceImageNodes[index].HelpText, candidateImageNodes[index].HelpText);
            }

            Assert.All(sourceImages.Select((image, index) => (image, index)), entry =>
            {
                Assert.Same(sourceBitmaps[entry.index], entry.image.Bitmap);
                Assert.False(entry.image.ImageLifetimeDisposedForTests);
            });
        }
        finally
        {
            await DisposeCandidateAndDrainAsync(candidate, constructionContext);
        }
    }

    [Fact]
    public async Task ExactAggregateRootRunAndImageCapsRemainAdmissible()
    {
        await using var seed = new MultiRasterPlacementCloneTests.MultiRasterFixture(imageCount: 1);
        int rootCount = LayoutSnapshot.MaximumPrivateRasterPlacementRoots;
        int runLimit = InlineContainerBox.MaximumPrivateRasterPlacementRuns;
        int imageLimit = InlineContainerBox.MaximumPrivateRasterPlacementImages;
        Assert.Equal(rootCount, imageLimit);
        Assert.Equal(0, runLimit % rootCount);
        int runsPerRoot = runLimit / rootCount;
        Assert.True(runsPerRoot >= 2);

        RootSpecification[] specs = new RootSpecification[rootCount];
        for (int rootIndex = 0; rootIndex < rootCount; rootIndex++)
        {
            var runs = new List<RunSpecification>(runsPerRoot)
            {
                RunSpecification.PlainText($"root-{rootIndex}-before "),
                RunSpecification.Image(0),
            };
            for (int runIndex = runs.Count; runIndex < runsPerRoot; runIndex++)
                runs.Add(runIndex % 2 == 0
                    ? RunSpecification.Link($" link-{rootIndex}-{runIndex}", $"https://example.test/{rootIndex}/{runIndex}")
                    : RunSpecification.PlainText($" tail-{rootIndex}-{runIndex}"));
            specs[rootIndex] = new RootSpecification(
                rootIndex % 2 == 0 ? MarkdownElementKeys.Body : MarkdownElementKeys.Heading2,
                runs.ToArray());
        }

        await using var document = new MultiRootDocumentFixture(seed, specs);
        document.Snapshot.AssignAutomationDocumentGeneration(1_241);
        int[] changedBlocks = document.ImageBearingBlockIndices;
        Assert.Equal(rootCount, changedBlocks.Length);
        Assert.Equal(runLimit, document.Roots.Sum(static root => root.Runs.Count));
        Assert.Equal(imageLimit, document.Images.Count);

        SingleRasterLayoutCandidate? candidate = null;
        MarkdownLayoutContext? constructionContext = null;
        try
        {
            candidate = document.Snapshot.CreateSingleRasterCandidate(
                changedBlocks,
                Width,
                CancellationToken.None,
                CancellationToken.None,
                document.ImagePreparationOwner,
                retirementSink: null,
                imageUnavailable: null,
                out constructionContext);
            SingleRasterLayoutCandidate owner = Assert.IsType<SingleRasterLayoutCandidate>(candidate);
            MarkdownLayoutContext buildContext = Assert.IsType<MarkdownLayoutContext>(constructionContext);
            Assert.Equal(rootCount, owner.Snapshot.Blocks.Count);
            Assert.Equal(imageLimit, owner.SourceImageCount);
            Assert.Equal(runLimit, owner.Snapshot.Blocks
                .OfType<InlineContainerBox>()
                .Sum(static root => root.Runs.Count));
            Assert.All(owner.Snapshot.Blocks, static block => Assert.IsType<InlineContainerBox>(block));
            Assert.True(owner.HasCurrentSourceRaster());
        }
        finally
        {
            await DisposeCandidateAndDrainAsync(candidate, constructionContext);
        }
    }

    [Fact]
    public async Task AggregateCapsAndUnsupportedLaterRootRejectWithoutLeakingSourceLeases()
    {
        await using var seed = new MultiRasterPlacementCloneTests.MultiRasterFixture(imageCount: 1);

        int maximumRoots = LayoutSnapshot.MaximumPrivateRasterPlacementRoots;
        RootSpecification[] tooManyRoots = Enumerable.Range(0, maximumRoots + 1)
            .Select(index => new RootSpecification(
                MarkdownElementKeys.Body,
                index == 0
                    ? [RunSpecification.PlainText("root "), RunSpecification.Image(0)]
                    : [RunSpecification.PlainText($"root-{index}")]))
            .ToArray();
        await AssertRejectedWithoutLeaseLeakAsync(seed, tooManyRoots);

        int maximumRuns = InlineContainerBox.MaximumPrivateRasterPlacementRuns;
        RootSpecification[] tooManyRuns =
        [
            new RootSpecification(
                MarkdownElementKeys.Body,
                Enumerable.Range(0, maximumRuns / 2)
                    .Select(index => RunSpecification.PlainText($"a{index} "))
                    .Append(RunSpecification.Image(0))
                    .ToArray()),
            new RootSpecification(
                MarkdownElementKeys.Heading2,
                Enumerable.Range(0, maximumRuns / 2)
                    .Select(index => RunSpecification.PlainText($"b{index} "))
                    .ToArray()),
        ];
        Assert.Equal(maximumRuns + 1, tooManyRuns.Sum(static root => root.Runs.Length));
        await AssertRejectedWithoutLeaseLeakAsync(seed, tooManyRuns);

        int maximumImages = InlineContainerBox.MaximumPrivateRasterPlacementImages;
        RootSpecification[] tooManyImages =
        [
            new RootSpecification(MarkdownElementKeys.Body,
                Enumerable.Range(0, maximumImages / 2 + 1)
                    .Select(_ => RunSpecification.Image(0)).ToArray()),
            new RootSpecification(MarkdownElementKeys.Heading2,
                Enumerable.Range(0, maximumImages / 2)
                    .Select(_ => RunSpecification.Image(0)).ToArray()),
        ];
        Assert.Equal(maximumImages + 1,
            tooManyImages.Sum(static root => root.Runs.Count(static run => run.Kind == RunKind.Image)));
        await AssertRejectedWithoutLeaseLeakAsync(seed, tooManyImages);

        RootSpecification[] unsupportedLaterRoot =
        [
            new RootSpecification(MarkdownElementKeys.Body,
                [RunSpecification.PlainText("eligible prefix "), RunSpecification.Image(0)]),
            new RootSpecification(MarkdownElementKeys.Heading2,
                [RunSpecification.DisclosureLink("expanded details")]),
        ];
        await AssertUnsupportedLaterRootWasPreflightedAsync(seed, unsupportedLaterRoot);
    }

    [Fact]
    public async Task AggregateTextLengthAtExactLimitRemainsAdmissible()
    {
        await using var seed = new MultiRasterPlacementCloneTests.MultiRasterFixture(imageCount: 1);
        int textLimit = LayoutSnapshot.MaximumPrivateRasterPlacementTextLength;
        int half = textLimit / 2;
        int imagePlaceholderLength = seed.ImageRuns[0].Text.Length;
        RootSpecification[] specs =
        [
            new RootSpecification(MarkdownElementKeys.Body,
                [RunSpecification.PlainText(new string('a', half - imagePlaceholderLength)), RunSpecification.Image(0)]),
            new RootSpecification(MarkdownElementKeys.Heading2,
                [RunSpecification.PlainText(new string('b', half))]),
        ];
        await using var document = new MultiRootDocumentFixture(seed, specs);
        Assert.Equal(textLimit, document.Roots.Sum(static root => root.PrivateRasterPlacementTextLength));

        SingleRasterLayoutCandidate? candidate = null;
        MarkdownLayoutContext? constructionContext = null;
        try
        {
            candidate = document.Snapshot.CreateSingleRasterCandidate(
                document.ImageBearingBlockIndices,
                Width,
                CancellationToken.None,
                CancellationToken.None,
                document.ImagePreparationOwner,
                retirementSink: null,
                imageUnavailable: null,
                out constructionContext);
            SingleRasterLayoutCandidate owner = Assert.IsType<SingleRasterLayoutCandidate>(candidate);
            MarkdownLayoutContext buildContext = Assert.IsType<MarkdownLayoutContext>(constructionContext);
            Assert.Equal(textLimit, owner.Snapshot.Blocks
                .OfType<InlineContainerBox>()
                .Sum(static root => root.PrivateRasterPlacementTextLength));
            Assert.Equal(1, owner.SourceImageCount);
            Assert.Equal(0, document.Snapshot.LayoutRevision);
        }
        finally
        {
            await DisposeCandidateAndDrainAsync(candidate, constructionContext);
        }
    }

    [Fact]
    public async Task AggregateTextLengthOneOverAndOversizedSingleRunRejectWithoutLeakingSourceLeases()
    {
        await using var seed = new MultiRasterPlacementCloneTests.MultiRasterFixture(imageCount: 1);
        int textLimit = LayoutSnapshot.MaximumPrivateRasterPlacementTextLength;
        int half = textLimit / 2;
        int imagePlaceholderLength = seed.ImageRuns[0].Text.Length;

        RootSpecification[] aggregateOneOver =
        [
            new RootSpecification(MarkdownElementKeys.Body,
                [RunSpecification.PlainText(new string('a', half + 1 - imagePlaceholderLength)), RunSpecification.Image(0)]),
            new RootSpecification(MarkdownElementKeys.Heading2,
                [RunSpecification.PlainText(new string('b', half))]),
        ];
        await using (var document = new MultiRootDocumentFixture(seed, aggregateOneOver))
        {
            Assert.Equal(textLimit + 1, document.Roots.Sum(static root => root.PrivateRasterPlacementTextLength));
            Assert.All(document.Roots, root => Assert.True(root.PrivateRasterPlacementTextLength < textLimit));
        }
        await AssertRejectedWithoutLeaseLeakAsync(seed, aggregateOneOver);

        RootSpecification[] oversizedSingleRun =
        [
            new RootSpecification(MarkdownElementKeys.Body,
                [RunSpecification.PlainText(new string('x', textLimit + 1)), RunSpecification.Image(0)]),
        ];
        await using (var document = new MultiRootDocumentFixture(seed, oversizedSingleRun))
        {
            Assert.True(document.Roots[0].Runs[0].Text.Length > textLimit);
            Assert.True(document.Roots[0].PrivateRasterPlacementTextLength > textLimit);
        }
        await AssertRejectedWithoutLeaseLeakAsync(seed, oversizedSingleRun);
    }

    [Fact]
    public async Task SemanticTextLengthAtLimitIsAcceptedAndOneOverIsRejected()
    {
        await using var seed = new MultiRasterPlacementCloneTests.MultiRasterFixture(imageCount: 1);
        int semanticLimit = LayoutSnapshot.MaximumPrivateRasterPlacementSemanticTextLength;
        RootSpecification[] atLimit =
        [
            new RootSpecification(MarkdownElementKeys.Body,
                [new RunSpecification(RunKind.Image, new string('a', semanticLimit), ImageIndex: 0)]),
        ];
        await using (var document = new MultiRootDocumentFixture(seed, atLimit))
        {
            Assert.Equal(semanticLimit, document.Snapshot.SemanticDocument.Text.Length);
            SingleRasterLayoutCandidate? candidate = null;
            MarkdownLayoutContext? constructionContext = null;
            try
            {
                candidate = document.Snapshot.CreateSingleRasterCandidate(
                    document.ImageBearingBlockIndices,
                    Width,
                    CancellationToken.None,
                    CancellationToken.None,
                    document.ImagePreparationOwner,
                    retirementSink: null,
                    imageUnavailable: null,
                    out constructionContext);
                SingleRasterLayoutCandidate owner = Assert.IsType<SingleRasterLayoutCandidate>(candidate);
                Assert.Equal(semanticLimit, owner.Snapshot.SemanticDocument.Text.Length);
                Assert.Equal(1, owner.SourceImageCount);
            }
            finally
            {
                await DisposeCandidateAndDrainAsync(candidate, constructionContext);
            }
        }

        RootSpecification[] oneOver =
        [
            new RootSpecification(MarkdownElementKeys.Body,
                [new RunSpecification(RunKind.Image, new string('a', semanticLimit + 1), ImageIndex: 0)]),
        ];
        await using (var document = new MultiRootDocumentFixture(seed, oneOver))
        {
            Assert.Equal(semanticLimit + 1, document.Snapshot.SemanticDocument.Text.Length);
            ImageBox sourceImage = Assert.Single(document.Images);
            int referencesBefore = GetEntryReferenceCount(GetImageLease(sourceImage));
            SingleRasterLayoutCandidate? candidate = null;
            MarkdownLayoutContext? constructionContext = null;
            try
            {
                candidate = document.Snapshot.CreateSingleRasterCandidate(
                    document.ImageBearingBlockIndices,
                    Width,
                    CancellationToken.None,
                    CancellationToken.None,
                    document.ImagePreparationOwner,
                    retirementSink: null,
                    imageUnavailable: null,
                    out constructionContext);
                Assert.Null(candidate);
                Assert.Null(constructionContext);
                Assert.Equal(referencesBefore, GetEntryReferenceCount(GetImageLease(sourceImage)));
            }
            finally
            {
                try
                {
                    candidate?.Dispose();
                }
                finally
                {
                    if (constructionContext is not null)
                        await constructionContext.DrainOwnedRetirementsAsync();
                }
            }
        }
    }

    [Fact]
    public async Task FailedLateRasterCloneRetiresEarlierRootClonesAndPreservesSource()
    {
        await using var seed = new MultiRasterPlacementCloneTests.MultiRasterFixture(
            imageCount: 2,
            finalImageIsSvg: true);
        RootSpecification[] specs =
        [
            new RootSpecification(MarkdownElementKeys.Body,
                [RunSpecification.PlainText("first root "), RunSpecification.Image(0)]),
            new RootSpecification(MarkdownElementKeys.Heading2,
                [RunSpecification.PlainText("later root "), RunSpecification.Image(1)]),
        ];
        await using var document = new MultiRootDocumentFixture(seed, specs);
        document.Snapshot.AssignAutomationDocumentGeneration(1_257);
        ImageBox readySource = document.Images[0];
        CanvasBitmap readyBitmap = Assert.IsType<CanvasBitmap>(readySource.Bitmap);
        int referenceCount = GetEntryReferenceCount(GetImageLease(readySource));

        MarkdownLayoutContext? constructionContext = null;
        SingleRasterLayoutCandidate? candidate = null;
        try
        {
            candidate = document.Snapshot.CreateSingleRasterCandidate(
                document.ImageBearingBlockIndices,
                Width,
                CancellationToken.None,
                CancellationToken.None,
                document.ImagePreparationOwner,
                retirementSink: null,
                imageUnavailable: null,
                out constructionContext);
            Assert.Null(candidate);
            MarkdownLayoutContext buildContext = Assert.IsType<MarkdownLayoutContext>(constructionContext);
            await buildContext.DrainOwnedRetirementsAsync();
            Assert.Equal(referenceCount, GetEntryReferenceCount(GetImageLease(readySource)));
            Assert.Same(readyBitmap, readySource.Bitmap);
            Assert.False(readySource.ImageLifetimeDisposedForTests);
            Assert.Same(document.Roots[0], document.Snapshot.Blocks[0]);
            Assert.Same(document.Roots[1], document.Snapshot.Blocks[1]);
            Assert.Equal(0, document.Snapshot.LayoutRevision);
            Assert.Equal(1_257, document.Snapshot.AutomationDocumentGeneration);
        }
        finally
        {
            await DisposeCandidateAndDrainAsync(candidate, constructionContext);
        }
    }

    [Fact]
    public async Task CanceledImageGenerationAndSourceRetirementRespectEveryRootOwner()
    {
        await using var seed = new MultiRasterPlacementCloneTests.MultiRasterFixture(imageCount: 3);
        await using var document = new MultiRootDocumentFixture(seed, CreateMixedRoots());
        LayoutSnapshot source = document.Snapshot;
        source.AssignAutomationDocumentGeneration(1_283);
        ImageBox[] sourceImages = document.Images.ToArray();
        CanvasBitmap[] sourceBitmaps = sourceImages
            .Select(static image => Assert.IsType<CanvasBitmap>(image.Bitmap))
            .ToArray();
        int sourceEntryReferences = GetEntryReferenceCount(GetImageLease(sourceImages[0]));

        using var canceledImageLifetime = new CancellationTokenSource();
        canceledImageLifetime.Cancel();
        SingleRasterLayoutCandidate? canceledCandidate = null;
        MarkdownLayoutContext? canceledContext = null;
        try
        {
            canceledCandidate = source.CreateSingleRasterCandidate(
                document.ImageBearingBlockIndices,
                Width,
                CancellationToken.None,
                canceledImageLifetime.Token,
                document.ImagePreparationOwner,
                retirementSink: null,
                imageUnavailable: null,
                out canceledContext);
            Assert.Null(canceledCandidate);
            MarkdownLayoutContext canceledBuild = Assert.IsType<MarkdownLayoutContext>(canceledContext);
            await canceledBuild.DrainOwnedRetirementsAsync();
            Assert.Equal(sourceEntryReferences, GetEntryReferenceCount(GetImageLease(sourceImages[0])));
            Assert.Same(source, document.Snapshot);
            Assert.Equal(0, source.LayoutRevision);
            Assert.Equal(1_283, source.AutomationDocumentGeneration);
            for (int index = 0; index < sourceImages.Length; index++)
                Assert.Same(sourceBitmaps[index], sourceImages[index].Bitmap);
        }
        finally
        {
            await DisposeCandidateAndDrainAsync(canceledCandidate, canceledContext);
        }

        SingleRasterLayoutCandidate? candidate = null;
        MarkdownLayoutContext? constructionContext = null;
        try
        {
            candidate = source.CreateSingleRasterCandidate(
                document.ImageBearingBlockIndices,
                Width,
                CancellationToken.None,
                CancellationToken.None,
                document.ImagePreparationOwner,
                retirementSink: null,
                imageUnavailable: null,
                out constructionContext);
            SingleRasterLayoutCandidate owner = Assert.IsType<SingleRasterLayoutCandidate>(candidate);
            MarkdownLayoutContext buildContext = Assert.IsType<MarkdownLayoutContext>(constructionContext);
            InlineImageRun[] clonedImageRuns = owner.Snapshot.Blocks
                .OfType<InlineContainerBox>()
                .SelectMany(static root => root.Runs.OfType<InlineImageRun>())
                .ToArray();
            CanvasBitmap[] retainedCandidateBitmaps = clonedImageRuns
                .Select(static run => Assert.IsType<CanvasBitmap>(run.Image.Bitmap))
                .ToArray();
            await source.Retire().WaitAsync(TimeSpan.FromSeconds(10));
            await document.SourceContext.DrainOwnedRetirementsAsync();
            Assert.All(sourceImages, static image => Assert.Null(image.Bitmap));
            for (int index = 0; index < clonedImageRuns.Length; index++)
                Assert.Same(retainedCandidateBitmaps[index], clonedImageRuns[index].Image.Bitmap);

            candidate.Dispose();
            await buildContext.DrainOwnedRetirementsAsync();
            Assert.All(clonedImageRuns, static run => Assert.Null(run.Image.Bitmap));
            Assert.True(source.IsRetiringOrDisposed);
        }
        finally
        {
            await DisposeCandidateAndDrainAsync(candidate, constructionContext);
        }
    }

    private static RootSpecification[] CreateMixedRoots()
        =>
        [
            new RootSpecification(MarkdownElementKeys.Body,
                [
                    RunSpecification.PlainText("body before e\u0301 👩‍👩‍👧 "),
                    RunSpecification.Link("linked paragraph", "https://example.test/body") with
                    {
                        AccessibilityName = "body link",
                    },
                    RunSpecification.PlainText(" after אבג"),
                ]),
            new RootSpecification(MarkdownElementKeys.Heading2,
                [
                    RunSpecification.PlainText("heading two "),
                    RunSpecification.Image(0),
                    RunSpecification.Link("heading link", "https://example.test/heading") with
                    {
                        AccessibilityName = "heading resource",
                    },
                ]),
            new RootSpecification(MarkdownElementKeys.Heading3,
                [
                    RunSpecification.Image(1),
                    RunSpecification.PlainText(" between rasters "),
                    RunSpecification.Image(2),
                ]),
        ];

    private static DocumentRange WholeBlockRange(InlineContainerBox block)
    {
        Assert.NotEmpty(block.Runs);
        InlineRun last = block.Runs[^1];
        return new DocumentRange(
            new DocumentPosition(block.BlockIndex, 0, 0),
            new DocumentPosition(block.BlockIndex, last.InlineIndex, last.Text.Length));
    }

    private static async Task AssertRejectedWithoutLeaseLeakAsync(
        MultiRasterPlacementCloneTests.MultiRasterFixture seed,
        RootSpecification[] specs)
    {
        await using var document = new MultiRootDocumentFixture(seed, specs);
        Assert.NotEmpty(document.Images);
        ImageBox sourceImage = document.Images[0];
        int referencesBefore = GetEntryReferenceCount(GetImageLease(sourceImage));
        SingleRasterLayoutCandidate? candidate = null;
        MarkdownLayoutContext? constructionContext = null;
        try
        {
            candidate = document.Snapshot.CreateSingleRasterCandidate(
                document.ImageBearingBlockIndices,
                Width,
                CancellationToken.None,
                CancellationToken.None,
                document.ImagePreparationOwner,
                retirementSink: null,
                imageUnavailable: null,
                out constructionContext);
            Assert.Null(candidate);
        }
        finally
        {
            await DisposeCandidateAndDrainAsync(candidate, constructionContext);
        }

        Assert.Equal(referencesBefore, GetEntryReferenceCount(GetImageLease(sourceImage)));
        Assert.Equal(0, document.Snapshot.LayoutRevision);
    }

    private static async Task AssertUnsupportedLaterRootWasPreflightedAsync(
        MultiRasterPlacementCloneTests.MultiRasterFixture seed,
        RootSpecification[] specs)
    {
        await using var document = new MultiRootDocumentFixture(seed, specs);
        ImageBox firstImage = Assert.Single(document.Images);
        int referencesBefore = GetEntryReferenceCount(GetImageLease(firstImage));
        MarkdownLayoutContext probeContext = document.SourceContext.CreateRasterPlacementContext(
            CancellationToken.None,
            document.ImagePreparationOwner,
            retirementSink: null,
            imageUnavailable: null);
        try
        {
            Assert.True(document.Roots[0].TryGetPrivateRasterPlacementCounts(
                probeContext, Width - (float)(DocumentPadding.Left + DocumentPadding.Right),
                out _, out int firstRootImages));
            Assert.Equal(1, firstRootImages);
            Assert.False(document.Roots[1].TryGetPrivateRasterPlacementCounts(
                probeContext, Width - (float)(DocumentPadding.Left + DocumentPadding.Right),
                out _, out _));
        }
        finally
        {
            await probeContext.DrainOwnedRetirementsAsync();
        }

        await AssertRejectedWithoutLeaseLeakAsync(seed, specs);
        Assert.Equal(referencesBefore, GetEntryReferenceCount(GetImageLease(firstImage)));
    }

    private static async Task DisposeCandidateAndDrainAsync(
        SingleRasterLayoutCandidate? candidate,
        MarkdownLayoutContext? constructionContext)
    {
        try
        {
            candidate?.Dispose();
        }
        finally
        {
            if (constructionContext is not null)
                await constructionContext.DrainOwnedRetirementsAsync();
        }
    }

    private static SharedCanvasBitmapCache.Lease GetImageLease(ImageBox image)
    {
        FieldInfo field = typeof(ImageBox).GetField("_bitmapLease", BindingFlags.Instance | BindingFlags.NonPublic)!;
        return Assert.IsType<SharedCanvasBitmapCache.Lease>(field.GetValue(image));
    }

    private static int GetEntryReferenceCount(SharedCanvasBitmapCache.Lease lease)
    {
        FieldInfo entryField = typeof(SharedCanvasBitmapCache.Lease).GetField(
            "_entry", BindingFlags.Instance | BindingFlags.NonPublic)!;
        object entry = Assert.IsAssignableFrom<SharedCanvasBitmapCache.Entry>(entryField.GetValue(lease));
        FieldInfo countField = typeof(SharedCanvasBitmapCache.Entry).GetField(
            "_referenceCount", BindingFlags.Instance | BindingFlags.NonPublic)!;
        return Assert.IsType<int>(countField.GetValue(entry));
    }

    private static T GetField<T>(object instance, string name)
    {
        FieldInfo field = instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
        return Assert.IsType<T>(field.GetValue(instance));
    }

    private static T GetAssignableField<T>(object instance, string name)
        where T : class
    {
        FieldInfo field = instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Field '{name}' was not found.");
        return Assert.IsAssignableFrom<T>(field.GetValue(instance));
    }

    private enum RunKind
    {
        Text,
        Link,
        Image,
        DisclosureLink,
    }

    private readonly record struct RunSpecification(
        RunKind Kind,
        string Text,
        string? Url = null,
        int ImageIndex = 0,
        string? AccessibilityName = null)
    {
        internal static RunSpecification PlainText(string text) => new(RunKind.Text, text);
        internal static RunSpecification Link(string text, string url) => new(RunKind.Link, text, url);
        internal static RunSpecification Image(int imageIndex) => new(RunKind.Image, $"raster-{imageIndex}", ImageIndex: imageIndex);
        internal static RunSpecification DisclosureLink(string text) =>
            new(RunKind.DisclosureLink, text, "https://example.test/disclosure");
    }

    private sealed record RootSpecification(string ElementKey, RunSpecification[] Runs);

    private sealed class MultiRootDocumentFixture : IAsyncDisposable
    {
        private readonly MarkdownLayoutContext _sourceContext;
        private bool _retired;

        internal MultiRootDocumentFixture(
            MultiRasterPlacementCloneTests.MultiRasterFixture seed,
            RootSpecification[] specifications)
        {
            ArgumentNullException.ThrowIfNull(seed);
            ArgumentNullException.ThrowIfNull(specifications);
            if (specifications.Length == 0)
                throw new ArgumentException("The multi-root fixture needs at least one block.", nameof(specifications));

            var sourceText = new StringBuilder();
            foreach (RootSpecification root in specifications)
            foreach (RunSpecification run in root.Runs)
                sourceText.Append(run.Kind == RunKind.Image ? "!" : run.Text);

            var sourceMap = new MarkdownSourceMap(sourceText.ToString());
            _sourceContext = new MarkdownLayoutContext(
                seed.SourceContext.ResourceCreator,
                seed.SourceContext.ThemeSnapshot,
                sourceMap,
                seed.SourceContext.Registry,
                seed.SourceContext.FlowDirection,
                seed.SourceContext.Dispatcher,
                seed.SourceContext.Language)
            {
                ImageCancellationToken = seed.SourceContext.ImageCancellationToken,
                RasterizationScale = seed.SourceContext.RasterizationScale,
                ImagePreparationOwner = seed.SourceContext.ImagePreparationOwner,
                DisclosureStates = seed.SourceContext.DisclosureStates,
            };

            var roots = new InlineContainerBox[specifications.Length];
            var allImages = new List<ImageBox>();
            int sourceOffset = 0;
            float contentWidth = (float)(Width - DocumentPadding.Left - DocumentPadding.Right);
            float y = (float)DocumentPadding.Top;
            for (int rootIndex = 0; rootIndex < specifications.Length; rootIndex++)
            {
                RootSpecification specification = specifications[rootIndex];
                var root = new InlineContainerBox(_sourceContext, specification.ElementKey)
                {
                    BlockIndex = rootIndex + 1,
                    UseContainerMargin = false,
                    Margin = new Thickness(2 + rootIndex, 5 + rootIndex, 3 + rootIndex, 7 + rootIndex),
                    CollapsedTopMargin = rootIndex % 2 == 0 ? 0 : 1.25 + rootIndex,
                    SuppressTopMargin = rootIndex % 3 == 1,
                    SuppressBottomMargin = rootIndex % 3 == 2,
                };

                foreach (RunSpecification runSpecification in specification.Runs)
                {
                    string sourceValue = runSpecification.Kind == RunKind.Image ? "!" : runSpecification.Text;
                    var sourceSpan = new SourceSpan(sourceOffset, sourceValue.Length);
                    InlineRun run = runSpecification.Kind switch
                    {
                        RunKind.Text => new TextRun(runSpecification.Text)
                        {
                            ElementKey = MarkdownElementKeys.Strong,
                            StyleAliases = ["multi-root-text"],
                            SourceSpan = sourceSpan,
                        },
                        RunKind.Link => new LinkRun(runSpecification.Text, runSpecification.Url!, "fixture link")
                        {
                            AccessibilityName = runSpecification.AccessibilityName ?? "accessible fixture link",
                            SourceSpan = sourceSpan,
                            StyleAliases = ["multi-root-link"],
                        },
                        RunKind.DisclosureLink => new LinkRun(runSpecification.Text, runSpecification.Url!)
                        {
                            DisclosureId = "fixture-disclosure",
                            SourceSpan = sourceSpan,
                        },
                        RunKind.Image => new InlineImageRun(
                            _sourceContext,
                            runSpecification.Text,
                            seed.ImageRuns[runSpecification.ImageIndex].Url,
                            title: $"title-{runSpecification.ImageIndex}",
                            linkUrl: $"https://example.test/image/{runSpecification.ImageIndex}")
                        {
                            ElementKey = MarkdownElementKeys.Link,
                            SourceSpan = sourceSpan,
                            StyleAliases = ["multi-root-image"],
                        },
                        _ => throw new ArgumentOutOfRangeException(nameof(specifications)),
                    };
                    root.Add(run);
                    if (run is InlineImageRun imageRun)
                        allImages.Add(imageRun.Image);
                    sourceOffset += sourceValue.Length;
                }

                float height = root.Measure(contentWidth);
                root.Arrange((float)DocumentPadding.Left, y, contentWidth);
                y += height + (float)BlockSpacing;
                roots[rootIndex] = root;
            }

            Roots = roots;
            Images = allImages;
            ImageBearingBlockIndices = roots
                .Where(static root => root.Runs.Any(static run => run is InlineImageRun))
                .Select(static root => root.BlockIndex)
                .ToArray();
            Source = new LayoutSnapshot(
                roots,
                sourceMap,
                Width,
                y - (float)BlockSpacing + (float)DocumentPadding.Bottom,
                documentPadding: DocumentPadding,
                blockSpacing: BlockSpacing);
        }

        internal LayoutSnapshot Source { get; }
        internal LayoutSnapshot Snapshot => Source;
        internal InlineContainerBox[] Roots { get; }
        internal IReadOnlyList<ImageBox> Images { get; }
        internal int[] ImageBearingBlockIndices { get; }
        internal MarkdownLayoutContext SourceContext => _sourceContext;
        internal object ImagePreparationOwner =>
            Assert.IsAssignableFrom<object>(_sourceContext.ImagePreparationOwner);

        public async ValueTask DisposeAsync()
        {
            if (_retired)
                return;
            _retired = true;
            await Source.Retire().WaitAsync(TimeSpan.FromSeconds(10));
            await _sourceContext.DrainOwnedRetirementsAsync();
        }
    }
}
