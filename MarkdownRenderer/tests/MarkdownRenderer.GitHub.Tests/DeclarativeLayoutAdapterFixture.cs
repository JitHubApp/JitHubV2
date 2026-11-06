using MarkdownRenderer.Document;
using MarkdownRenderer.Extensions;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Parsing;
using MarkdownRenderer.Theming;
using Markdig.Syntax;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml;
using Windows.UI;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>
/// Builds the declarative extension engines, content trees and layout snapshots
/// that <see cref="DeclarativeLayoutAdapterTests"/> measures.
/// </summary>
internal static class DeclarativeLayoutAdapterFixture
{
    /// <summary>Adds a nested unordered list followed by an ordered list that starts at 7.</summary>
    internal static void AddNestedAndOrderedLists(SourceSpan span, MarkdownContentBuilder content)
    {
        content.AddContainer(
            MarkdownContentKind.List,
            MarkdownStyleRole.Body,
            span,
            MarkdownAccessibilityRole.List,
            list =>
            {
                list.AddContainer(
                    MarkdownContentKind.ListItem,
                    MarkdownStyleRole.Body,
                    span,
                    MarkdownAccessibilityRole.ListItem,
                    item =>
                    {
                        item.AddText("outer", span);
                        item.AddContainer(
                            MarkdownContentKind.List,
                            MarkdownStyleRole.Body,
                            span,
                            MarkdownAccessibilityRole.List,
                            nested => nested.AddContainer(
                                MarkdownContentKind.ListItem,
                                MarkdownStyleRole.Body,
                                span,
                                MarkdownAccessibilityRole.ListItem,
                                nestedItem => nestedItem.AddText("nested", span)));
                    });
                list.AddContainer(
                    MarkdownContentKind.ListItem,
                    MarkdownStyleRole.Body,
                    span,
                    MarkdownAccessibilityRole.ListItem,
                    item => item.AddText("after", span));
            });
        content.AddContainer(
            MarkdownContentKind.List,
            MarkdownStyleRole.Body,
            span,
            MarkdownAccessibilityRole.List,
            list =>
            {
                list.AddContainer(
                    MarkdownContentKind.ListItem,
                    MarkdownStyleRole.Body,
                    span,
                    MarkdownAccessibilityRole.ListItem,
                    item => item.AddText("ordered seven", span));
                list.AddContainer(
                    MarkdownContentKind.ListItem,
                    MarkdownStyleRole.Body,
                    span,
                    MarkdownAccessibilityRole.ListItem,
                    item => item.AddText("ordered eight", span));
            },
            attributes: new Dictionary<string, string>
            {
                [MarkdownContentAttributes.ListOrdered] = "true",
                [MarkdownContentAttributes.ListStart] = "7",
            });
    }

    internal static MarkdownEngine CreateEngine(Action<MarkdownExtensionBuilder> configure) =>
        new MarkdownEngineBuilder()
            .UseExtension(new TestExtension(configure))
            .Build();

    internal static void AddContent(
        string kind,
        MarkdownExtensionContext context,
        MarkdownContentBuilder content)
    {
        SourceSpan span = context.Node.SourceSpan;
        switch (kind)
        {
            case "text":
                content.AddText("native text", span);
                break;
            case "code":
                content.AddCodeBlock(
                    "Console.WriteLine();\n",
                    "csharp",
                    span,
                    attributes: new Dictionary<string, string>
                    {
                        [MarkdownContentAttributes.CodeShowLineNumbers] = "true",
                    });
                break;
            case "image":
                content.AddImage(
                    "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=",
                    "one pixel",
                    span);
                break;
            case "list":
                content.AddContainer(
                    MarkdownContentKind.List,
                    MarkdownStyleRole.Body,
                    span,
                    MarkdownAccessibilityRole.List,
                    list => list.AddContainer(
                        MarkdownContentKind.ListItem,
                        MarkdownStyleRole.Body,
                        span,
                        MarkdownAccessibilityRole.ListItem,
                        item => item.AddText("item", span)));
                break;
            case "table":
                content.AddContainer(
                    MarkdownContentKind.Table,
                    MarkdownStyleRole.Table,
                    span,
                    MarkdownAccessibilityRole.Table,
                    table => table.AddContainer(
                        MarkdownContentKind.TableRow,
                        MarkdownStyleRole.Table,
                        span,
                        MarkdownAccessibilityRole.Row,
                        row => row.AddContainer(
                            MarkdownContentKind.TableCell,
                            MarkdownStyleRole.TableHeader,
                            span,
                            MarkdownAccessibilityRole.Cell,
                            cell => cell.AddText("heading", span)),
                        attributes: new Dictionary<string, string>
                        {
                            [MarkdownContentAttributes.TableHeaderRow] = "true",
                        }));
                break;
            default:
                throw new InvalidOperationException(kind);
        }
    }

