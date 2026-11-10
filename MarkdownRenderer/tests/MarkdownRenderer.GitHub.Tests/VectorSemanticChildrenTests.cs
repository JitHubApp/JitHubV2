using MarkdownRenderer.Accessibility.Internal.Semantics;
using MarkdownRenderer.Controls.Input;
using MarkdownRenderer.Document;
using MarkdownRenderer.Extensions;
using MarkdownRenderer.Extensions.Internal;
using MarkdownRenderer.Layout.Internal.Boxes;
using MarkdownRenderer.Layout.Internal.Context;
using MarkdownRenderer.Layout.Internal.Focus;
using MarkdownRenderer.Layout.Internal.Snapshots;
using MarkdownRenderer.Layout.Internal.Vectors;
using MarkdownRenderer.Theming;
using Windows.Foundation;
using Xunit;
using static MarkdownRenderer.GitHub.Tests.VectorAccessibilityFixture;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>
/// Checks the semantic children of vector scenes: their text ranges,
/// bounds and link entries, how semantic flags drive focus, selection,
/// pointer and UI Automation projections, and unstructured semantic text.
/// </summary>
public sealed class VectorSemanticChildrenTests
{
    [Fact]
    public void VectorChildrenOwnDistinctTextRangesBoundsAndKeyboardLinkEntries()
    {
        MarkdownVectorScene scene = CreateSemanticScene(includeLink: true);
        MarkdownLayoutContext context = CreateContext();
        var vector = new VectorSceneBox(context, CreateContent(scene), MarkdownElementKeys.Diagram)
        {
            BlockIndex = context.NextBlockIndex(),
        };
        vector.Measure(100);
        vector.Arrange(10, 20, 100);

        using var snapshot = new LayoutSnapshot(
            new BlockBox[] { vector },
            context.SourceMap,
            width: 100,
            height: (float)vector.Bounds.Height);
        MarkdownSemanticDocument document = snapshot.SemanticDocument;
        MarkdownSemanticNode diagram = Assert.Single(document.Root.Children);
        MarkdownSemanticNode alpha = Assert.Single(
            diagram.Children,
            static child => child.AccessibilityName == "Alpha");
        MarkdownSemanticNode beta = Assert.Single(
            diagram.Children,
            static child => child.AccessibilityName == "Beta");

        Assert.Equal("Alpha", document.GetText(alpha));
        Assert.Equal("Beta", document.GetText(beta));
        Assert.NotEqual((alpha.TextStart, alpha.TextEnd), (beta.TextStart, beta.TextEnd));
        Assert.Equal(alpha.TextStart, document.TextOffsetFromDocumentPosition(
            new DocumentPosition(vector.BlockIndex, 1, 0)));
        Assert.Equal(alpha.TextEnd, document.TextOffsetFromDocumentPosition(
            new DocumentPosition(vector.BlockIndex, 1, 1)));

        MarkdownTextSpan alphaSpan = Assert.Single(
            document.TextSpans,
            static span => span.VectorSemanticIndex == 1);
        MarkdownTextSpan betaSpan = Assert.Single(
            document.TextSpans,
            static span => span.VectorSemanticIndex == 2);
        Assert.Equal((alpha.TextStart, alpha.TextEnd), (alphaSpan.TextStart, alphaSpan.TextEnd));
        Assert.Equal((beta.TextStart, beta.TextEnd), (betaSpan.TextStart, betaSpan.TextEnd));

        Rect alphaBounds = Assert.Single(document.GetDocumentRects(alpha.TextStart, alpha.TextEnd));
        Assert.Equal(new Rect(20, 25, 60, 20), alphaBounds);
        Assert.Equal(alphaBounds, alpha.Bounds);

        FocusableItem focusable = Assert.Single(snapshot.CollectFocusableItems());
        Assert.True(focusable.IsVectorSemantic);
        Assert.Equal(vector.BlockIndex, focusable.BlockIndex);
        Assert.Equal(1, focusable.InlineIndex);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(11)]
    [InlineData(12)]
    [InlineData(13)]
    [InlineData(14)]
    [InlineData(15)]
    public void VectorSemanticFlagsDriveFocusSelectionPointerAndUiaProjections(int rawFlags)
    {
        var flags = (MarkdownVectorSemanticFlags)rawFlags;
        bool decorative = (flags & MarkdownVectorSemanticFlags.Decorative) != 0;
        bool expectedExposed = !decorative;
        bool expectedFocusable = expectedExposed && (flags & MarkdownVectorSemanticFlags.Focusable) != 0;
        bool expectedSelectable = expectedExposed && (flags & MarkdownVectorSemanticFlags.Selectable) != 0;
        bool expectedLinked = expectedExposed && (flags & MarkdownVectorSemanticFlags.Linked) != 0;
        MarkdownVectorScene scene = CreateFlagScene(flags);
        MarkdownLayoutContext context = CreateContext();
        var vector = new VectorSceneBox(context, CreateContent(scene), MarkdownElementKeys.Diagram)
        {
            BlockIndex = context.NextBlockIndex(),
        };
        vector.Measure(200);
        vector.Arrange(0, 0, 200);

        using var snapshot = new LayoutSnapshot(
            new BlockBox[] { vector },
            context.SourceMap,
            width: 200,
            height: (float)vector.Bounds.Height);
        MarkdownSemanticDocument document = snapshot.SemanticDocument;
        MarkdownSemanticNode diagram = Assert.Single(document.Root.Children);
        MarkdownSemanticNode? itemNode = diagram.Children.SingleOrDefault(
            static child => child.AccessibilityName == "Flag target");
        Rect itemBounds = vector.GetSemanticBounds(1);
        var point = new Point(
            itemBounds.Left + itemBounds.Width / 2,
            itemBounds.Top + itemBounds.Height / 2);

        Assert.Equal(expectedExposed, MarkdownVectorSemanticPolicy.IsExposed(flags));
        Assert.Equal(expectedFocusable, MarkdownVectorSemanticPolicy.IsKeyboardFocusable(flags));
        Assert.Equal(expectedSelectable, MarkdownVectorSemanticPolicy.IsSelectable(flags));
        Assert.Equal(expectedLinked, MarkdownVectorSemanticPolicy.IsLinked(flags));
        Assert.Equal(expectedLinked, MarkdownVectorSemanticPolicy.IsInvokable(flags, hasAction: true));

        Assert.Equal(expectedExposed, itemNode is not null);
        if (itemNode is not null)
        {
            Assert.Equal(flags, itemNode.VectorSemanticFlags);
            Assert.Equal(
                expectedLinked ? MarkdownSemanticRole.Link : MarkdownSemanticRole.Group,
                itemNode.Role);
            Assert.Equal(
                expectedLinked ? MarkdownAccessibilityRole.Link : MarkdownAccessibilityRole.Group,
                itemNode.AccessibilityRole);
        }

        Assert.Equal(
            expectedFocusable,
            snapshot.CollectFocusableItems().Any(static item => item.IsVectorSemantic));
        Assert.Equal(
            expectedExposed,
            document.TextSpans.Any(static span => span.VectorSemanticIndex == 1));
        Assert.Equal(expectedSelectable, vector.HitTest(point, out DocumentPosition position));
        if (expectedSelectable)
            Assert.Equal(1, position.InlineIndex);
        Assert.Equal(expectedLinked, vector.TryGetLinkAt(point, out MarkdownVectorLinkAction? link));
        Assert.Equal(expectedLinked, link is not null);

        var target = LinkTargets.FindLinkTargetInBlock(
            vector,
            new DocumentPosition(vector.BlockIndex, 1, 0),
            point);
        Assert.Equal(expectedLinked, target.HasValue);
    }

