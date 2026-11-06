using System.Reflection;
using MarkdownRenderer.Accessibility;
using MarkdownRenderer.Document;
using MarkdownRenderer.Extensions;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Math;
using MarkdownRenderer.Parsing;
using MarkdownRenderer.Performance;
using MarkdownRenderer.Selection;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Xunit;
using static MarkdownRenderer.GitHub.Tests.ContainerRelayoutTestTree;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>
/// Document construction, Markdown sources and vector, image and semantic
/// geometry capture for <see cref="RootLocalContainerVectorImageRelayoutTests"/>.
/// </summary>
internal static class RootLocalContainerVectorRelayoutHarness
{
    internal const float LayoutWidth = 640f;

    internal static string CreateListAndQuoteSource(ContainerVectorResolver resolver) =>
        $"- outer ![outer]({resolver.GetUrl(0)}) with $x^2$\n" +
        $"  - nested ![nested]({resolver.GetUrl(1)}) with $\\frac{{1}}{{2}}$\n\n" +
        $"> quote ![quote]({resolver.GetUrl(2)}) with $\\alpha + 1$\n\n" +
        "Trailing text stays outside the changed container roots.\n";

    internal static async Task<ContainerVectorDocument> BuildDocumentAsync(
        CompletedRasterPlacementCloneTests.PlacementFixture seed,
        MarkdownPerformanceSession session,
        ContainerVectorResolver resolver,
        string source,
        FlowDirection direction,
        float rasterizationScale)
    {
        MarkdownEngine engine = new MarkdownEngineBuilder()
            .UseProfile(MarkdownProfiles.GfmStrict)
            .UseMathematics()
            .WithParseCacheBudgetBytes(0)
            .Build();
        try
        {
            MarkdownDocument markdown = await engine.ParseAsync(source);
            var registry = new MarkdownExtensionRegistry().ConfigureGitHubReadmeRegistry();
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
                PerformanceDocumentOwner = new object(),
                PerformanceSession = session,
                ImageResolver = resolver,
                AllowThirdPartyRemoteImages = true,
                RasterizationScale = rasterizationScale,
            };

            LayoutSnapshot? snapshot = null;
            try
            {
                snapshot = new LayoutBuilder(context, semanticDocument: markdown)
                    .Build(markdown.ParsedDocument!, LayoutWidth, CancellationToken.None);
                snapshot.AssignAutomationDocumentGeneration(92_146);
                var fixture = new ContainerVectorDocument(context, snapshot, engine);
                snapshot = null;
                engine = null!;
                return fixture;
            }
            catch
            {
                if (snapshot is not null)
                    await snapshot.Retire();
                await context.DrainOwnedRetirementsAsync();
                throw;
            }
        }
        catch
        {
            engine?.Dispose();
            throw;
        }
    }

    internal static Rect[][] CaptureVectorSelectionGeometry(
        LayoutSnapshot snapshot,
        IReadOnlyList<InlineContainerBox> inlines) =>
        inlines.SelectMany(inline => inline.Runs
            .Select((run, index) => (run, index))
            .Where(static item => item.run is InlineVectorSceneRun)
            .Select(item => SelectionFor(snapshot, inline, item.index, item.run)))
            .ToArray();

    private static Rect[] SelectionFor(
        LayoutSnapshot snapshot,
        InlineContainerBox inline,
        int runIndex,
        InlineRun run)
    {
        var range = new DocumentRange(
            new DocumentPosition(inline.BlockIndex, runIndex, 0),
            new DocumentPosition(inline.BlockIndex, runIndex, run.RenderedLength));
        var selection = new SelectionController();
        selection.SetAnchor(range.Start);
        selection.ExtendTo(range.End);
        return selection.GetHighlightRects(snapshot).ToArray();
    }

    internal static InlineImagePlacementTrace CaptureInlineImagePlacement(LayoutSnapshot snapshot, ImageBox image)
    {
        InlineContainerBox? inlineOwner = EnumerateBoxes(snapshot.Blocks)
            .OfType<InlineContainerBox>()
            .FirstOrDefault(inline => inline.InlineImageRuns.Any(entry => ReferenceEquals(entry.Run.Image, image)));
        if (inlineOwner is null)
        {
            BlockBox topLevelOwner = Assert.Single(snapshot.Blocks, block => ReferenceEquals(block, image));
            if (!snapshot.TryBeginInteraction())
                throw new InvalidOperationException("Could not acquire a layout read lease.");
            try
            {
                using LayoutPlacementTransformContext.Scope placementScope =
                    snapshot.EnterPlacementTransformScopeForTopLevelBlockUnderPaint(topLevelOwner);
                snapshot.PlacementTransform.GetCurrentTranslation(out _, out float translationY);
                Rect logicalBounds = snapshot.ToLogicalRectForTopLevelBlockUnderPaint(topLevelOwner, image.Bounds);
                return new InlineImagePlacementTrace(
                    logicalBounds, image.Bounds, topLevelOwner.Bounds, translationY, "standalone");
            }
            finally
            {
                snapshot.EndInteraction();
            }
        }

        if (!snapshot.TryBeginInteraction())
            throw new InvalidOperationException("Could not acquire a layout read lease.");
        try
        {
            using LayoutPlacementTransformContext.Scope placementScope =
                snapshot.EnterPlacementTransformScopeForBlockIndexUnderPaint(inlineOwner.BlockIndex);
            snapshot.PlacementTransform.GetCurrentTranslation(out _, out float translationY);
            (InlineImageRun Run, Rect Rect) placement = Assert.Single(
                inlineOwner.EnumerateInlineImageRects(), entry => ReferenceEquals(entry.Run.Image, image));
            Rect logicalBounds = snapshot.ToLogicalRectForBlockIndexUnderPaint(
                inlineOwner.BlockIndex, placement.Rect);
            return new InlineImagePlacementTrace(
                logicalBounds, image.Bounds, inlineOwner.Bounds, translationY, "inline");
        }
        finally
        {
            snapshot.EndInteraction();
        }
    }

    internal static SemanticSpanGeometry[] CaptureSemanticSpanGeometry(LayoutSnapshot snapshot)
    {
        if (!snapshot.TryBeginInteraction())
            return [];
        try
        {
            MarkdownSemanticDocument semanticDocument = snapshot.SemanticDocument;
            Func<int, Rect, Rect> placementTransform = snapshot.CaptureLogicalRectMapper();
            return semanticDocument.TextSpans
                .Select(span => new SemanticSpanGeometry(
                    span.TextStart,
                    span.TextEnd,
                    span.InlineRun?.GetType(),
                    semanticDocument.Text.Substring(span.TextStart, span.TextEnd - span.TextStart),
                    semanticDocument.GetDocumentRects(
                        span.TextStart,
                        span.TextEnd,
                        expandDegenerate: false,
                        placementTransform: placementTransform,
                        placementScope: snapshot.EnterPlacementTransformScopeForBlockIndexUnderPaint).ToArray()))
                .ToArray();
        }
        finally
        {
            snapshot.EndInteraction();
        }
    }

    internal static void AssertSemanticSpanGeometryEqual(
        SemanticSpanGeometry[] expected,
        SemanticSpanGeometry[] actual)
    {
        Assert.Equal(expected.Length, actual.Length);
        for (int index = 0; index < expected.Length; index++)
        {
            Assert.Equal(expected[index].Start, actual[index].Start);
            Assert.Equal(expected[index].End, actual[index].End);
            Assert.Equal(expected[index].RunType, actual[index].RunType);
            Assert.Equal(expected[index].Text, actual[index].Text);
            Assert.Equal(expected[index].DocumentRects, actual[index].DocumentRects);
        }
    }

    internal static CanvasTextLayout GetInlineLayout(InlineContainerBox inline) =>
        Assert.IsType<CanvasTextLayout>(inline.TextLayout);

    internal static VectorSceneDrawing GetVectorDrawing(InlineVectorSceneRun run) =>
        Assert.IsType<VectorSceneDrawing>(ReadRawField(run, "_drawing"));

    private static object? ReadRawField(object instance, string name)
    {
        FieldInfo field = instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Missing private test field '{name}'.");
        return field.GetValue(instance);
    }

    internal readonly record struct InlineImagePlacementTrace(
        Rect LogicalBounds,
        Rect ImageBoxBounds,
        Rect OwnerBounds,
        float PlacementTranslationY,
        string Kind);

    internal readonly record struct SemanticSpanGeometry(
        int Start,
        int End,
        Type? RunType,
        string Text,
        Rect[] DocumentRects);
}
