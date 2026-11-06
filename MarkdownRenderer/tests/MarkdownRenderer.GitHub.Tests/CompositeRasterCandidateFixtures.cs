using MarkdownRenderer.Document;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Parsing;
using MarkdownRenderer.Theming;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Windows.UI;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>
/// Table, list and quote source builders, tree walks and paint helpers
/// for <see cref="CompositeRasterLayoutCandidateTests"/>.
/// </summary>
internal static class CompositeRasterCandidateFixtures
{
    private const string RasterMarkdownTemplate = "![raster alt]({0})";

    internal static TableBox CreateTable(
        CompletedRasterPlacementCloneTests.PlacementFixture fixture,
        float width,
        out InlineContainerBox emptyCell)
    {
        MarkdownLayoutContext context = fixture.SourceContext;
        emptyCell = new InlineContainerBox(context, MarkdownElementKeys.TableCell)
        {
            BlockIndex = context.NextBlockIndex(),
        };
        _ = emptyCell.Measure(width);
        emptyCell.Arrange(0, 0, width);

        var table = new TableBox(
            context,
            Array.Empty<InlineContainerBox[]>(),
            new[] { new[] { fixture.Source, emptyCell } },
            new[] { TableBox.CellAlignment.Left, TableBox.CellAlignment.Right })
        {
            BlockIndex = context.NextBlockIndex(),
        };
        float height = table.Measure(width);
        table.Arrange(0, 0, width);
        return table;
    }

    /// <summary>
    /// Builds a one-row table whose first cell holds a styled caption run and a
    /// linked raster run, and whose second cell is empty. The caller measures it.
    /// </summary>
    internal static TableBox CreateStyledImageTable(
        MarkdownLayoutContext sourceContext,
        string rasterSource,
        out InlineContainerBox imageCell,
        out InlineImageRun sourceImageRun)
    {
        imageCell = new(sourceContext, MarkdownElementKeys.TableCell)
        {
            BlockIndex = sourceContext.NextBlockIndex(),
        };
        var textRun = new TextRun("caption ")
        {
            ElementKey = MarkdownElementKeys.Strong,
            SourceSpan = new SourceSpan(0, 8),
            StyleAliases = new[] { "table-caption" },
            StyleModifierKeys = new[] { MarkdownElementKeys.Emphasis },
            SemanticHeadingKey = MarkdownElementKeys.Heading2,
        };
        imageCell.Add(textRun);
        sourceImageRun = new InlineImageRun(
            sourceContext,
            "Raster alt",
            rasterSource,
            linkUrl: "https://example.test/table-image")
        {
            ElementKey = MarkdownElementKeys.Link,
            SourceSpan = new SourceSpan(8, 1),
            StyleAliases = new[] { "table-image" },
            StyleModifierKeys = new[] { MarkdownElementKeys.Strong },
            SemanticHeadingKey = MarkdownElementKeys.Heading3,
        };
        imageCell.Add(sourceImageRun);
        InlineContainerBox emptyCell = new(sourceContext, MarkdownElementKeys.TableCell)
        {
            BlockIndex = sourceContext.NextBlockIndex(),
        };
        var sourceTable = new TableBox(
            sourceContext,
            Array.Empty<InlineContainerBox[]>(),
            new[] { new[] { imageCell, emptyCell } },
            new[] { TableBox.CellAlignment.Left, TableBox.CellAlignment.Right })
        {
            BlockIndex = sourceContext.NextBlockIndex(),
        };
        return sourceTable;
    }

    internal static string CreateGitHubCompositeMarkdown(string shape, string imageSource)
    {
        string image = string.Format(
            System.Globalization.CultureInfo.InvariantCulture,
            RasterMarkdownTemplate,
            imageSource);
        return shape switch
        {
            "table" => $"| Resource | Preview |\n| :--- | ---: |\n| [docs](https://example.test/docs) | {image} |",
            "nested-list" => $"1. Outer [docs](https://example.test/docs)\n   - Inner preview: {image}",
            "quote" => $"> Quoted [docs](https://example.test/docs).\n>\n> Preview: {image}",
            _ => throw new ArgumentOutOfRangeException(nameof(shape), shape, "Unknown GFM fixture shape."),
        };
    }

    internal static MarkdownLayoutContext CreateParsedContext(
        CompletedRasterPlacementCloneTests.PlacementFixture fixture,
        MarkdownSourceMap sourceMap,
        MarkdownExtensionRegistry registry) =>
        new(
            fixture.Device,
            fixture.SourceContext.ThemeSnapshot,
            sourceMap,
            registry,
            FlowDirection.LeftToRight,
            language: "en-US")
        {
            CancellationToken = CancellationToken.None,
            ImageCancellationToken = CancellationToken.None,
            ImagePreparationOwner = fixture.ImagePreparationOwnerToken,
            PerformanceDocumentOwner = fixture.PerformanceDocumentOwner,
            RenderResources = fixture.RenderResources,
        };

