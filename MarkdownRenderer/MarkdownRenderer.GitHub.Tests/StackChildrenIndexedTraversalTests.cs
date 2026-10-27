using System;
using System.Collections.Generic;
using System.Linq;
using MarkdownRenderer.Accessibility;
using MarkdownRenderer.Document;
using MarkdownRenderer.Extensions;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Parsing;
using MarkdownRenderer.Theming;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml;
using Windows.UI;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>
/// Protects semantic construction through StackBox children against ordering,
/// source-range, and list-position drift when traversed by index.
/// </summary>
public sealed class StackChildrenIndexedTraversalTests
{
    [Fact]
    public void NestedListsAndQuotesPreserveSemanticPreorderTextRangesAndFocusOrder()
    {
        string[] labels =
        [
            "quote-one",
            "quote-nested",
            "outer-one",
            "outer-two",
            "inner-two",
            "tail-quote",
        ];
        string source = string.Join(' ', labels);
        MarkdownLayoutContext context = CreateContext(source);
        int nextSourceOffset = 0;
        var expectedLinks = new List<LinkRun>(labels.Length);
        var expectedFocus = new List<FocusableItem>(labels.Length);

        StackBox quote = NewStack(context);
        quote.Add(NewLinkedParagraph(
            context, source, "quote-one", ref nextSourceOffset, expectedLinks, expectedFocus));

        StackBox quoteList = NewStack(context);
        quoteList.Add(NewListItem(
            context,
            markerText: "•",
            content => content.Add(NewLinkedParagraph(
                context, source, "quote-nested", ref nextSourceOffset, expectedLinks, expectedFocus))));
        quote.Add(quoteList);

        StackBox outerList = NewStack(context);
        outerList.Add(NewListItem(
            context,
            markerText: "1.",
            content => content.Add(NewLinkedParagraph(
                context, source, "outer-one", ref nextSourceOffset, expectedLinks, expectedFocus))));

        outerList.Add(NewListItem(context, markerText: "2.", content =>
        {
            content.Add(NewLinkedParagraph(
                context, source, "outer-two", ref nextSourceOffset, expectedLinks, expectedFocus));

            StackBox nestedList = NewStack(context);
            nestedList.Add(NewListItem(
                context,
                markerText: "•",
                nestedContent => nestedContent.Add(NewLinkedParagraph(
                    context, source, "inner-two", ref nextSourceOffset, expectedLinks, expectedFocus))));
            content.Add(nestedList);
        }));

        StackBox tailQuote = NewStack(context);
        tailQuote.Add(NewLinkedParagraph(
            context, source, "tail-quote", ref nextSourceOffset, expectedLinks, expectedFocus));

        using var snapshot = new LayoutSnapshot(
            new BlockBox[] { quote, outerList, tailQuote },
            context.SourceMap,
            width: 640,
            height: 800);

        MarkdownSemanticDocument document = snapshot.SemanticDocument;
        MarkdownSemanticNode[] preorder = MarkdownSemanticDocument
            .EnumerateDepthFirst(document.Root)
            .ToArray();
        Assert.Equal(
            new[]
            {
                MarkdownSemanticRole.Document,
                MarkdownSemanticRole.Group,
                MarkdownSemanticRole.Paragraph,
                MarkdownSemanticRole.Link,
                MarkdownSemanticRole.List,
                MarkdownSemanticRole.ListItem,
                MarkdownSemanticRole.Paragraph,
                MarkdownSemanticRole.Group,
                MarkdownSemanticRole.Paragraph,
                MarkdownSemanticRole.Link,
                MarkdownSemanticRole.List,
                MarkdownSemanticRole.ListItem,
                MarkdownSemanticRole.Paragraph,
                MarkdownSemanticRole.Group,
                MarkdownSemanticRole.Paragraph,
                MarkdownSemanticRole.Link,
                MarkdownSemanticRole.ListItem,
                MarkdownSemanticRole.Paragraph,
                MarkdownSemanticRole.Group,
                MarkdownSemanticRole.Paragraph,
                MarkdownSemanticRole.Link,
                MarkdownSemanticRole.List,
                MarkdownSemanticRole.ListItem,
                MarkdownSemanticRole.Paragraph,
                MarkdownSemanticRole.Group,
                MarkdownSemanticRole.Paragraph,
                MarkdownSemanticRole.Link,
                MarkdownSemanticRole.Group,
                MarkdownSemanticRole.Paragraph,
                MarkdownSemanticRole.Link,
            },
            preorder.Select(static node => node.Role));

        Assert.Equal(
            "quote-one\n•\nquote-nested\n1.\nouter-one\n2.\nouter-two\n•\ninner-two\ntail-quote",
            document.Text);

        MarkdownSemanticNode[] links = preorder
            .Where(static node => node.Role == MarkdownSemanticRole.Link)
            .ToArray();
        Assert.Equal(expectedLinks.Count, links.Length);
        for (int index = 0; index < expectedLinks.Count; index++)
        {
            LinkRun expectedRun = expectedLinks[index];
            MarkdownSemanticNode node = links[index];
            Assert.Same(expectedRun, node.InlineRun);
            MarkdownTextSpan textSpan = Assert.Single(document.TextSpans,
                span => ReferenceEquals(span.InlineRun, expectedRun));
            Assert.Equal(expectedRun.Text, document.Text.Substring(
                textSpan.TextStart,
                textSpan.TextEnd - textSpan.TextStart));

            var range = new DocumentRange(
                new DocumentPosition(textSpan.InlineBox!.BlockIndex, expectedRun.InlineIndex, 0),
                new DocumentPosition(
                    textSpan.InlineBox.BlockIndex,
                    expectedRun.InlineIndex,
                    expectedRun.RenderedLength));
            Assert.True(snapshot.SourceMap.TryMapRange(range, out SourceSpan mappedSource));
            Assert.Equal(expectedRun.SourceSpan, mappedSource);
        }

        MarkdownSemanticNode[] lists = preorder
            .Where(static node => node.Role == MarkdownSemanticRole.List)
            .ToArray();
        Assert.Equal(3, lists.Length);
        Assert.Equal(new[] { 1, 1, 2 }, lists.Select(static list => list.Level));

        MarkdownSemanticNode[] quoteListItems = lists[0].Children
            .Where(static node => node.Role == MarkdownSemanticRole.ListItem)
            .ToArray();
        MarkdownSemanticNode[] outerItems = lists[1].Children
            .Where(static node => node.Role == MarkdownSemanticRole.ListItem)
            .ToArray();
        MarkdownSemanticNode[] nestedItems = lists[2].Children
            .Where(static node => node.Role == MarkdownSemanticRole.ListItem)
            .ToArray();
        Assert.Collection(quoteListItems,
            item => Assert.Equal((1, 1, 1), (item.Level, item.PositionInSet, item.SizeOfSet)));
        Assert.Collection(outerItems,
            item => Assert.Equal((1, 1, 2), (item.Level, item.PositionInSet, item.SizeOfSet)),
            item => Assert.Equal((1, 2, 2), (item.Level, item.PositionInSet, item.SizeOfSet)));
        Assert.Collection(nestedItems,
            item => Assert.Equal((2, 1, 1), (item.Level, item.PositionInSet, item.SizeOfSet)));

        Assert.Equal(expectedFocus, snapshot.CollectFocusableItems());
    }

