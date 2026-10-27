using System.Collections;
using System.Reflection;
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

/// <summary>Ordered semantic metadata tests; these do not qualify native UIA geometry.</summary>
public sealed class SemanticRangeIndexTests
{
    [Fact]
    public void ImageRangesMatchLinearOracleIncludingEmptyAltAndInlineImages()
    {
        MarkdownLayoutContext context = CreateContext();
        var blockImage = new ImageBox(context, string.Empty, "block");
        var inlineImage = new InlineImageRun(context, "inline", string.Empty);
        try
        {
            MarkdownTextSpan[] spans =
            [
                new(0, 0, null, null, blockImage, null, null),
                new(0, 2, null, null, null, null, null),
                new(2, 2, null, null, blockImage, null, null),
                new(2, 4, null, inlineImage, null, null, null),
                new(4, 4, null, null, blockImage, null, null),
                new(4, 6, null, null, blockImage, null, null),
                new(8, 8, null, null, blockImage, null, null),
            ];
            MarkdownSemanticDocument document = CreateDocument("01234567", CreateRoot(8), spans);
            for (int start = -1; start <= 9; start++)
            {
                for (int end = -1; end <= 9; end++)
                {
                    int clampedStart = System.Math.Clamp(start, 0, 8);
                    int clampedEnd = System.Math.Clamp(end, clampedStart, 8);
                    ImageBox[] expected = spans
                        .Where(span => span.TextEnd >= clampedStart && span.TextStart <= clampedEnd)
                        .Select(span => span.ImageBox ?? (span.InlineRun as InlineImageRun)?.Image)
                        .OfType<ImageBox>()
                        .ToArray();
                    Assert.Equal(expected, document.GetImagesIntersectingTextRange(start, end).ToArray());
                }
            }
        }
        finally
        {
            inlineImage.Image.Dispose();
            blockImage.Dispose();
        }
    }

    [Fact]
    public void FarTailImageRangeReadsOnlyTheIndexedSpanWindow()
    {
        const int count = 8192;
        var spans = new CountingList<MarkdownTextSpan>(Enumerable.Range(0, count)
            .Select(index => new MarkdownTextSpan(index * 2, index * 2 + 1, null, null, null, null, null))
            .ToArray());
        string text = new('x', count * 2 + 1);
        MarkdownSemanticDocument document = CreateDocument(text, CreateRoot(text.Length), spans);
        spans.ResetReads();

        Assert.Empty(document.GetImagesIntersectingTextRange(count * 2 - 1, count * 2 - 1));
        Assert.InRange(spans.Reads, 1, 20);
    }

    [Fact]
    public void RootChildQueriesMatchLinearOraclesAtEveryBoundary()
    {
        const int length = 12;
        MarkdownSemanticNode root = CreateRoot(length);
        root.Add(Node(0, 0));
        MarkdownSemanticNode first = Node(0, 3);
        first.Add(Node(0, 1));
        first.Add(Node(1, 3));
        root.Add(first);
        root.Add(Node(3, 3));
        root.Add(Node(4, 8));
        root.Add(Node(8, 8));
        root.Add(Node(8, 12));
        root.Add(Node(12, 12));
        MarkdownSemanticDocument document = CreateDocument(new string('x', length), root, []);

        for (int start = -1; start <= length + 1; start++)
        {
            for (int end = -1; end <= length + 1; end++)
            {
                int lower = System.Math.Clamp(start, 0, length);
                int upper = System.Math.Clamp(end, lower, length);
                Assert.Same(LinearEnclosing(root, lower, upper, length),
                    document.GetInnermostNodeContainingTextRange(start, end));
                MarkdownSemanticNode[] expected = lower == upper ? [] : root.Children
                    .Where(child => child.TextEnd > lower && child.TextStart < upper).ToArray();
                Assert.Equal(expected,
                    document.GetImmediateChildrenIntersectingTextRange(root, start, end).ToArray());
            }
        }
    }

    [Fact]
    public void NestedSemanticChildrenKeepHistoricalOrderAndDoNotAssumeParentContainment()
    {
        MarkdownSemanticNode root = CreateRoot(10);
        MarkdownSemanticNode scene = Node(0, 10);
        root.Add(scene);
        scene.Add(Node(8, 10));
        scene.Add(Node(0, 0));
        scene.Add(Node(0, 3));
        scene.Add(Node(3, 8));
        MarkdownSemanticDocument document = CreateDocument("0123456789", root, []);

        Assert.Same(scene.Children[2], document.GetInnermostNodeContainingTextRange(1, 2));
        Assert.Equal(scene.Children.Where(child => child.TextEnd > 1 && child.TextStart < 9).ToArray(),
            document.GetImmediateChildrenIntersectingTextRange(scene, 1, 9).ToArray());
        Assert.Same(scene.Children[0], document.GetInnermostNodeContainingTextRange(9, 10));
    }

