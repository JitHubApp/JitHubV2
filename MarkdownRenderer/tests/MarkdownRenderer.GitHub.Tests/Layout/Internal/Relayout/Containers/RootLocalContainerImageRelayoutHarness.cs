using MarkdownRenderer.Document;
using MarkdownRenderer.Extensions;
using MarkdownRenderer.GitHub.Tests.Layout.Internal.Inlines.Placement;
using MarkdownRenderer.Html;
using MarkdownRenderer.Images.Svg;
using MarkdownRenderer.Layout.Internal.Boxes;
using MarkdownRenderer.Layout.Internal.Building;
using MarkdownRenderer.Layout.Internal.Context;
using MarkdownRenderer.Layout.Internal.Inlines;
using MarkdownRenderer.Layout.Internal.Inlines.Runs;
using MarkdownRenderer.Layout.Internal.Snapshots;
using MarkdownRenderer.Parsing.Internal;
using MarkdownRenderer.Performance;
using MarkdownRenderer.Selection;
using MarkdownRenderer.Selection.Internal;
using Markdig;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Windows.UI;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests.Layout.Internal.Relayout.Containers;

/// <summary>
/// Document construction, Markdown sources, paint and selection capture for
/// <see cref="RootLocalContainerImageRelayoutTests"/>.
/// </summary>
internal static class RootLocalContainerImageRelayoutHarness
{
    internal const float LayoutWidth = 640f;
    internal const long AutomationGeneration = 8_452;

    internal static string CreateRichListAndQuoteSource(ContainerResolver resolver) =>
        "- **outer strong** and [outer link](https://example.test/list)\n" +
        "  - nested text-only leaf\n" +
        $"  - ![nested image]({resolver.FirstUrl})\n" +
        $"  - ![nested SVG]({resolver.SvgUrl})\n\n" +
        $"- ![outer loose image]({resolver.SecondUrl})\n\n" +
        "> quote text with **style** and [quote link](https://example.test/quote)\n>\n" +
        $"> ![quote image]({resolver.ThirdUrl})\n\n" +
        "Trailing text-only paragraph keeps the changed roots away from document end.\n";

    internal static string CreateUnsupportedSource(
        ContainerResolver resolver,
        RootLocalContainerImageRelayoutTests.UnsupportedNestedShape shape) => shape switch
    {
        RootLocalContainerImageRelayoutTests.UnsupportedNestedShape.Code =>
            $"- ![candidate]({resolver.FirstUrl})\n\n  ```csharp\n  int value = 1;\n  ```\n",
        RootLocalContainerImageRelayoutTests.UnsupportedNestedShape.TaskList =>
            $"- [ ] task text ![candidate]({resolver.FirstUrl})\n- ordinary sibling\n",
        RootLocalContainerImageRelayoutTests.UnsupportedNestedShape.Html =>
            $"- ![candidate]({resolver.FirstUrl})\n\n  <div align=\"center\">\n  <p>raw inline html</p>\n  </div>\n",
        RootLocalContainerImageRelayoutTests.UnsupportedNestedShape.Table =>
            $"- lead text\n\n  | cell | other |\n  | --- | --- |\n  | ![candidate]({resolver.FirstUrl}) | value |\n",
        _ => throw new ArgumentOutOfRangeException(nameof(shape)),
    };

    internal static void ApplyNonzeroListItemMarginsAndRefresh(LayoutSnapshot snapshot)
    {
        StackBox list = Assert.Single(snapshot.Blocks.OfType<StackBox>(),
            stack => stack.Children.OfType<ListItemBox>().Any());
        int index = 0;
        foreach (ListItemBox item in list.Children.OfType<ListItemBox>())
        {
            float offset = index++ * 2;
            item.Margin = new Thickness(3 + offset, 2 + offset, 5 + offset, 7 + offset);
        }
        snapshot.RelayoutMeasuredBlocks(LayoutWidth, CancellationToken.None);
    }

    internal static ContainerImageDocument BuildDocument(
        CompletedRasterPlacementCloneTests.PlacementFixture seed,
        MarkdownPerformanceSession session,
        ContainerResolver resolver,
        string source,
        FlowDirection direction,
        float rasterizationScale,
        bool lazy = false,
        IMarkdownSvgRenderer? svgRenderer = null,
        bool enableSafeHtml = false)
    {
        var registry = new MarkdownExtensionRegistry().ConfigureGitHubReadmeRegistry(
            enableSafeHtml ? SafeHtmlOptions.Default : null);
        Markdig.Syntax.MarkdownDocument parsed = Markdown.Parse(source, registry.BuildPipeline());
        MarkdownRenderer.Document.MarkdownDocument semanticDocument = MarkdownRenderer.Document.MarkdownDocument.FromParsed(source, parsed);
        var sourceMap = new MarkdownSourceMap(source);
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
                ? builder.BuildLazy(parsed, LayoutWidth, viewportTop: 0, viewportHeight: 100,
                    overscan: 0, CancellationToken.None)
                : builder.Build(parsed, LayoutWidth, CancellationToken.None);
            snapshot.AssignAutomationDocumentGeneration(AutomationGeneration);
            var document = new ContainerImageDocument(context, snapshot, sourceMap, resolver);
            snapshot = null;
            return document;
        }
        catch
        {
            try
            {
                snapshot?.Retire().GetAwaiter().GetResult();
            }
            finally
            {
                context.DrainOwnedRetirementsAsync().GetAwaiter().GetResult();
            }
            throw;
        }
    }

    internal static Rect[][] CaptureSelectionGeometry(
        LayoutSnapshot snapshot,
        IReadOnlyList<InlineContainerBox> inlines)
    {
        var result = new Rect[inlines.Count][];
        for (int index = 0; index < inlines.Count; index++)
        {
            InlineContainerBox inline = inlines[index];
            TextRun? firstText = inline.Runs.OfType<TextRun>().FirstOrDefault(static run => run.Text.Length > 0);
            if (firstText is null)
            {
                result[index] = [];
                continue;
            }

            int runIndex = Enumerable.Range(0, inline.Runs.Count)
                .First(index => ReferenceEquals(inline.Runs[index], firstText));
            var range = new DocumentRange(
                new DocumentPosition(inline.BlockIndex, runIndex, 0),
                new DocumentPosition(inline.BlockIndex, runIndex, 1));
            var selection = new SelectionController();
            selection.SetAnchor(range.Start);
            selection.ExtendTo(range.End);
            result[index] = selection.GetHighlightRects(snapshot).ToArray();
        }
        return result;
    }

    internal static Color[] Paint(
        LayoutSnapshot snapshot,
        CanvasDevice device,
        float rasterizationScale,
        Rect? viewport = null)
    {
        float height = (float)System.Math.Max(1, System.Math.Ceiling(snapshot.Size.Height + 1));
        using var target = new CanvasRenderTarget(device, LayoutWidth, height, 96 * rasterizationScale);
        using (CanvasDrawingSession drawing = target.CreateDrawingSession())
        {
            drawing.Clear(Color.FromArgb(0, 0, 0, 0));
            snapshot.Paint(drawing, viewport ?? new Rect(0, 0, LayoutWidth, height));
        }
        return target.GetPixelColors();
    }
}
