using System.Reflection;
using MarkdownRenderer.Accessibility;
using MarkdownRenderer.Document;
using MarkdownRenderer.Hosting;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Parsing;
using MarkdownRenderer.Theming;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml;
using Windows.UI;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

public sealed class MarkdownSemanticIndexPreallocationTests
{
    [Fact]
    public void UnmappedStandaloneImagesDoNotPreallocatePositionIndexEntries()
    {
        MarkdownLayoutContext context = CreateContext();
        const int imageCount = 96;
        var images = new ImageBox[imageCount];
        for (int i = 0; i < images.Length; i++)
        {
            images[i] = new ImageBox(context, $"https://example.test/{i}.png", $"image {i}")
            {
                BlockIndex = context.NextBlockIndex(),
            };
        }

        using var snapshot = new LayoutSnapshot(
            images,
            context.SourceMap,
            width: 320,
            height: 240);
        MarkdownSemanticDocument document = snapshot.SemanticDocument;
        Dictionary<(int BlockIndex, int InlineIndex), int> map = GetSpanMap(document);

        Assert.Equal(imageCount, document.TextSpans.Count);
        Assert.Equal(imageCount, document.Root.Children.Count);
        Assert.Empty(map);
        Assert.Equal(0, map.EnsureCapacity(0));

        int expectedStart = 0;
        for (int i = 0; i < images.Length; i++)
        {
            Assert.Equal(expectedStart, document.TextOffsetFromDocumentPosition(
                new DocumentPosition(images[i].BlockIndex, 0, 0)));
            expectedStart += images[i].Alt.Length;
            if (i + 1 < images.Length)
                expectedStart++;
        }
        Assert.Equal(expectedStart, document.Text.Length);
    }

    [Fact]
    public void MappedInlineAtomicBoundariesKeepFirstSpanAndCompleteTextProjection()
    {
        MarkdownLayoutContext context = CreateContext();
        var paragraph = new InlineContainerBox(context, MarkdownElementKeys.Body)
        {
            BlockIndex = context.NextBlockIndex(),
        };
        paragraph.Add(new TextRun("part"));
        var image = new InlineImageRun(context, "name", "https://example.test/name.png");
        paragraph.Add(image);
        paragraph.Add(new TextRun("tail"));

        using var snapshot = new LayoutSnapshot(
            new BlockBox[] { paragraph },
            context.SourceMap,
            width: 320,
            height: 80);
        MarkdownSemanticDocument document = snapshot.SemanticDocument;
        Dictionary<(int BlockIndex, int InlineIndex), int> map = GetSpanMap(document);

        Assert.Equal("part name tail", document.Text);
        Assert.Equal(5, document.TextSpans.Count);
        Assert.Equal(3, map.Count);
        Assert.Equal(3, map.EnsureCapacity(0));

        // Atomic accessible names are separated from adjacent word text. The
        // synthetic leading boundary and image span share one document key;
        // preserving TryAdd's first-wins rule keeps position projection stable.
        MarkdownTextSpan firstImagePositionSpan = document.TextSpans[1];
        Assert.Same(image, firstImagePositionSpan.InlineRun);
        Assert.Equal((4, 5), (firstImagePositionSpan.TextStart, firstImagePositionSpan.TextEnd));
        int firstImagePositionSpanIndex = map[(paragraph.BlockIndex, image.InlineIndex)];
        Assert.Equal(1, firstImagePositionSpanIndex);
        MarkdownTextSpan mappedFirstImagePositionSpan = document.TextSpans[firstImagePositionSpanIndex];
        Assert.Equal((firstImagePositionSpan.TextStart, firstImagePositionSpan.TextEnd),
            (mappedFirstImagePositionSpan.TextStart, mappedFirstImagePositionSpan.TextEnd));
        Assert.Same(firstImagePositionSpan.InlineRun, mappedFirstImagePositionSpan.InlineRun);
        Assert.Same(image, document.TextSpans[2].InlineRun);
        Assert.Equal(4, document.TextOffsetFromDocumentPosition(
            new DocumentPosition(paragraph.BlockIndex, image.InlineIndex, 0)));
        Assert.Equal(5, document.TextOffsetFromDocumentPosition(
            new DocumentPosition(paragraph.BlockIndex, image.InlineIndex, 1)));

        Assert.Equal(
            (DocumentPosition?)new DocumentPosition(paragraph.BlockIndex, image.InlineIndex, 0),
            document.PositionFromTextOffset(4));
        Assert.Equal(
            (DocumentPosition?)new DocumentPosition(paragraph.BlockIndex, image.InlineIndex, image.RenderedLength),
            document.PositionFromTextOffset(8));
    }