    [Fact]
    public void LinkedOnlyVectorChildOwnsTextRangeWithoutBecomingSelectable()
    {
        MarkdownVectorScene scene = CreateFlagScene(MarkdownVectorSemanticFlags.Linked);
        MarkdownLayoutContext context = CreateContext();
        var vector = new VectorSceneBox(context, CreateContent(scene), MarkdownElementKeys.Diagram)
        {
            BlockIndex = context.NextBlockIndex(),
        };
        vector.Measure(200);
        vector.Arrange(0, 0, 200);

        using var snapshot = new LayoutSnapshot(
            new BlockBox[] { vector },
            context.SourceMap,
            width: 200,
            height: (float)vector.Bounds.Height);
        MarkdownSemanticDocument document = snapshot.SemanticDocument;
        MarkdownSemanticNode diagram = Assert.Single(document.Root.Children);
        MarkdownSemanticNode item = Assert.Single(
            diagram.Children,
            static child => child.AccessibilityName == "Flag target");
        MarkdownTextSpan span = Assert.Single(
            document.TextSpans,
            static candidate => candidate.VectorSemanticIndex == 1);

        Assert.Equal(MarkdownSemanticRole.Link, item.Role);
        Assert.Equal(MarkdownAccessibilityRole.Link, item.AccessibilityRole);
        Assert.Equal("Flag target", document.GetText(item));
        Assert.Equal((item.TextStart, item.TextEnd), (span.TextStart, span.TextEnd));
        Assert.True(item.TextEnd > item.TextStart);

        Rect bounds = vector.GetSemanticBounds(1);
        var point = new Point(
            bounds.Left + bounds.Width / 2,
            bounds.Top + bounds.Height / 2);
        Assert.False(vector.HitTest(point, out _));
        Assert.True(vector.TryGetLinkAt(point, out MarkdownVectorLinkAction? link));
        Assert.NotNull(link);
    }

