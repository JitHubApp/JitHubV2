using MarkdownRenderer.Document;
using MarkdownRenderer.Extensions;
using MarkdownRenderer.Images;
using MarkdownRenderer.Layout.Internal.Building;
using MarkdownRenderer.Layout.Internal.Context;
using MarkdownRenderer.Layout.Internal.Snapshots;
using MarkdownRenderer.Parsing.Internal;
using MarkdownRenderer.Performance;
using Markdig;
using Microsoft.UI.Xaml;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>
/// A laid-out GitHub README document for the root-local table relayout tests.
/// It owns the layout context and snapshot it builds and retires both on
/// disposal.
/// </summary>
internal sealed class RootLocalTableDocument : IAsyncDisposable
{
    internal const float DefaultLayoutWidth = 320f;
    internal const long AutomationGeneration = 8_422;

    private RootLocalTableDocument(
        MarkdownLayoutContext context,
        LayoutSnapshot snapshot,
        MarkdownSourceMap sourceMap,
        IMarkdownImageResolver resolver)
    {
        Context = context;
        Snapshot = snapshot;
        SourceMap = sourceMap;
        Resolver = resolver;
    }

    internal MarkdownLayoutContext Context { get; }
    internal LayoutSnapshot Snapshot { get; }
    internal MarkdownSourceMap SourceMap { get; }
    internal IMarkdownImageResolver Resolver { get; }

    internal static RootLocalTableDocument Build(
        CompletedRasterPlacementCloneTests.PlacementFixture seed,
        MarkdownPerformanceSession session,
        IMarkdownImageResolver resolver,
        string sourceText,
        FlowDirection direction,
        float rasterizationScale,
        bool lazy,
        float lazyViewportHeight = 120,
        float layoutWidth = DefaultLayoutWidth,
        IMarkdownSvgRenderer? svgRenderer = null)
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
            RenderResources = seed.RenderResources,
            CancellationToken = CancellationToken.None,
            ImageCancellationToken = CancellationToken.None,
            ImagePreparationOwner = seed.ImagePreparationOwnerToken,
            PerformanceDocumentOwner = new object(),
            PerformanceSession = session,
            ImageResolver = resolver,
            SvgRenderer = svgRenderer,
            AllowThirdPartyRemoteImages = true,
            RasterizationScale = rasterizationScale,
        };

        LayoutSnapshot? snapshot = null;
        try
        {
            var builder = new LayoutBuilder(context, semanticDocument: semanticDocument);
            snapshot = lazy
                ? builder.BuildLazy(parsed, layoutWidth, viewportTop: 0,
                    viewportHeight: lazyViewportHeight, overscan: 0, CancellationToken.None)
                : builder.Build(parsed, layoutWidth, CancellationToken.None);
            snapshot.AssignAutomationDocumentGeneration(AutomationGeneration);
            var result = new RootLocalTableDocument(context, snapshot, sourceMap, resolver);
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