    [Fact]
    public void RealSemanticBuilderPreservesOrderedRootRangesAcrossMixedBlocksAndTrailingTrim()
    {
        MarkdownLayoutContext context = CreateContext();
        InlineContainerBox Paragraph(string text)
        {
            var paragraph = new InlineContainerBox(context, MarkdownElementKeys.Body)
            {
                BlockIndex = context.NextBlockIndex(),
            };
            paragraph.Add(new TextRun(text));
            return paragraph;
        }

        var group = new StackBox();
        group.Add(Paragraph("עברית e\u0301 👩‍👩‍👧‍👦"));
        group.Add(Paragraph("nested"));
        var table = new TableBox(context, [[Paragraph("header")]], [[Paragraph("cell")]]);
        var scene = new MarkdownVectorScene(100, 40, 32, [], referenceFontSize: 16,
            viewport: new MarkdownVectorRectangle(0, 0, 100, 40), semantics:
        [
            new(1, MarkdownVectorSemanticRole.Node, "Parent", null, -1,
                new SourceSpan(0, 0), new MarkdownVectorRectangle(0, 0, 30, 10)),
            new(2, MarkdownVectorSemanticRole.Label, "Child", null, 0,
                new SourceSpan(0, 0), new MarkdownVectorRectangle(40, 0, 30, 10)),
        ], links: []);
        var contentBuilder = new MarkdownContentBuilder();
        contentBuilder.AddVectorScene(scene, new SourceSpan(0, 0), MarkdownStyleRole.Diagram,
            MarkdownAccessibilityRole.Diagram);
        var vector = new VectorSceneBox(context, Assert.Single(contentBuilder.Build().Items),
            MarkdownElementKeys.Diagram) { BlockIndex = context.NextBlockIndex() };
        BlockBox[] blocks = [Paragraph("first"), new ImageBox(context, string.Empty, string.Empty),
            group, table, vector, Paragraph("last\n\n")];
        using var snapshot = new LayoutSnapshot(blocks, context.SourceMap, 200, 400);
        MarkdownSemanticDocument document = snapshot.SemanticDocument;
        IReadOnlyList<MarkdownSemanticNode> rootChildren = document.Root.Children;
        // Explicit empty alt remains decorative, so its block has no UIA node.
        Assert.Equal([MarkdownSemanticRole.Paragraph, MarkdownSemanticRole.Group,
            MarkdownSemanticRole.Table, MarkdownSemanticRole.Diagram, MarkdownSemanticRole.Paragraph],
            rootChildren.Select(node => node.Role).ToArray());
        for (int index = 1; index < rootChildren.Count; index++)
        {
            Assert.True(rootChildren[index - 1].TextEnd <= rootChildren[index].TextStart);
            Assert.True(rootChildren[index - 1].TextEnd <= rootChildren[index].TextEnd);
        }
        // Generated block separators are trimmed, while authored terminal
        // newlines survive. Every node range matches that final projection.
        Assert.Equal(document.Text.Length, rootChildren[^1].TextEnd);
        Assert.InRange(rootChildren[^1].TextStart, 0, rootChildren[^1].TextEnd);

        MarkdownSemanticNode vectorRoot = Assert.Single(rootChildren, node => node.VectorSceneBox == vector);
        MarkdownSemanticNode parent = Assert.Single(vectorRoot.Children);
        MarkdownSemanticNode child = Assert.Single(parent.Children);
        Assert.True(child.TextStart >= parent.TextEnd);
        for (int start = 0; start <= document.Text.Length; start++)
        {
            for (int end = start; end <= document.Text.Length; end++)
            {
                Assert.Same(LinearEnclosing(document.Root, start, end, document.Text.Length),
                    document.GetInnermostNodeContainingTextRange(start, end));
                MarkdownSemanticNode[] expected = start == end ? [] : rootChildren
                    .Where(node => node.TextEnd > start && node.TextStart < end).ToArray();
                Assert.Equal(expected,
                    document.GetImmediateChildrenIntersectingTextRange(document.Root, start, end).ToArray());
            }
        }
    }

