using MarkdownRenderer.Document;
using MarkdownRenderer.Extensions;
using MarkdownRenderer.GitHub.Tests.Layout.Internal.Inlines.Placement;
using MarkdownRenderer.Layout.Internal.Building;
using MarkdownRenderer.Layout.Internal.Context;
using MarkdownRenderer.Layout.Internal.Snapshots;
using MarkdownRenderer.Math;
using MarkdownRenderer.Parsing.Internal;
using MarkdownRenderer.Performance;
using Microsoft.UI.Xaml;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests.Layout.Internal.Relayout.Tables;

/// <summary>
/// A laid-out GFM document with mathematics for the root-local table vector
/// tests. It owns its engine, layout context and snapshot and retires all
/// three on disposal.
/// </summary>
internal sealed class RootLocalTableVectorDocument : IAsyncDisposable
{
    internal const float InitialWidth = 640f;
    internal const long AutomationGeneration = 84_221;

    private RootLocalTableVectorDocument(
        MarkdownLayoutContext context,
        LayoutSnapshot snapshot,
        MarkdownEngine engine)
    {
        Context = context;
        Snapshot = snapshot;
        Engine = engine;
    }

    internal MarkdownLayoutContext Context { get; }
    internal LayoutSnapshot Snapshot { get; }
    private MarkdownEngine Engine { get; }

    internal static async Task<RootLocalTableVectorDocument> BuildAsync(
        CompletedRasterPlacementCloneTests.PlacementFixture seed,
        MarkdownPerformanceSession session,
        RootLocalTableVectorResolver resolver,
        string source,
        FlowDirection direction,
        float rasterizationScale,
        float width = InitialWidth)
    {
        MarkdownEngine engine = new MarkdownEngineBuilder()
            .UseProfile(MarkdownProfiles.GfmStrict)
            .UseMathematics()
            .WithParseCacheBudgetBytes(0)
            .Build();
        LayoutSnapshot? snapshot = null;
        MarkdownLayoutContext? context = null;
        try
        {
            MarkdownDocument markdown = Assert.IsType<MarkdownDocument>(await engine.ParseAsync(source));
            var registry = new MarkdownExtensionRegistry().ConfigureGitHubReadmeRegistry();
            var sourceMap = new MarkdownSourceMap(markdown.Source);
            context = new MarkdownLayoutContext(
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
                AllowThirdPartyRemoteImages = true,
                RasterizationScale = rasterizationScale,
            };
            snapshot = new LayoutBuilder(context, semanticDocument: markdown)
                .Build(markdown.ParsedDocument!, width, CancellationToken.None);
            snapshot.AssignAutomationDocumentGeneration(AutomationGeneration);
            var result = new RootLocalTableVectorDocument(context, snapshot, engine);
            snapshot = null;
            context = null;
            engine = null!;
            return result;
        }
        catch
        {
            try
            {
                if (snapshot is not null)
                    await snapshot.Retire().ConfigureAwait(false);
            }
            finally
            {
                if (context is not null)
                    await context.DrainOwnedRetirementsAsync().ConfigureAwait(false);
                engine.Dispose();
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
            try
            {
                await Context.DrainOwnedRetirementsAsync().WaitAsync(TimeSpan.FromSeconds(10));
            }
            finally
            {
                Engine.Dispose();
            }
        }
    }
}