    private static MarkdownLayoutContext CreateContext(string source)
    {
        var style = new ElementStyle();
        var styles = new Dictionary<string, ElementStyle>(StringComparer.Ordinal)
        {
            [MarkdownElementKeys.Body] = style,
            [MarkdownElementKeys.ListMarker] = style,
        };
        Color transparent = Color.FromArgb(0, 0, 0, 0);
        var theme = new ThemeSnapshot(
            styles,
            new Dictionary<string, ElementStyleOverride>(),
            transparent,
            transparent,
            transparent,
            transparent,
            isDark: false,
            isHighContrast: false,
            textScaleFactor: 1);
        return new MarkdownLayoutContext(
            CanvasDevice.GetSharedDevice(),
            theme,
            new MarkdownSourceMap(source),
            new MarkdownExtensionRegistry(),
            FlowDirection.LeftToRight);
    }

    private static StackBox NewStack(MarkdownLayoutContext context) => new()
    {
        BlockIndex = context.NextBlockIndex(),
        FlowDirection = FlowDirection.LeftToRight,
    };

    private static ListItemBox NewListItem(
        MarkdownLayoutContext context,
        string markerText,
        Action<StackBox> populateContent)
    {
        var marker = new InlineContainerBox(context, MarkdownElementKeys.ListMarker)
        {
            BlockIndex = context.NextBlockIndex(),
        };
        marker.Add(new TextRun(markerText)
        {
            ElementKey = MarkdownElementKeys.ListMarker,
        });

        StackBox content = NewStack(context);
        populateContent(content);
        return new ListItemBox(marker, content, markerWidth: 24)
        {
            BlockIndex = context.NextBlockIndex(),
            FlowDirection = FlowDirection.LeftToRight,
        };
    }

    private static InlineContainerBox NewLinkedParagraph(
        MarkdownLayoutContext context,
        string source,
        string text,
        ref int nextSourceOffset,
        List<LinkRun> runs,
        List<FocusableItem> focus)
    {
        int sourceStart = source.IndexOf(text, nextSourceOffset, StringComparison.Ordinal);
        Assert.True(sourceStart >= nextSourceOffset);
        var sourceSpan = new SourceSpan(sourceStart, text.Length);
        nextSourceOffset = sourceSpan.End;

        var paragraph = new InlineContainerBox(context, MarkdownElementKeys.Body)
        {
            BlockIndex = context.NextBlockIndex(),
        };
        var run = new LinkRun(text, $"https://example.test/{text}")
        {
            SourceSpan = sourceSpan,
        };
        paragraph.Add(run);
        context.SourceMap.Add(paragraph.BlockIndex, run.InlineIndex, run.RenderedLength, sourceSpan);
        runs.Add(run);
        focus.Add(new FocusableItem(paragraph.BlockIndex, run.InlineIndex, FocusableItemKind.Link));
        return paragraph;
    }
}