    [Fact]
    public void OrderedChildSearchUsesLogarithmicReadsAndKeepsEqualEndBoundaries()
    {
        const int count = 8192;
        var children = new CountingList<MarkdownSemanticNode>(Enumerable.Range(0, count)
            .Select(index => Node(index * 2, index * 2 + 1)).ToArray());
        MethodInfo search = typeof(MarkdownSemanticDocument).GetMethod("GetOrderedChildStartIndex",
            BindingFlags.Static | BindingFlags.NonPublic)!;
        Assert.NotNull(search);

        Assert.Equal(count - 1, search.Invoke(null, [children, count * 2 - 1]));
        Assert.InRange(children.Reads, 1, 14);
        children.ResetReads();
        Assert.Equal(count, search.Invoke(null, [children, count * 2]));
        Assert.InRange(children.Reads, 1, 14);

        MarkdownSemanticNode[] tied = [Node(0, 1), Node(1, 1), Node(1, 1), Node(1, 2)];
        Assert.Equal(0, search.Invoke(null, [tied, 1]));
        Assert.Equal(3, search.Invoke(null, [tied, 2]));
    }

    [Fact]
    public void PrivateCodeLinePayloadsRemainExactReadonlyValues()
    {
        Type logicalType = typeof(CodeBlockBox).GetNestedType("CodeLineInfo", BindingFlags.NonPublic)!;
        Type visualType = typeof(CodeBlockBox).GetNestedType("CodeVisualLineInfo", BindingFlags.NonPublic)!;
        Type diffType = typeof(CodeBlockBox).GetNestedType("CodeLineDiffKind", BindingFlags.NonPublic)!;
        object removed = Enum.Parse(diffType, "Removed");
        AssertFields(logicalType, [37, 11, 19, removed, "line label"],
            ["Number", "Start", "Length", "DiffKind", "Label"]);
        AssertFields(visualType, [7, 12.5, 34.25], ["LogicalLineIndex", "Top", "Height"]);

        static void AssertFields(Type type, object[] values, string[] names)
        {
            ConstructorInfo constructor = Assert.Single(type.GetConstructors(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic));
            object payload = constructor.Invoke(values);
            Assert.Empty(type.GetProperties(BindingFlags.Instance | BindingFlags.Public));
            for (int index = 0; index < values.Length; index++)
            {
                FieldInfo field = type.GetField(names[index], BindingFlags.Instance | BindingFlags.Public)!;
                Assert.NotNull(field);
                Assert.True(field.IsInitOnly);
                Assert.Equal(values[index], field.GetValue(payload));
            }
        }
    }

    private static MarkdownSemanticNode LinearEnclosing(MarkdownSemanticNode root, int start, int end, int length)
    {
        MarkdownSemanticNode current = root;
        while (true)
        {
            MarkdownSemanticNode? next = current.Children.FirstOrDefault(child => start == end
                ? child.TextStart <= start && (start < child.TextEnd || start == length && start == child.TextEnd)
                : child.TextStart <= start && end <= child.TextEnd);
            if (next is null) return current;
            current = next;
        }
    }

    private static MarkdownSemanticNode Node(int start, int end) => new(MarkdownSemanticRole.Paragraph)
    {
        TextStart = start,
        TextEnd = end,
    };

    private static MarkdownSemanticNode CreateRoot(int length) => new(MarkdownSemanticRole.Document)
    {
        TextStart = 0,
        TextEnd = length,
    };

    private static MarkdownSemanticDocument CreateDocument(string text, MarkdownSemanticNode root,
        IReadOnlyList<MarkdownTextSpan> spans)
    {
        ConstructorInfo constructor = typeof(MarkdownSemanticDocument).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic, null,
            [typeof(MarkdownSemanticNode), typeof(string), typeof(IReadOnlyList<MarkdownTextSpan>)], null)!;
        Assert.NotNull(constructor);
        return (MarkdownSemanticDocument)constructor.Invoke([root, text, spans]);
    }

    private static MarkdownLayoutContext CreateContext()
    {
        var style = new ElementStyle();
        Color color = Color.FromArgb(255, 0, 0, 0);
        var theme = new ThemeSnapshot(new Dictionary<string, ElementStyle> { [MarkdownElementKeys.Body] = style },
            new Dictionary<string, ElementStyleOverride>(), color, color, color, color,
            isDark: false, isHighContrast: false, textScaleFactor: 1);
        return new MarkdownLayoutContext(CanvasDevice.GetSharedDevice(), theme, new MarkdownSourceMap(""),
            new MarkdownExtensionRegistry(), FlowDirection.LeftToRight);
    }

    private sealed class CountingList<T>(T[] items) : IReadOnlyList<T>
    {
        internal int Reads { get; private set; }
        public int Count => items.Length;
        public T this[int index] { get { Reads++; return items[index]; } }
        internal void ResetReads() => Reads = 0;
        public IEnumerator<T> GetEnumerator()
        {
            for (int index = 0; index < Count; index++) yield return this[index];
        }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