    internal static IEnumerable<InlineRun> EnumerateInlineRuns(BlockBox root)
    {
        if (root is InlineContainerBox inline)
        {
            foreach (InlineRun run in inline.Runs)
                yield return run;
        }

        foreach (BlockBox child in EnumerateChildBlocks(root))
        {
            foreach (InlineRun run in EnumerateInlineRuns(child))
                yield return run;
        }
    }

    internal static IEnumerable<InlineImageRun> EnumerateInlineImageRuns(BlockBox root)
    {
        foreach (InlineRun run in EnumerateInlineRuns(root))
        {
            if (run is InlineImageRun image)
                yield return image;
        }
    }

    private static IEnumerable<BlockBox> EnumerateChildBlocks(BlockBox root)
    {
        switch (root)
        {
            case TableBox table:
                foreach (InlineContainerBox cell in table.GetCellBoxes())
                    yield return cell;
                break;
            case ListItemBox item:
                yield return item.Marker;
                yield return item.Content;
                break;
            case StackBox stack:
                foreach (BlockBox child in stack.Children)
                    yield return child;
                break;
        }
    }

    internal static IEnumerable<BlockBox> EnumerateBlocks(BlockBox root)
    {
        yield return root;
        foreach (BlockBox child in EnumerateChildBlocks(root))
        {
            foreach (BlockBox descendant in EnumerateBlocks(child))
                yield return descendant;
        }
    }

    internal static MarkdownLayoutContext CreateContext(
        CompletedRasterPlacementCloneTests.PlacementFixture fixture,
        double rasterizationScale,
        FlowDirection flowDirection)
    {
        MarkdownLayoutContext template = fixture.SourceContext;
        return new MarkdownLayoutContext(
            template.ResourceCreator,
            template.ThemeSnapshot,
            template.SourceMap,
            template.Registry,
            flowDirection,
            template.Dispatcher,
            template.Language)
        {
            CancellationToken = CancellationToken.None,
            ImageCancellationToken = CancellationToken.None,
            RetirementSink = template.RetirementSink,
            RenderResources = template.RenderResources,
            RasterizationScale = rasterizationScale,
            IsCodeBlockCopyEnabled = template.IsCodeBlockCopyEnabled,
            CodeBlockLineNumberMode = template.CodeBlockLineNumberMode,
            CodeBlockWrappingMode = template.CodeBlockWrappingMode,
            CodeBlockChromeMode = template.CodeBlockChromeMode,
            IsTaskListEditingEnabled = template.IsTaskListEditingEnabled,
            CommandProvider = template.CommandProvider,
            StringProvider = template.StringProvider,
            ImageResolver = template.ImageResolver,
            PerformanceSession = template.PerformanceSession,
            PerformanceDocumentOwner = fixture.PerformanceDocumentOwner,
            ImagePreparationOwner = fixture.ImagePreparationOwnerToken,
            SvgRenderer = template.SvgRenderer,
            ImageBaseUri = template.ImageBaseUri,
            ImageDocumentPath = template.ImageDocumentPath,
            ImageDocumentSource = template.ImageDocumentSource,
            AllowThirdPartyRemoteImages = template.AllowThirdPartyRemoteImages,
            DisclosureStates = template.DisclosureStates,
        };
    }

    internal static LayoutSnapshot CreateSourceSnapshot(
        CompletedRasterPlacementCloneTests.PlacementFixture fixture,
        TableBox table,
        float width,
        long automationGeneration)
    {
        var snapshot = new LayoutSnapshot(
            new BlockBox[] { table },
            fixture.SourceContext.SourceMap,
            width,
            (float)table.Bounds.Height);
        fixture.TransferSourceOwnershipToSnapshot(snapshot);
        snapshot.AssignAutomationDocumentGeneration(automationGeneration);
        return snapshot;
    }

    internal static Color[] PaintTable(CanvasDevice device, TableBox table, float width)
    {
        float height = System.Math.Max(1, (float)System.Math.Ceiling(table.Bounds.Height + 4));
        using var target = new CanvasRenderTarget(device, width, height, 96);
        using (CanvasDrawingSession drawing = target.CreateDrawingSession())
        {
            drawing.Clear(Color.FromArgb(0, 0, 0, 0));
            table.Paint(drawing, new Rect(0, 0, width, height));
        }
        return target.GetPixelColors();
    }

    internal static Point Center(Rect bounds)
        => new(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2);
}
