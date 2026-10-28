using MarkdownRenderer.Document;
using MarkdownRenderer.GitHub;
using MarkdownRenderer.Images;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Parsing;
using MarkdownRenderer.Performance;
using Markdig;
using Microsoft.UI.Xaml;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

[Collection(SharedCanvasBitmapCacheTestCollection.Name)]
public sealed class LazyIdleImageLayoutCandidateTests
{
    [Theory]
    [InlineData(false, 1d, false)]
    [InlineData(true, 1.5d, true)]
    [InlineData(false, 2d, true)]
    [InlineData(true, 4d, false)]
    public async Task ActualLazyDocumentCarriesOffscreenIdleImageWithoutRealizingOrResolvingIt(
        bool inlineImage, double scale, bool rightToLeft)
    {
        await using var seed = new CompletedRasterPlacementCloneTests.PlacementFixture();
        using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        var resolver = new RasterResolver(DisplaySizedRasterDecodeTests.CreatePng(400, 200));
        string markdown = "![front alt](https://images.example/front.png)\n\n" +
            string.Join("\n\n", Enumerable.Range(0, 12).Select(index => $"Paragraph {index} with **bold** and `code`.")) +
            "\n\n" + (inlineImage
                ? "Far [![tail alt](https://images.example/tail.png)](https://example.test/target) image."
                : "![tail alt](https://images.example/tail.png)");
        var registry = new MarkdownExtensionRegistry().ConfigureGitHubReadmeRegistry();
        var parsed = Markdown.Parse(markdown, registry.BuildPipeline());
        var sourceMap = new MarkdownSourceMap(markdown);
        var context = new MarkdownLayoutContext(seed.Device, seed.SourceContext.ThemeSnapshot, sourceMap,
            registry, rightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight, language: "en-US")
        {
            ImagePreparationOwner = seed.ImagePreparationOwnerToken,
            PerformanceDocumentOwner = new object(),
            PerformanceSession = session,
            ImageResolver = resolver,
            AllowThirdPartyRemoteImages = true,
            RasterizationScale = scale,
        };
        LayoutSnapshot? source = null;
        SingleRasterLayoutCandidate? candidate = null;
        MarkdownLayoutContext? candidateContext = null;
        try
        {
            source = new LayoutBuilder(context, semanticDocument: MarkdownDocument.FromParsed(markdown, parsed))
                .BuildLazy(parsed, 320, 0, 120, 0, CancellationToken.None);
            source.AssignAutomationDocumentGeneration(4_301);
            Assert.Null(source.VectorSceneResidencyPolicy);
            ImageBox front = Assert.IsType<ImageBox>(source.Blocks[0]);
            BlockBox tailRoot = source.Blocks[^1];
            ImageBox tail = GetImage(tailRoot);
            Assert.False(source.IsTopLevelBlockMeasured(tailRoot));
            Assert.True(tail.IsCurrentIdleImagePlacement(0));
            var tailBounds = tailRoot.Bounds;
            int measuredCount = source.MeasuredTopLevelBlockCount;
            await DisplaySizedRasterDecodeTests.WaitForLoadAsync(front);
            await DisplaySizedRasterDecodeTests.WaitUntilAsync(() => front.ActiveImageWorkCountForTests == 0);
            Assert.Equal(1, resolver.ResolveCount);

            candidate = source.CreateSingleRasterCandidate([front.BlockIndex], 320, CancellationToken.None,
                CancellationToken.None, seed.ImagePreparationOwnerToken, null, null, out candidateContext);
            SingleRasterLayoutCandidate owner = Assert.IsType<SingleRasterLayoutCandidate>(candidate);
            await owner.DrainConstructionAsync().WaitAsync(TimeSpan.FromSeconds(10));
            LayoutSnapshot replacement = owner.Snapshot;
            BlockBox privateTailRoot = replacement.Blocks[^1];
            ImageBox privateTail = GetImage(privateTailRoot);
            Assert.Equal(2, owner.SourceImageCount);
            Assert.Equal(1, owner.SourceSubscribedImageCount);
            Assert.True(owner.MatchesSourceImages([front], new HashSet<ImageBox> { front }));
            Assert.False(owner.MatchesSourceImages([front, tail], new HashSet<ImageBox> { front, tail }));
            Assert.True(owner.HasCurrentSourceRaster());
            Assert.NotSame(tail, privateTail);
            Assert.True(privateTail.IsCurrentIdleImagePlacement(0));
            Assert.Equal(measuredCount, replacement.MeasuredTopLevelBlockCount);
            Assert.False(replacement.IsTopLevelBlockMeasured(privateTailRoot));
            Assert.Equal(tailBounds.Height, privateTailRoot.Bounds.Height);
            Assert.Equal(0, privateTail.ActiveImageWorkCountForTests);
            Assert.Null(privateTail.PlaceholderLayoutForTests);
            if (privateTailRoot is InlineContainerBox privateInline)
                Assert.False(privateInline.HasMeasuredLayout);
            Assert.Same(sourceMap, replacement.SourceMap);
            Assert.Same(source.SemanticDocument.Text, replacement.SemanticDocument.Text);
            Assert.Equal(source.SemanticDocument.SemanticRangeFingerprint, replacement.SemanticDocument.SemanticRangeFingerprint);
            Assert.Equal(4_301, replacement.AutomationDocumentGeneration);
            var imagePlan = new RasterPlacementImagePlan(replacement.GetMeasuredTopLevelBlocks(), owner.SourceSubscribedImageCount);
            Assert.Single(imagePlan.Images);
            Assert.DoesNotContain(privateTail, imagePlan.Images);
            Assert.Equal(1, resolver.ResolveCount);

            // Source retirement cannot cancel a descriptor's new lifetime.
            await source.Retire().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(privateTail.IsCurrentIdleImagePlacement(0));
            LazyLayoutCommit commit = replacement.EnsureMeasuredViewport(privateTailRoot.Bounds.Top,
                System.Math.Max(120, privateTailRoot.Bounds.Height), 0, CancellationToken.None);
            Assert.True(commit.Changed);
            Assert.True(replacement.IsTopLevelBlockMeasured(privateTailRoot));
            Assert.Equal(1, resolver.ResolveCount);
            Assert.Equal(tailBounds, tailRoot.Bounds);
            await DisplaySizedRasterDecodeTests.WaitForLoadAsync(privateTail);
            await DisplaySizedRasterDecodeTests.WaitUntilAsync(() => privateTail.ActiveImageWorkCountForTests == 0);
            Assert.NotNull(privateTail.Bitmap);
            Assert.Equal(2, resolver.ResolveCount);
            Assert.Null(tail.Bitmap);
        }
        finally
        {
            try { candidate?.Dispose(); }
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
            finally { await context.DrainOwnedRetirementsAsync().WaitAsync(TimeSpan.FromSeconds(10)); }
        }
    }

    private static ImageBox GetImage(BlockBox root) => root switch
    {
        ImageBox image => image,
        InlineContainerBox inline => Assert.Single(inline.InlineImageRuns).Run.Image,
        _ => throw new InvalidOperationException("The fixture image has an unexpected layout owner."),
    };

    private sealed class RasterResolver(byte[] png) : IMarkdownImageResolver
    {
        private readonly string _cachePartition = Guid.NewGuid().ToString("N");
        private int _resolveCount;
        internal int ResolveCount => Volatile.Read(ref _resolveCount);

        public ValueTask<MarkdownImageResolution> ResolveAsync(string source,
            MarkdownImageResolveContext context, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _resolveCount);
            return ValueTask.FromResult(MarkdownImageResolution.Resolved(
                new MarkdownImageAsset(png, "image/png", CacheKey: _cachePartition + source)));
        }
    }
}
