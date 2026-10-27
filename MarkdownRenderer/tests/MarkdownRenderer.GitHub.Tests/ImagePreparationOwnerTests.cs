using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

[Collection(SharedCanvasBitmapCacheTestCollection.Name)]
public sealed class ImagePreparationOwnerTests
{
    private const float Width = 320f;

    [Fact]
    public async Task CandidateContextSeparatesSceneOwnerFromImagePreparationOwner()
    {
        await using var fixture = new CompletedRasterPlacementCloneTests.PlacementFixture();
        using LayoutSnapshot source = CreateSourceSnapshot(fixture);
        object originalDocumentOwner = fixture.PerformanceDocumentOwner;
        object imagePreparationOwner = fixture.ImagePreparationOwnerToken;

        (SingleRasterLayoutCandidate candidate, MarkdownLayoutContext constructionContext) =
            CreateCandidate(source, fixture);

        using (candidate)
        {
            Assert.Same(originalDocumentOwner, fixture.SourceContext.PerformanceDocumentOwner);
            Assert.Null(constructionContext.PerformanceDocumentOwner);
            Assert.Same(imagePreparationOwner, constructionContext.ImagePreparationOwner);

            InlineContainerBox root = Assert.IsType<InlineContainerBox>(
                Assert.Single(candidate.Snapshot.Blocks));
            Assert.Same(constructionContext, root.Context);
            InlineImageRun imageRun = Assert.IsType<InlineImageRun>(
                Assert.Single(root.Runs.OfType<InlineImageRun>()));
            Assert.Null(imageRun.Image.Context.PerformanceDocumentOwner);
            Assert.Same(imagePreparationOwner, imageRun.Image.Context.ImagePreparationOwner);
            Assert.True(candidate.HasCurrentSourceRaster());
        }

        await constructionContext.DrainOwnedRetirementsAsync();
    }

    [Fact]
    public async Task CandidatePreservesImmutableImagePolicyAndLeavesOriginalOwnerUntouched()
    {
        await using var fixture = new CompletedRasterPlacementCloneTests.PlacementFixture();
        using LayoutSnapshot source = CreateSourceSnapshot(fixture);
        MarkdownLayoutContext original = fixture.SourceContext;

        (SingleRasterLayoutCandidate candidate, MarkdownLayoutContext constructionContext) =
            CreateCandidate(source, fixture);

        using (candidate)
        {
            Assert.Same(original.ResourceCreator, constructionContext.ResourceCreator);
            Assert.Same(original.ThemeSnapshot, constructionContext.ThemeSnapshot);
            Assert.Same(original.SourceMap, constructionContext.SourceMap);
            Assert.Same(original.Registry, constructionContext.Registry);
            Assert.Same(original.Dispatcher, constructionContext.Dispatcher);
            Assert.Same(original.ImageResolver, constructionContext.ImageResolver);
            Assert.Same(original.SvgRenderer, constructionContext.SvgRenderer);
            Assert.Same(original.PerformanceSession, constructionContext.PerformanceSession);
            Assert.Same(original.ImageDocumentSource, constructionContext.ImageDocumentSource);
            Assert.Same(original.PerformanceDocumentOwner, fixture.PerformanceDocumentOwner);
            Assert.Same(original.ImagePreparationOwner, constructionContext.ImagePreparationOwner);
            Assert.Null(constructionContext.PerformanceDocumentOwner);
        }

        await constructionContext.DrainOwnedRetirementsAsync();
    }

    [Fact]
    public async Task LegacyImagePreparationOwnerFallsBackToPerformanceDocumentOwner()
    {
        await using var fixture = new CompletedRasterPlacementCloneTests.PlacementFixture();
        MarkdownLayoutContext legacy = fixture.CreateContext(includeImagePreparationOwner: false);

        Assert.Null(legacy.ImagePreparationOwner);
        Assert.Same(fixture.PerformanceDocumentOwner, legacy.EffectiveImagePreparationOwner);

        await legacy.DrainOwnedRetirementsAsync();
    }

    private static LayoutSnapshot CreateSourceSnapshot(
        CompletedRasterPlacementCloneTests.PlacementFixture fixture)
    {
        var snapshot = new LayoutSnapshot(
            new BlockBox[] { fixture.Source },
            fixture.SourceContext.SourceMap,
            Width,
            (float)fixture.Source.Bounds.Height);
        fixture.TransferSourceOwnershipToSnapshot(snapshot);
        return snapshot;
    }

    private static (SingleRasterLayoutCandidate Candidate, MarkdownLayoutContext ConstructionContext)
        CreateCandidate(
            LayoutSnapshot source,
            CompletedRasterPlacementCloneTests.PlacementFixture fixture)
    {
        SingleRasterLayoutCandidate? candidate = source.CreateSingleRasterCandidate(
            new[] { fixture.Source.BlockIndex },
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
