using MarkdownRenderer.Document;
using MarkdownRenderer.Extensions;
using MarkdownRenderer.Layout.Internal.Boxes;
using MarkdownRenderer.Layout.Internal.Building;
using MarkdownRenderer.Layout.Internal.Context;
using MarkdownRenderer.Layout.Internal.Inlines;
using MarkdownRenderer.Layout.Internal.Snapshots;
using MarkdownRenderer.Layout.Internal.Tables;
using MarkdownRenderer.Parsing.Internal;
using MarkdownRenderer.Performance;
using MarkdownRenderer.Performance.Internal;
using MarkdownRenderer.Theming;
using MarkdownRenderer.Theming.Internal;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml;
using Windows.UI;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests.TestSupport.Fixtures;

/// <summary>
/// Names the deferred scene marker attribute, builds layouts, and reads the
/// deferred scene markers of a document and the inline containers of a
/// layout for the progressive scene deferral tests.
/// </summary>
internal static class ProgressiveSceneDeferralFixture
{
    internal const string MarkerAttribute = MarkdownContentAttributes.DeferredScene;

    internal static LayoutSnapshot BuildLayout(
        MarkdownDocument document,
        FlowDirection flowDirection,
        IMarkdownPerformanceSessionInternal? session = null,
        object? owner = null)
        => CreateLayoutBuilder(document, flowDirection, session, owner)
            .Build(document.ParsedDocument!, availableWidth: 640);

    internal static LayoutBuilder CreateLayoutBuilder(
        MarkdownDocument document,
        FlowDirection flowDirection,
        IMarkdownPerformanceSessionInternal? session = null,
        object? owner = null)
    {
        var style = new ElementStyle();
        string[] keys =
        [
            MarkdownElementKeys.Body,
            MarkdownElementKeys.Math,
            MarkdownElementKeys.Link,
            MarkdownElementKeys.CodeInline,
            MarkdownElementKeys.CodeBlock,
            MarkdownElementKeys.List,
            MarkdownElementKeys.ListMarker,
            MarkdownElementKeys.Table,
            MarkdownElementKeys.TableHeader,
            MarkdownElementKeys.TableCell,
            MarkdownElementKeys.Diagram,
        ];
        Color transparent = Color.FromArgb(0, 0, 0, 0);
        var theme = new ThemeSnapshot(
            keys.ToDictionary(static key => key, _ => style, StringComparer.Ordinal),
            new Dictionary<string, ElementStyleOverride>(),
            transparent,
            transparent,
            transparent,
            transparent,
            isDark: false,
            isHighContrast: false,
            textScaleFactor: 1);
        var context = new MarkdownLayoutContext(
            CanvasDevice.GetSharedDevice(),
            theme,
            new MarkdownSourceMap(document.Source),
            new MarkdownExtensionRegistry(),
            flowDirection)
        {
            PerformanceSession = session,
            PerformanceDocumentOwner = owner,
        };
        return new LayoutBuilder(context, semanticDocument: document);
    }

    internal static IEnumerable<InlineContainerBox> EnumerateInlineContainers(
        IEnumerable<BlockBox> blocks)
    {
        foreach (BlockBox block in blocks)
        {
            switch (block)
            {
                case InlineContainerBox inline:
                    yield return inline;
                    break;
                case ListItemBox listItem:
                    foreach (InlineContainerBox nested in EnumerateInlineContainers([listItem.Marker, listItem.Content]))
                        yield return nested;
                    break;
                case TableBox table:
                    foreach (InlineContainerBox cell in table.GetCellBoxes())
                        yield return cell;
                    break;
                case StackBox stack:
                    foreach (InlineContainerBox nested in EnumerateInlineContainers(stack.Children))
                        yield return nested;
                    break;
            }
        }
    }

    internal static string GetSingleMarker(MarkdownDocument document)
        => Assert.IsType<string>(Assert.Single(
            document.ExtensionBlockNodeContent.Values.SelectMany(static fragment => fragment.Items))
            .Attributes[MarkerAttribute]);

    internal static string[] GetMarkers(MarkdownDocument document)
        => document.ExtensionBlockNodeContent.Values
            .SelectMany(static fragment => fragment.Items)
            .Where(static content => content.Attributes.ContainsKey(MarkerAttribute))
            .Select(content => Assert.IsType<string>(content.Attributes[MarkerAttribute]))
            .ToArray();
}
