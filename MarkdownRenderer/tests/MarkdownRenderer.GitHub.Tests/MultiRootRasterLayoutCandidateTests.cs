using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using MarkdownRenderer.Accessibility.Internal.Peers;
using MarkdownRenderer.Accessibility.Internal.Semantics;
using MarkdownRenderer.Document;
using MarkdownRenderer.GitHub;
using MarkdownRenderer.Images;
using MarkdownRenderer.Layout.Internal.Building;
using MarkdownRenderer.Layout.Internal.Context;
using MarkdownRenderer.Layout.Internal.Images;
using MarkdownRenderer.Layout.Internal.Inlines;
using MarkdownRenderer.Layout.Internal.Inlines.Runs;
using MarkdownRenderer.Layout.Internal.PrivatePlacement;
using MarkdownRenderer.Layout.Internal.Snapshots;
using MarkdownRenderer.Parsing.Internal;
using MarkdownRenderer.Performance;
using MarkdownRenderer.Theming;
using Markdig;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Xunit;
using static MarkdownRenderer.GitHub.Tests.MultiRootCandidateAssertions;
using static MarkdownRenderer.GitHub.Tests.ProgressiveRasterCandidateAssertions;
using RootSpecification = MarkdownRenderer.GitHub.Tests.MultiRootDocumentFixture.RootSpecification;
using RunKind = MarkdownRenderer.GitHub.Tests.MultiRootDocumentFixture.RunKind;
using RunSpecification = MarkdownRenderer.GitHub.Tests.MultiRootDocumentFixture.RunSpecification;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>
/// Single-raster layout candidates built over several top-level roots.
/// </summary>
/// <remarks>
/// This file is longer than 500 lines because it keeps every admission and
/// lifetime scenario for <c>CreateSingleRasterCandidate</c> over multiple roots
/// together: the lazy progressive clone, mixed-root semantics, the exact and
/// one-over root, run, image, text and semantic caps, the failed late clone and
/// cancellation. Fixtures and oracles live in <see cref="MultiRootDocumentFixture"/>,
/// <see cref="MultiRootCandidateAssertions"/> and
/// <see cref="ProgressiveRasterCandidateAssertions"/>.
/// </remarks>
[Collection(SharedCanvasBitmapCacheTestCollection.Name)]
public sealed class MultiRootRasterLayoutCandidateTests
{
    private const float Width = MultiRootDocumentFixture.Width;

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
        string sourceText = CreateProgressiveSourceText();
        MarkdownExtensionRegistry registry = new MarkdownExtensionRegistry().ConfigureGitHubReadmeRegistry();
        Markdig.Syntax.MarkdownDocument parsed = Markdown.Parse(sourceText, registry.BuildPipeline());
        MarkdownDocument parsedDocument = MarkdownDocument.FromParsed(sourceText, parsed);
        var sourceMap = new MarkdownSourceMap(sourceText);
        MarkdownLayoutContext sourceContext = CreateProgressiveSourceContext(
            seed, session, resolver, registry, sourceMap, rightToLeft, rasterizationScale);

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
            AssertLazyProgressiveSource(source, sourceMap);
            MarkdownSemanticDocument sourceSemantic = source.SemanticDocument;

            ProgressiveSource completed = await CompleteProgressiveSourceImageAsync(source, resolver);
            ImageBox sourceImage = completed.Image;
            InlineContainerBox[] sourceRoots = completed.Roots;

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
            AssertLazyReplacementPreservesSourceIdentity(source, replacement, sourceSemantic, sourceMeasuredCount);
            AssertReplacementImageReusesSourceRaster(sourceImage, replacementImage, rasterizationScale);
            AssertClonedRootsKeepMeasurementState(
                ownedCandidate, sourceImage, replacementImage, sourceRoots, replacementRoots);

            PaintAtDeviceDpi(seed.Device, source.Blocks[0]);

            AssertReplacementMeasuresFarRootPrivately(replacement, replacementRoots[^1]);

            AssertSourceKeptOnlyItsOwnMeasurements(
                source, completed, sourceRevision, sourceMeasuredCount, resolver, replacementImage);

            await source.Retire().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Null(sourceImage.Bitmap);
            Assert.NotNull(replacementImage.Bitmap);
            Assert.False(ownedCandidate.HasCurrentSourceRaster());
            PaintAtDeviceDpi(seed.Device, replacementImage);

            _ = ownedCandidateContext;
        }
        finally
        {
            await DisposeCandidateAndRetireSourceAsync(candidate, candidateContext, source, sourceContext);
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
            .Select(static root => root.TextLayout!)
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
                AssertRootCloneMatchesSource(sourceRoots[index], replacementRoots[index],
                    sourceLayouts[index], sourceBounds[index], sourceSlices[index], replacement.SourceMap);

            AssertMixedHeadingRootsAndSemantics(replacement, replacementRoots);
            AssertCandidateImagesAreDistinctClonesWithSemanticOwners(
                owner, sourceSemantic, sourceRoots, replacementRoots, sourceImages);
            AssertSourceRastersRetained(sourceImages, sourceBitmaps);
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
            Assert.False(readySource.GetWorkState().LifetimeDisposed);
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
}