    internal static LayoutSnapshot Build(
        MarkdownRenderer.Document.MarkdownDocument document,
        float width = 800,
        double rasterizationScale = 1,
        FlowDirection flowDirection = FlowDirection.LeftToRight,
        ElementStyle? listMarkerStyle = null)
    {
        var style = new ElementStyle();
        string[] keys =
        [
            MarkdownElementKeys.Body,
            MarkdownElementKeys.Strong,
            MarkdownElementKeys.Link,
            MarkdownElementKeys.ImageCaption,
            MarkdownElementKeys.CodeBlock,
            MarkdownElementKeys.CodeBlockHeader,
            MarkdownElementKeys.CodeBlockLanguage,
            MarkdownElementKeys.CodeBlockGutter,
            MarkdownElementKeys.CodeBlockLineNumber,
            MarkdownElementKeys.ListMarker,
            MarkdownElementKeys.Table,
            MarkdownElementKeys.TableHeader,
            MarkdownElementKeys.TableCell,
        ];
        var styles = keys.ToDictionary(static key => key, _ => style, StringComparer.Ordinal);
        if (listMarkerStyle is not null)
            styles[MarkdownElementKeys.ListMarker] = listMarkerStyle;
        Color transparent = Color.FromArgb(0, 0, 0, 0);
        var snapshot = new ThemeSnapshot(
            styles,
            new Dictionary<string, ElementStyleOverride>(),
            transparent,
            transparent,
            transparent,
            transparent,
            isDark: false,
            isHighContrast: false,
            textScaleFactor: 1);
        var sourceMap = new MarkdownSourceMap(document.Source);
        var context = new MarkdownLayoutContext(
            CanvasDevice.GetSharedDevice(),
            snapshot,
            sourceMap,
            new MarkdownExtensionRegistry(),
            flowDirection)
        {
            RasterizationScale = rasterizationScale,
        };
        return new LayoutBuilder(
            context,
            enableDeclarativeHostedElements: false,
            semanticDocument: document)
            .Build(document.ParsedDocument!, width);
    }

    internal static IEnumerable<ListItemBox> EnumerateListItems(IEnumerable<BlockBox> boxes)
    {
        foreach (BlockBox box in boxes)
        {
            if (box is ListItemBox item)
            {
                yield return item;
                foreach (ListItemBox nested in EnumerateListItems(item.Content.Children))
                    yield return nested;
            }
            else if (box is StackBox stack)
            {
                foreach (ListItemBox nested in EnumerateListItems(stack.Children))
                    yield return nested;
            }
        }
    }

    internal static void AssertClose(double expected, double actual) =>
        Assert.InRange(actual, expected - 0.001, expected + 0.001);

    private sealed class TestExtension(Action<MarkdownExtensionBuilder> configure) : IMarkdownExtension
    {
        public string Id => "MarkdownRenderer.Tests.DeclarativeLayout";

        public void Configure(MarkdownExtensionBuilder builder) => configure(builder);
    }
}