    [Fact]
    public void PointerHitTargetPreservesExternalVectorLinkDispositionMetadata()
    {
        MarkdownVectorScene scene = CreateSemanticScene(includeLink: true);
        MarkdownLayoutContext context = CreateContext();
        var vector = new VectorSceneBox(context, CreateContent(scene), MarkdownElementKeys.Diagram)
        {
            BlockIndex = context.NextBlockIndex(),
        };
        vector.Measure(100);
        vector.Arrange(10, 20, 100);

        var target = LinkTargets.FindLinkTargetInBlock(
            vector,
            new DocumentPosition(vector.BlockIndex, 0, 0),
            new Point(30, 30));

        Assert.NotNull(target);
        Assert.Equal("https://example.test/alpha", target.Value.Url);
        Assert.True(target.Value.External);
    }

    [Fact]
    public void UnstructuredVectorSemanticTextRemainsOneExactRange()
    {
        const string semanticText = "  x + y\r\n= z  ";
        var scene = new MarkdownVectorScene(
            width: 80,
            height: 30,
            baseline: 24,
            commands: Array.Empty<MarkdownVectorCommand>(),
            referenceFontSize: 16);
        var builder = new MarkdownContentBuilder();
        builder.AddVectorScene(
            scene,
            new SourceSpan(0, semanticText.Length),
            MarkdownStyleRole.Math,
            MarkdownAccessibilityRole.Math,
            accessibilityName: "Formula",
            semanticText: semanticText);
        MarkdownLayoutContext context = CreateContext();
        var vector = new VectorSceneBox(
            context,
            Assert.Single(builder.Build().Items),
            MarkdownElementKeys.Math)
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
        MarkdownSemanticDocument document = snapshot.SemanticDocument;
        MarkdownTextSpan span = Assert.Single(document.TextSpans);

        Assert.Equal(semanticText, document.Text);
        Assert.Equal((0, semanticText.Length), (span.TextStart, span.TextEnd));
        Assert.Equal(-1, span.VectorSemanticIndex);
    }

    private static MarkdownVectorScene CreateFlagScene(MarkdownVectorSemanticFlags flags)
    {
        IReadOnlyList<MarkdownVectorLinkAction> links =
            (flags & MarkdownVectorSemanticFlags.Linked) != 0
                ? [new MarkdownVectorLinkAction(1, "https://example.test/flag", action: null)]
                : Array.Empty<MarkdownVectorLinkAction>();
        return new MarkdownVectorScene(
            width: 80,
            height: 40,
            baseline: 32,
            commands: Array.Empty<MarkdownVectorCommand>(),
            referenceFontSize: 16,
            viewport: new MarkdownVectorRectangle(0, 0, 80, 40),
            semantics:
            [
                new(
                    1,
                    MarkdownVectorSemanticRole.Diagram,
                    "Flag diagram",
                    null,
                    -1,
                    new SourceSpan(0, 10),
                    new MarkdownVectorRectangle(0, 0, 80, 40)),
                new(
                    2,
                    MarkdownVectorSemanticRole.Node,
                    "Flag target",
                    null,
                    0,
                    new SourceSpan(0, 10),
                    new MarkdownVectorRectangle(10, 10, 40, 20),
                    flags),
            ],
            links);
    }
}
