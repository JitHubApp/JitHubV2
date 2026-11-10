using MarkdownRenderer.Accessibility.Internal.Semantics;
using MarkdownRenderer.Document;
using MarkdownRenderer.Extensions;
using MarkdownRenderer.Images;
using MarkdownRenderer.Layout.Internal.Boxes;
using MarkdownRenderer.Layout.Internal.Context;
using MarkdownRenderer.Layout.Internal.Images;
using MarkdownRenderer.Layout.Internal.Inlines;
using MarkdownRenderer.Layout.Internal.Inlines.Runs;
using MarkdownRenderer.Layout.Internal.LazyLayout;
using MarkdownRenderer.Layout.Internal.PrivatePlacement;
using MarkdownRenderer.Layout.Internal.Snapshots;
using MarkdownRenderer.Parsing.Internal;
using MarkdownRenderer.Performance;
using MarkdownRenderer.Selection;
using MarkdownRenderer.Theming;
using MarkdownRenderer.Theming.Internal;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Xunit;
using RootSpecification = MarkdownRenderer.GitHub.Tests.MultiRootDocumentFixture.RootSpecification;
using RunKind = MarkdownRenderer.GitHub.Tests.MultiRootDocumentFixture.RunKind;
using RunSpecification = MarkdownRenderer.GitHub.Tests.MultiRootDocumentFixture.RunSpecification;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>
/// Arrange and oracle steps for a single-raster candidate built from a
/// default progressive lazy snapshot whose far roots are still estimated.
/// </summary>
internal static class ProgressiveRasterCandidateAssertions
{
    private const float Width = MultiRootDocumentFixture.Width;

    internal static string CreateProgressiveSourceText() =>
        "![front alt](https://images.example/private-placement.png)\n\n" +
        "Measured `code`, **bold**, *emphasis*, [link](https://example.test/measured), and  \n" +
        "a hard line break.\n\n" +
        string.Join("\n\n", Enumerable.Range(0, 18).Select(index =>
            $"Tail {index}: `code-{index}`, **bold-{index}**, *emphasis-{index}*, " +
            $"[link-{index}](https://example.test/tail/{index}), and  \ncontinued-{index}."));

    internal static MarkdownLayoutContext CreateProgressiveSourceContext(
        CompletedRasterPlacementCloneTests.PlacementFixture seed,
        MarkdownPerformanceSession session,
        PrivatePlacementRasterResolver resolver,
        MarkdownExtensionRegistry registry,
        MarkdownSourceMap sourceMap,
        bool rightToLeft,
        float rasterizationScale) =>
        new(
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
            PerformanceDocumentOwner = new object(),
            PerformanceSession = session,
            RenderResources = seed.SourceContext.RenderResources,
            RasterizationScale = rasterizationScale,
            ImageResolver = resolver,
            AllowThirdPartyRemoteImages = true,
        };

    internal static void AssertLazyProgressiveSource(LayoutSnapshot source, MarkdownSourceMap sourceMap)
    {
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
    }

    internal static void AssertHasEveryRichInlineRun(InlineContainerBox root)
    {
        Assert.Contains(root.Runs, static run => run is CodeInlineRun);
        Assert.Contains(root.Runs, static run => run is StrongRun);
        Assert.Contains(root.Runs, static run => run is EmphasisRun);
        Assert.Contains(root.Runs, static run => run is LinkRun);
        Assert.Contains(root.Runs, static run => run is LineBreakRun);
    }

    internal static void AssertLazyReplacementPreservesSourceIdentity(
        LayoutSnapshot source,
        LayoutSnapshot replacement,
        MarkdownSemanticDocument sourceSemantic,
        int sourceMeasuredCount)
    {
        Assert.True(replacement.IsLazyLayoutEnabled);
        Assert.Null(replacement.VectorSceneResidencyPolicy);
        Assert.Equal(source.TopLevelBlockCount, replacement.TopLevelBlockCount);
        Assert.Equal(sourceMeasuredCount, replacement.MeasuredTopLevelBlockCount);
        Assert.Equal(
            source.GeneratedBlockIndexUpperBound,
            replacement.GeneratedBlockIndexUpperBound);
        IReadOnlyDictionary<int, int> sourceOrdinalIndex = source.Geometry.TopLevelOrdinalByBlockIndex;
        IReadOnlyDictionary<int, int> replacementOrdinalIndex = replacement.Geometry.TopLevelOrdinalByBlockIndex;
        Assert.Equal(
            Assert.IsType<BlockOrdinalIndex>(sourceOrdinalIndex).DenseGeneratedUpperBound,
            Assert.IsType<BlockOrdinalIndex>(replacementOrdinalIndex).DenseGeneratedUpperBound);
        Assert.Equal(
            sourceOrdinalIndex.OrderBy(static pair => pair.Key),
            replacementOrdinalIndex.OrderBy(static pair => pair.Key));
        Assert.Equal(2_407, replacement.AutomationDocumentGeneration);
        Assert.Same(source.SourceMap, replacement.SourceMap);
        Assert.Equal(sourceSemantic.TextProjectionFingerprint, replacement.SemanticDocument.TextProjectionFingerprint);
        Assert.Equal(sourceSemantic.SemanticRangeFingerprint, replacement.SemanticDocument.SemanticRangeFingerprint);
        Assert.Same(sourceSemantic.Text, replacement.SemanticDocument.Text);
        Assert.Same(sourceSemantic.TextElementBoundaries, replacement.SemanticDocument.TextElementBoundaries);
        Assert.Equal(source.LayoutRevision + 1, replacement.LayoutRevision);
    }