    [Fact]
    public void DuplicateInlineBlockIndicesRemainAmbiguousAfterIndexPreallocation()
    {
        MarkdownLayoutContext context = CreateContext();
        int duplicateBlockIndex = context.NextBlockIndex();
        var first = new InlineContainerBox(context, MarkdownElementKeys.Body)
        {
            BlockIndex = duplicateBlockIndex,
        };
        first.Add(new TextRun("first"));
        var second = new InlineContainerBox(context, MarkdownElementKeys.Body)
        {
            BlockIndex = duplicateBlockIndex,
        };
        second.Add(new TextRun("second"));

        using var snapshot = new LayoutSnapshot(
            new BlockBox[] { first, second },
            context.SourceMap,
            width: 320,
            height: 80);
        MarkdownSemanticDocument document = snapshot.SemanticDocument;
        Dictionary<int, MarkdownSemanticNode> blockMap = GetInlineBlockMap(document);

        Assert.True(document.TryGetInlineContainerNode(first, out MarkdownSemanticNode firstNode));
        Assert.True(document.TryGetInlineContainerNode(second, out MarkdownSemanticNode secondNode));
        Assert.NotSame(firstNode, secondNode);
        Assert.False(blockMap.ContainsKey(duplicateBlockIndex));
        Assert.True(blockMap.EnsureCapacity(0) >= 2);

        // Ambiguous IDs still use the range-based fallback, so both ranges
        // resolve to their own inline node rather than the preallocation winner.
        Assert.Same(
            firstNode,
            document.GetEnclosingNodeForTextRangeByBlockIndex(
                firstNode.TextStart,
                firstNode.TextEnd,
                duplicateBlockIndex));
        Assert.Same(
            secondNode,
            document.GetEnclosingNodeForTextRangeByBlockIndex(
                secondNode.TextStart,
                secondNode.TextEnd,
                duplicateBlockIndex));
    }

    private static Dictionary<(int BlockIndex, int InlineIndex), int> GetSpanMap(
        MarkdownSemanticDocument document)
    {
        FieldInfo field = typeof(MarkdownSemanticDocument).GetField(
            "_spanByPosition",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        return Assert.IsType<Dictionary<(int BlockIndex, int InlineIndex), int>>(
            field.GetValue(document));
    }

    private static Dictionary<int, MarkdownSemanticNode> GetInlineBlockMap(
        MarkdownSemanticDocument document)
    {
        FieldInfo field = typeof(MarkdownSemanticDocument).GetField(
            "_nodeByInlineBlockIndex",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        return Assert.IsType<Dictionary<int, MarkdownSemanticNode>>(field.GetValue(document));
    }

    private static MarkdownLayoutContext CreateContext()
    {
        var style = new ElementStyle();
        var styles = new Dictionary<string, ElementStyle>(StringComparer.Ordinal)
        {
            [MarkdownElementKeys.Body] = style,
            [MarkdownElementKeys.Diagram] = style,
            [MarkdownElementKeys.Link] = style,
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
            new MarkdownSourceMap(new string(' ', 2_000)),
            new MarkdownExtensionRegistry(),
            FlowDirection.LeftToRight);
    }
}
