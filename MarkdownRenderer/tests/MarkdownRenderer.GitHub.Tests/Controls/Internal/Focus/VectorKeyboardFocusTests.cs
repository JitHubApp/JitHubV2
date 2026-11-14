using MarkdownRenderer.Accessibility.Internal.Peers;
using MarkdownRenderer.Accessibility.Internal.Semantics;
using MarkdownRenderer.Controls.Internal.Focus;
using MarkdownRenderer.Extensions.Vectors;
using MarkdownRenderer.Layout.Internal.Boxes;
using MarkdownRenderer.Layout.Internal.Context;
using MarkdownRenderer.Layout.Internal.Focus;
using MarkdownRenderer.Layout.Internal.Inlines;
using MarkdownRenderer.Layout.Internal.Inlines.Runs;
using MarkdownRenderer.Layout.Internal.Snapshots;
using MarkdownRenderer.Layout.Internal.Vectors;
using MarkdownRenderer.Theming;
using Xunit;
using static MarkdownRenderer.GitHub.Tests.TestSupport.Assertions.VectorAccessibilityAssertions;
using static MarkdownRenderer.GitHub.Tests.TestSupport.Fixtures.VectorAccessibilityFixture;

namespace MarkdownRenderer.GitHub.Tests.Controls.Internal.Focus;

/// <summary>
/// Checks keyboard access to vector content: semantic focus order,
/// linked inline math activation, overflow entries for wide scenes and
/// their virtual focus, and read-only task embeds left out of the order.
/// </summary>
public sealed class VectorKeyboardFocusTests
{
    [Fact]
    public void VectorKeyboardFocusFollowsSemanticOrderIndependentlyOfLinkActions()
    {
        MarkdownVectorSemanticItem[] semantics =
        [
            new(
                1,
                MarkdownVectorSemanticRole.Diagram,
                "Flow diagram",
                null,
                -1,
                new SourceSpan(0, 10),
                new MarkdownVectorRectangle(0, 0, 200, 60)),
            new(
                2,
                MarkdownVectorSemanticRole.Node,
                "Alpha",
                null,
                0,
                new SourceSpan(0, 5),
                new MarkdownVectorRectangle(10, 5, 60, 20),
                MarkdownVectorSemanticFlags.Focusable),
            new(
                3,
                MarkdownVectorSemanticRole.Node,
                "Beta",
                null,
                0,
                new SourceSpan(6, 4),
                new MarkdownVectorRectangle(120, 35, 40, 15),
                MarkdownVectorSemanticFlags.Focusable | MarkdownVectorSemanticFlags.Linked),
        ];
        var scene = new MarkdownVectorScene(
            200,
            60,
            48,
            Array.Empty<MarkdownVectorCommand>(),
            16,
            new MarkdownVectorRectangle(0, 0, 200, 60),
            semantics,
            [
                new MarkdownVectorLinkAction(2, "https://example.test/beta", action: null),
            ]);
        MarkdownLayoutContext context = CreateContext();
        var vector = new VectorSceneBox(context, CreateContent(scene), MarkdownElementKeys.Diagram)
        {
            BlockIndex = context.NextBlockIndex(),
        };
        vector.Measure(100);
        vector.Arrange(0, 0, 100);

        using var snapshot = new LayoutSnapshot(
            new BlockBox[] { vector },
            context.SourceMap,
            width: 100,
            height: (float)vector.Bounds.Height);
        IReadOnlyList<FocusableItem> focusable = snapshot.CollectFocusableItems();

        Assert.Equal(2, focusable.Count);
        Assert.Equal(1, focusable[0].InlineIndex);
        Assert.Equal(2, focusable[1].InlineIndex);
    }

    [Fact]
    public void LinkedInlineMathHasOneSemanticNodeAndAKeyboardActivationTarget()
    {
        const string semanticText = "x squared";
        const string linkUrl = "https://example.test/formula";
        const string linkTitle = "Formula documentation";
        var scene = new MarkdownVectorScene(
            width: 80,
            height: 30,
            baseline: 24,
            commands: Array.Empty<MarkdownVectorCommand>(),
            referenceFontSize: 16);
        MarkdownLayoutContext context = CreateContext();
        var paragraph = new InlineContainerBox(context, MarkdownElementKeys.Body)
        {
            BlockIndex = context.NextBlockIndex(),
        };
        var math = new InlineVectorSceneRun(
            context,
            scene,
            semanticText,
            accessibilityName: "x squared",
            accessibilityDescription: "The variable x raised to the second power.",
            elementKey: MarkdownElementKeys.Math)
        {
            SourceSpan = new SourceSpan(12, 6),
            LinkUrl = linkUrl,
            LinkTitle = linkTitle,
        };
        var unlinkedMath = new InlineVectorSceneRun(
            context,
            scene,
            semanticText: "y",
            accessibilityName: "y",
            accessibilityDescription: null,
            elementKey: MarkdownElementKeys.Math)
        {
            SourceSpan = new SourceSpan(20, 1),
        };

        try
        {
            paragraph.Add(new TextRun("See "));
            paragraph.Add(math);
            paragraph.Add(unlinkedMath);
            paragraph.Add(new TextRun(" here."));
            _ = paragraph.Measure(300);
            paragraph.Arrange(0, 0, 300);

            using var snapshot = new LayoutSnapshot(
                new BlockBox[] { paragraph },
                context.SourceMap,
                width: 300,
                height: (float)paragraph.Bounds.Height);
            FocusableItem focusable = Assert.Single(snapshot.CollectFocusableItems());
            Assert.True(focusable.IsLink);
            Assert.Equal(paragraph.BlockIndex, focusable.BlockIndex);
            Assert.Equal(math.InlineIndex, focusable.InlineIndex);

            Assert.True(FocusableItemQueries.TryGetLinkedAtomicForFocusable(
                snapshot,
                focusable,
                out InlineContainerBox resolvedInline,
                out InlineRun resolvedRun));
            Assert.Same(paragraph, resolvedInline);
            Assert.Same(math, resolvedRun);
            AssertLinkedMathActivationTargets(paragraph, math, unlinkedMath, linkUrl, linkTitle);

            AssertLinkedMathHasOneSemanticNode(snapshot, math, semanticText, linkTitle);
        }
        finally
        {
            paragraph.Dispose();
        }
    }

