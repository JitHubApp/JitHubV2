using MarkdownRenderer.Document;
using MarkdownRenderer.Extensions;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Parsing;
using MarkdownRenderer.Performance;
using MarkdownRenderer.Theming;
using Markdig;
using Microsoft.UI.Xaml;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>
/// A laid-out GitHub README snapshot for the standalone raster root relayout
/// tests, with its target, optional pending sibling and optional far image.
/// Disposal retires the snapshot and drains the layout context it owns.
/// </summary>
internal sealed class RootLocalImageDocument : IAsyncDisposable
{
    internal const float LayoutWidth = 320f;
    internal const long AutomationGeneration = 4_731;

    private RootLocalImageDocument(
        MarkdownLayoutContext context,
        LayoutSnapshot snapshot,
        MarkdownSourceMap sourceMap,
        ControlledRasterResolver resolver,
        ImageBox targetImage,
        ImageBox? pendingImage,
        ImageBox? farImage)
    {
        Context = context;
        Snapshot = snapshot;
        SourceMap = sourceMap;
        Resolver = resolver;
        TargetImage = targetImage;
        PendingImage = pendingImage;
        FarImage = farImage;
    }

    internal MarkdownLayoutContext Context { get; }
    internal LayoutSnapshot Snapshot { get; }
    internal MarkdownSourceMap SourceMap { get; }
    internal ControlledRasterResolver Resolver { get; }
    internal ImageBox TargetImage { get; }
    internal ImageBox? PendingImage { get; }
    internal ImageBox? FarImage { get; }

    internal static RootLocalImageDocument Build(
        MarkdownRenderResources renderResources,
        CompletedRasterPlacementCloneTests.PlacementFixture seed,
        MarkdownPerformanceSession session,
        ControlledRasterResolver resolver,
        string sourceText,
        FlowDirection direction,
        float rasterizationScale,
        bool lazy,
        float lazyViewportHeight = 120)
    {
        var registry = new MarkdownExtensionRegistry().ConfigureGitHubReadmeRegistry();
        Markdig.Syntax.MarkdownDocument parsed = Markdown.Parse(sourceText, registry.BuildPipeline());
        MarkdownDocument semanticDocument = MarkdownDocument.FromParsed(sourceText, parsed);
        var sourceMap = new MarkdownSourceMap(sourceText);
        var context = new MarkdownLayoutContext(
            seed.Device,
            seed.SourceContext.ThemeSnapshot,
            sourceMap,
            registry,
            direction,
            language: "en-US")
        {
            RenderResources = renderResources,
            CancellationToken = CancellationToken.None,
            ImageCancellationToken = CancellationToken.None,
            ImagePreparationOwner = seed.ImagePreparationOwnerToken,
            PerformanceDocumentOwner = new object(),
            PerformanceSession = session,
            ImageResolver = resolver,
            AllowThirdPartyRemoteImages = true,
            RasterizationScale = rasterizationScale,
        };

        LayoutSnapshot? snapshot = null;
        try
        {
            var builder = new LayoutBuilder(context, semanticDocument: semanticDocument);
            snapshot = lazy
                ? builder.BuildLazy(parsed, LayoutWidth, viewportTop: 0, viewportHeight: lazyViewportHeight,
                    overscan: 0, CancellationToken.None)
                : builder.Build(parsed, LayoutWidth, CancellationToken.None);
            snapshot.AssignAutomationDocumentGeneration(AutomationGeneration);
            ImageBox target = Assert.Single(snapshot.Blocks.OfType<ImageBox>(),
                image => image.Url == resolver.TargetUrl);
            ImageBox? pending = snapshot.Blocks.OfType<ImageBox>()
                .SingleOrDefault(image => image.Url == resolver.PendingUrl);
            ImageBox? far = snapshot.Blocks.OfType<ImageBox>()
                .SingleOrDefault(image => image.Url == resolver.FarUrl);
            var result = new RootLocalImageDocument(context, snapshot, sourceMap, resolver, target, pending, far);
            snapshot = null;
            return result;
        }
        catch
        {
            try
            {
                if (snapshot is not null)
                    snapshot.Retire().GetAwaiter().GetResult();
            }
            finally
            {
                context.DrainOwnedRetirementsAsync().GetAwaiter().GetResult();
            }
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await Snapshot.Retire().WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            await Context.DrainOwnedRetirementsAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }
    }
}