    internal static void AssertReplacementImageReusesSourceRaster(
        ImageBox sourceImage,
        ImageBox replacementImage,
        float rasterizationScale)
    {
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
    }

    internal static void AssertClonedRootsKeepMeasurementState(
        SingleRasterLayoutCandidate candidate,
        ImageBox sourceImage,
        ImageBox replacementImage,
        InlineContainerBox[] sourceRoots,
        InlineContainerBox[] replacementRoots)
    {
        var imagePlan = new RasterPlacementImagePlan(candidate.Snapshot.Blocks, candidate.SourceImageCount);
        Assert.Same(replacementImage, Assert.Single(imagePlan.Images));
        Assert.True(candidate.MatchesSourceImages([sourceImage], new HashSet<ImageBox> { sourceImage }));
        Assert.All(sourceRoots.Zip(replacementRoots), pair => Assert.NotSame(pair.First, pair.Second));
        Assert.True(replacementRoots[0].HasMeasuredLayout);
        Assert.False(replacementRoots[^1].HasMeasuredLayout);
        AssertHasEveryRichInlineRun(replacementRoots[0]);
        Assert.Contains(replacementRoots[^1].Runs, static run => run is CodeInlineRun);
        Assert.Contains(replacementRoots[^1].Runs, static run => run is LineBreakRun);
    }

    internal static void AssertReplacementMeasuresFarRootPrivately(
        LayoutSnapshot replacement,
        InlineContainerBox replacementFarRoot)
    {
        int replacementMeasuredCount = replacement.MeasuredTopLevelBlockCount;
        LazyLayoutCommit farCommit = replacement.EnsureMeasuredViewport(
            replacementFarRoot.Bounds.Top,
            System.Math.Max(48, replacementFarRoot.Bounds.Height),
            overscan: 0,
            CancellationToken.None);
        Assert.True(farCommit.Changed);
        Assert.True(replacementFarRoot.HasMeasuredLayout);
        Assert.True(replacement.MeasuredTopLevelBlockCount > replacementMeasuredCount);
    }

    internal static async Task DisposeCandidateAndRetireSourceAsync(
        SingleRasterLayoutCandidate? candidate,
        MarkdownLayoutContext? candidateContext,
        LayoutSnapshot? source,
        MarkdownLayoutContext sourceContext)
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

    internal static async Task<ProgressiveSource> CompleteProgressiveSourceImageAsync(
        LayoutSnapshot source,
        PrivatePlacementRasterResolver resolver)
    {
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
            () => sourceImage.GetWorkState().ActiveWorkCount == 0);
        Assert.NotNull(sourceImage.Bitmap);
        Assert.Equal(1, resolver.ResolveCount);
        Assert.Equal(imageBoundsBeforeCompletion, sourceImage.Bounds);

        InlineContainerBox measuredTextRoot = sourceRoots[0];
        Assert.True(measuredTextRoot.HasMeasuredLayout);
        AssertHasEveryRichInlineRun(measuredTextRoot);
        InlineContainerBox sourceFarRoot = sourceRoots[^1];
        Assert.False(sourceFarRoot.HasMeasuredLayout);
        AssertHasEveryRichInlineRun(sourceFarRoot);
        return new ProgressiveSource(sourceImage, sourceRoots, imageBoundsBeforeCompletion, sourceFarBounds);
    }

    /// <summary>
    /// The candidate owns only its private measurements. The source's far
    /// paragraph remains an estimated, unmeasured root.
    /// </summary>
    internal static void AssertSourceKeptOnlyItsOwnMeasurements(
        LayoutSnapshot source,
        ProgressiveSource completed,
        long sourceRevision,
        int sourceMeasuredCount,
        PrivatePlacementRasterResolver resolver,
        ImageBox replacementImage)
    {
        InlineContainerBox sourceFarRoot = completed.Roots[^1];
        Assert.Equal(sourceRevision, source.LayoutRevision);
        Assert.Equal(sourceMeasuredCount, source.MeasuredTopLevelBlockCount);
        Assert.False(sourceFarRoot.HasMeasuredLayout);
        Assert.Equal(completed.FarRootBounds, sourceFarRoot.Bounds);
        Assert.Equal(completed.ImageBoundsBeforeCompletion, completed.Image.Bounds);
        Assert.Equal(1, resolver.ResolveCount);
        Assert.NotNull(completed.Image.Bitmap);
        Assert.NotNull(replacementImage.Bitmap);
    }


    internal static void PaintAtDeviceDpi(CanvasDevice device, BlockBox box)
    {
        using var target = new CanvasRenderTarget(device, Width, 180, 96);
        using CanvasDrawingSession drawing = target.CreateDrawingSession();
        box.Paint(drawing, new Rect(0, 0, Width, 180));
    }

    /// <summary>The lazy source after its front image completed, with the geometry it must keep.</summary>
    internal sealed record ProgressiveSource(
        ImageBox Image,
        InlineContainerBox[] Roots,
        Rect ImageBoundsBeforeCompletion,
        Rect FarRootBounds);
}