    [Fact]
    public void PlainWideVectorSceneGetsAKeyboardOperableOverflowEntry()
    {
        MarkdownVectorScene scene = CreateSemanticScene(includeLink: false);
        MarkdownLayoutContext context = CreateContext();
        var vector = new VectorSceneBox(context, CreateContent(scene), MarkdownElementKeys.Diagram)
        {
            BlockIndex = context.NextBlockIndex(),
        };
        vector.Measure(100);
        vector.Arrange(0, 0, 100);

        using var snapshot = new LayoutSnapshot(
            new BlockBox[] { vector },
            context.SourceMap,
            width: 100,
            height: (float)vector.Bounds.Height);
        FocusableItem focusable = Assert.Single(snapshot.CollectFocusableItems());

        Assert.True(vector.CanScrollHorizontally);
        Assert.True(focusable.IsHorizontalOverflow);
        Assert.Equal(vector.BlockIndex, focusable.BlockIndex);
    }

    [Fact]
    public void OverflowVirtualFocusMapsToItsExactBlockPeerAndFocusableEntry()
    {
        MarkdownVectorScene scene = CreateSemanticScene(includeLink: false);
        MarkdownLayoutContext context = CreateContext();
        var first = new VectorSceneBox(context, CreateContent(scene), MarkdownElementKeys.Diagram)
        {
            BlockIndex = context.NextBlockIndex(),
        };
        var second = new VectorSceneBox(context, CreateContent(scene), MarkdownElementKeys.Diagram)
        {
            BlockIndex = context.NextBlockIndex(),
        };
        first.Measure(100);
        first.Arrange(0, 0, 100);
        second.Measure(100);
        second.Arrange(0, (float)first.Bounds.Bottom, 100);

        using var snapshot = new LayoutSnapshot(
            new BlockBox[] { first, second },
            context.SourceMap,
            width: 100,
            height: (float)second.Bounds.Bottom);
        IReadOnlyList<FocusableItem> focusable = snapshot.CollectFocusableItems();

        Assert.Equal(2, focusable.Count);
        Assert.All(focusable, static item => Assert.True(item.IsHorizontalOverflow));
        Assert.True(FocusableItemQueries.TryFindHorizontalOverflowFocusableIndex(
            snapshot,
            focusable,
            first,
            out int firstIndex));
        Assert.True(FocusableItemQueries.TryFindHorizontalOverflowFocusableIndex(
            snapshot,
            focusable,
            second,
            out int secondIndex));
        Assert.Equal(0, firstIndex);
        Assert.Equal(1, secondIndex);

        MarkdownSemanticDocument document = snapshot.SemanticDocument;
        MarkdownSemanticNode firstPeerNode = Assert.IsType<MarkdownSemanticNode>(
            MarkdownAutomationPeer.FindHorizontalOverflowFocusNode(document, first));
        MarkdownSemanticNode secondPeerNode = Assert.IsType<MarkdownSemanticNode>(
            MarkdownAutomationPeer.FindHorizontalOverflowFocusNode(document, second));
        Assert.Same(first, firstPeerNode.Box);
        Assert.Same(second, secondPeerNode.Box);
        Assert.NotSame(firstPeerNode, secondPeerNode);
        Assert.Equal(-1, firstPeerNode.VectorSemanticIndex);
        Assert.Equal(-1, secondPeerNode.VectorSemanticIndex);
    }

    [Fact]
    public void ReadOnlyTaskEmbedIsExcludedFromKeyboardOrder()
    {
        var readOnlyTask = new InlineEmbedRun(
            1,
            1,
            () => throw new InvalidOperationException())
        {
            AutomationMetadata = new InlineEmbedAutomationMetadata(
                "Checked",
                "Unchecked",
                "Read only",
                "Toggle",
                "task-1",
                isChecked: false),
        };

        Assert.False(LayoutSnapshot.IsInlineEmbedKeyboardFocusable(readOnlyTask));
    }
}
