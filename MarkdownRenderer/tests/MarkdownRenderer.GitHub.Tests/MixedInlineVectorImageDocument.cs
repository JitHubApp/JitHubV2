using MarkdownRenderer.Document;
using MarkdownRenderer.Extensions;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Math;
using MarkdownRenderer.Parsing.Internal;
using MarkdownRenderer.Performance;
using Microsoft.UI.Xaml;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>
/// A laid-out math-enabled snapshot for the mixed inline vector and image
/// relayout tests, built either eagerly or through the progressive deferred
/// scene path. Disposal retires the snapshot, drains its layout context,
/// releases the deferred scene document and disposes the engine.
/// </summary>
internal sealed class MixedInlineVectorImageDocument : IAsyncDisposable
{
    internal const float LayoutWidth = 320f;

    private MixedInlineVectorImageDocument(
        MarkdownLayoutContext context,
        LayoutSnapshot snapshot,
        MarkdownSourceMap sourceMap,
        InlineVectorRasterResolver resolver,
        MarkdownEngine engine,
        object? performanceOwner)
    {
        Context = context;
        Snapshot = snapshot;
        SourceMap = sourceMap;
        Resolver = resolver;
        Engine = engine;
        PerformanceOwner = performanceOwner;
    }

    internal MarkdownLayoutContext Context { get; }
    internal LayoutSnapshot Snapshot { get; }
    internal MarkdownSourceMap SourceMap { get; }
    internal InlineVectorRasterResolver Resolver { get; }
    private MarkdownEngine Engine { get; }
    internal object? PerformanceOwner { get; }

    internal static async Task<MixedInlineVectorImageDocument> BuildAsync(
        CompletedRasterPlacementCloneTests.PlacementFixture seed,
        MarkdownPerformanceSession session,
        InlineVectorRasterResolver resolver,
        string source,
        FlowDirection direction,
        float rasterizationScale)
    {
        MarkdownEngine engine = CreateMathEngine();
        try
        {
            MarkdownDocument markdown = await engine.ParseAsync(source);
            return BuildSnapshot(seed, session, resolver, markdown, engine, direction, rasterizationScale, lazy: false);
        }
        catch
        {
            engine.Dispose();
            throw;
        }
    }

    internal static async Task<MixedInlineVectorImageDocument> BuildProgressiveLazyAsync(
        CompletedRasterPlacementCloneTests.PlacementFixture seed,
        MarkdownPerformanceSession session,
        InlineVectorRasterResolver resolver,
        string source,
        FlowDirection direction,
        float rasterizationScale,
        bool materializeAllInlineMarkers = false)
    {
        MarkdownEngine engine = CreateMathEngine();
        object owner = new();
        var registry = new MarkdownExtensionRegistry().ConfigureGitHubReadmeRegistry();
        try
        {
            MarkdownDocument markdown = Assert.IsType<MarkdownDocument>(
                await ((IMarkdownPerformanceSessionInternal)session).ParseAndPrepareDocumentAsync(
                    engine, null, source, registry, owner, CancellationToken.None));
            string[] allInlineMarkers = GetInlineMarkers(markdown);
            string[] markersToPublish = materializeAllInlineMarkers
                ? allInlineMarkers
                : [Assert.Single(allInlineMarkers)];
            MarkdownProgressiveScenePlan plan = MixedInlineVectorImageRelayoutFixture.GetRegisteredPlan(session, owner);
            plan.UpdateVisibleMarkers(markersToPublish);
            foreach (string marker in markersToPublish)
                await MixedInlineVectorImageRelayoutFixture.PublishAsync(plan, marker);
            return BuildSnapshot(seed, session, resolver, markdown, engine, direction, rasterizationScale,
                lazy: true, performanceOwner: owner, registry: registry);
        }
        catch
        {
            engine.Dispose();
            await ((IMarkdownPerformanceSessionInternal)session).ReleaseDeferredSceneDocument(owner);
            throw;
        }
    }

    private static MarkdownEngine CreateMathEngine() => new MarkdownEngineBuilder()
        .UseProfile(MarkdownProfiles.GfmStrict)
        .UseMathematics()
        .WithParseCacheBudgetBytes(0)
        .Build();

    private static MixedInlineVectorImageDocument BuildSnapshot(
        CompletedRasterPlacementCloneTests.PlacementFixture seed,
        MarkdownPerformanceSession session,
        InlineVectorRasterResolver resolver,
        MarkdownDocument markdown,
        MarkdownEngine engine,
        FlowDirection direction,
        float rasterizationScale,
        bool lazy,
        object? performanceOwner = null,
        MarkdownExtensionRegistry? registry = null)
    {
        registry ??= new MarkdownExtensionRegistry().ConfigureGitHubReadmeRegistry();
        var sourceMap = new MarkdownSourceMap(markdown.Source);
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
            PerformanceDocumentOwner = performanceOwner ?? new object(),
            PerformanceSession = session,
            ImageResolver = resolver,
            AllowThirdPartyRemoteImages = true,
            RasterizationScale = rasterizationScale,
        };

        LayoutSnapshot? snapshot = null;
        try
        {
            var builder = new LayoutBuilder(context, semanticDocument: markdown);
            snapshot = lazy
                ? builder.BuildLazy(markdown.ParsedDocument!, LayoutWidth, viewportTop: 0,
                    viewportHeight: 1024, overscan: 0, CancellationToken.None)
                : builder.Build(markdown.ParsedDocument!, LayoutWidth, CancellationToken.None);
            snapshot.AssignAutomationDocumentGeneration(42_081);
            var result = new MixedInlineVectorImageDocument(context, snapshot, sourceMap, resolver, engine, performanceOwner);
            snapshot = null;
            return result;
        }
        catch
        {
            if (snapshot is not null)
                snapshot.Retire().GetAwaiter().GetResult();
            context.DrainOwnedRetirementsAsync().GetAwaiter().GetResult();
            throw;
        }
    }

    private static string[] GetInlineMarkers(MarkdownDocument document) =>
        document.ExtensionInlineNodeContent.Values
            .SelectMany(static fragment => fragment.Items)
        .Where(static content => content.Attributes.ContainsKey(MarkdownContentAttributes.DeferredScene))
        .Select(static content => Assert.IsType<string>(content.Attributes[MarkdownContentAttributes.DeferredScene]))
        .ToArray();

    public async ValueTask DisposeAsync()
    {
        try
        {
            await Snapshot.Retire().WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            await Context.DrainOwnedRetirementsAsync().WaitAsync(TimeSpan.FromSeconds(10));
            if (PerformanceOwner is not null)
                await ((IMarkdownPerformanceSessionInternal)Context.PerformanceSession!)
                    .ReleaseDeferredSceneDocument(PerformanceOwner);
            Engine.Dispose();
        }
    }
}
