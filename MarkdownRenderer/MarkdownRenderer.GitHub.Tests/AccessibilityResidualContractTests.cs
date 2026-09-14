using MarkdownRenderer.Accessibility;
using MarkdownRenderer.Document;
using MarkdownRenderer.Extensions;
using MarkdownRenderer.Hosting;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Parsing;
using MarkdownRenderer.Theming;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Text;
using Windows.Foundation;
using Windows.UI;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

public sealed class AccessibilityResidualContractTests
{
    [Fact]
    public void SemanticTextRangesUseInnermostEnclosureAndImmediateChildren()
    {
        MarkdownLayoutContext context = CreateContext();
        var paragraph = new InlineContainerBox(context, MarkdownElementKeys.Body)
        {
            BlockIndex = context.NextBlockIndex(),
        };
        paragraph.Add(new TextRun("before ") { SourceSpan = new SourceSpan(0, 7) });
        paragraph.Add(new LinkRun("target", "https://example.test")
        {
            SourceSpan = new SourceSpan(7, 6),
        });
        paragraph.Add(new TextRun(" after") { SourceSpan = new SourceSpan(13, 6) });

        using var snapshot = new LayoutSnapshot(
            new BlockBox[] { paragraph },
            context.SourceMap,
            width: 200,
            height: 40);
        MarkdownSemanticDocument document = snapshot.SemanticDocument;
        MarkdownSemanticNode paragraphNode = Assert.Single(document.Root.Children);
        MarkdownSemanticNode linkNode = Assert.Single(paragraphNode.Children);

        Assert.True(document.TryGetInlineContainerNode(paragraph, out MarkdownSemanticNode indexedParagraph));
        Assert.Same(paragraphNode, indexedParagraph);
        Assert.Equal((paragraphNode.TextStart, paragraphNode.TextEnd), (0, "before target after".Length));
        Assert.Same(paragraphNode, document.GetInnermostNodeContainingTextRange(0, 6));
        Assert.Same(
            linkNode,
            document.GetInnermostNodeContainingTextRange(linkNode.TextStart, linkNode.TextEnd));
        Assert.Equal(
            new[] { paragraphNode },
            document.GetImmediateChildrenIntersectingTextRange(
                document.Root,
                0,
                document.Text.Length));
        Assert.Equal(
            new[] { linkNode },
            document.GetImmediateChildrenIntersectingTextRange(
                paragraphNode,
                paragraphNode.TextStart,
                paragraphNode.TextEnd));
    }

    [Fact]
    public void ExactBlockRangeScopePreservesImmediateHierarchyWhenOnlyChildSharesOffsets()
    {
        MarkdownLayoutContext context = CreateContext();
        var paragraph = new InlineContainerBox(context, MarkdownElementKeys.Body)
        {
            BlockIndex = context.NextBlockIndex(),
        };
        paragraph.Add(new LinkRun("only link", "https://example.test")
        {
            SourceSpan = new SourceSpan(0, 9),
        });

        using var snapshot = new LayoutSnapshot(
            new BlockBox[] { paragraph },
            context.SourceMap,
            width: 200,
            height: 40);
        MarkdownSemanticDocument document = snapshot.SemanticDocument;
        MarkdownSemanticNode paragraphNode = Assert.Single(document.Root.Children);
        MarkdownSemanticNode linkNode = Assert.Single(paragraphNode.Children);

        Assert.Equal(
            (paragraphNode.TextStart, paragraphNode.TextEnd),
            (linkNode.TextStart, linkNode.TextEnd));
        Assert.Same(
            linkNode,
            document.GetEnclosingNodeForTextRange(
                linkNode.TextStart,
                linkNode.TextEnd,
                exactRangeScope: null));
        Assert.Same(
            paragraphNode,
            document.GetEnclosingNodeForTextRange(
                paragraphNode.TextStart,
                paragraphNode.TextEnd,
                paragraph));
        Assert.Equal(
            new[] { linkNode },
            document.GetImmediateChildrenIntersectingTextRange(
                paragraphNode,
                paragraphNode.TextStart,
                paragraphNode.TextEnd));
    }

    [Fact]
    public void UnsupportedPageUnitPromotesToDocument()
    {
        Assert.Equal(
            TextUnit.Document,
            MarkdownTextRangeProvider.NormalizeSupportedTextUnit(TextUnit.Page));
        Assert.Equal(
            TextUnit.Paragraph,
            MarkdownTextRangeProvider.NormalizeSupportedTextUnit(TextUnit.Paragraph));
    }

    [Fact]
    public void CollapsedFormatCaretChoosesExactlyOneHalfOpenRun()
    {
        Assert.False(MarkdownTextRangeProvider.ContainsHalfOpenOffset(
            offset: 5,
            start: 0,
            end: 5,
            isFinalRun: false,
            documentLength: 10));
        Assert.True(MarkdownTextRangeProvider.ContainsHalfOpenOffset(
            offset: 5,
            start: 5,
            end: 10,
            isFinalRun: true,
            documentLength: 10));
        Assert.True(MarkdownTextRangeProvider.ContainsHalfOpenOffset(
            offset: 10,
            start: 5,
            end: 10,
            isFinalRun: true,
            documentLength: 10));
    }

    [Fact]
    public void FormatBoundaryNavigationJumpsWithoutPerMoveAllocation()
    {
        var boundaries = new int[100_001];
        for (int i = 0; i < boundaries.Length; i++)
            boundaries[i] = i * 2;

        int current = MarkdownTextRangeProvider.MoveAcrossSortedBoundaries(
            boundaries,
            current: 1,
            count: 1,
            out int warmMoved);
        Assert.Equal(2, current);
        Assert.Equal(1, warmMoved);

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10_000; i++)
        {
            int count = (i & 1) == 0 ? 1 : -1;
            current = MarkdownTextRangeProvider.MoveAcrossSortedBoundaries(
                boundaries,
                current,
                count,
                out _);
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
        Assert.Equal(
            boundaries[^1],
            MarkdownTextRangeProvider.MoveAcrossSortedBoundaries(
                boundaries,
                current,
                int.MaxValue,
                out _));
        Assert.Equal(
            boundaries[0],
            MarkdownTextRangeProvider.MoveAcrossSortedBoundaries(
                boundaries,
                current,
                int.MinValue,
                out _));
    }

    [Fact]
    public void FormatCacheOwnershipLivesOnPeerAndIndependentRequestsReuseOneBuild()
    {
        const System.Reflection.BindingFlags Fields =
            System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.NonPublic;
        Assert.Contains(
            typeof(MarkdownAutomationPeer).GetFields(Fields),
            field => field.FieldType == typeof(MarkdownTextFormatCacheStore));
        Assert.DoesNotContain(
            typeof(MarkdownTextRangeProvider).GetFields(Fields),
            field => field.FieldType == typeof(MarkdownTextFormatCache) ||
                     field.FieldType == typeof(MarkdownTextFormatCacheStore));

        MarkdownLayoutContext context = CreateContext();
        var paragraph = new InlineContainerBox(context, MarkdownElementKeys.Body)
        {
            BlockIndex = context.NextBlockIndex(),
        };
        paragraph.Add(new TextRun("plain "));
        paragraph.Add(new LinkRun("linked", "https://example.test"));
        paragraph.Add(new TextRun(" tail"));

        using var snapshot = new LayoutSnapshot(
            new BlockBox[] { paragraph },
            context.SourceMap,
            width: 200,
            height: 40);
        var store = new MarkdownTextFormatCacheStore();

        MarkdownTextFormatCache documentRangeCache = store.GetOrCreate(
            snapshot.SemanticDocument,
            context.ThemeSnapshot);
        MarkdownTextFormatCache independentRangeCache = store.GetOrCreate(
            snapshot.SemanticDocument,
            context.ThemeSnapshot);

        Assert.Same(documentRangeCache, independentRangeCache);
        Assert.Equal(1, store.BuildCount);
        Assert.True(documentRangeCache.RunCount >= 3);
    }

    [Fact]
    public void FormatCacheInvalidatesByDocumentAndThemeIdentity()
    {
        MarkdownLayoutContext firstContext = CreateContext();
        var firstParagraph = new InlineContainerBox(firstContext, MarkdownElementKeys.Body)
        {
            BlockIndex = firstContext.NextBlockIndex(),
        };
        firstParagraph.Add(new TextRun("first"));
        using var firstSnapshot = new LayoutSnapshot(
            new BlockBox[] { firstParagraph },
            firstContext.SourceMap,
            width: 200,
            height: 40);

        MarkdownLayoutContext secondContext = CreateContext();
        var secondParagraph = new InlineContainerBox(secondContext, MarkdownElementKeys.Body)
        {
            BlockIndex = secondContext.NextBlockIndex(),
        };
        secondParagraph.Add(new TextRun("second"));
        using var secondSnapshot = new LayoutSnapshot(
            new BlockBox[] { secondParagraph },
            secondContext.SourceMap,
            width: 200,
            height: 40);

        var store = new MarkdownTextFormatCacheStore();
        MarkdownTextFormatCache first = store.GetOrCreate(
            firstSnapshot.SemanticDocument,
            firstContext.ThemeSnapshot);
        MarkdownTextFormatCache themeChanged = store.GetOrCreate(
            firstSnapshot.SemanticDocument,
            secondContext.ThemeSnapshot);
        MarkdownTextFormatCache documentChanged = store.GetOrCreate(
            secondSnapshot.SemanticDocument,
            secondContext.ThemeSnapshot);

        Assert.NotSame(first, themeChanged);
        Assert.NotSame(themeChanged, documentChanged);
        Assert.Equal(3, store.BuildCount);
    }

    [Fact]
    public void SharedFormatCacheNavigationStaysAllocationBounded()
    {
        MarkdownLayoutContext context = CreateContext();
        var paragraph = new InlineContainerBox(context, MarkdownElementKeys.Body)
        {
            BlockIndex = context.NextBlockIndex(),
        };
        paragraph.Add(new TextRun("plain "));
        paragraph.Add(new LinkRun("linked", "https://example.test"));
        paragraph.Add(new TextRun(" tail"));
        using var snapshot = new LayoutSnapshot(
            new BlockBox[] { paragraph },
            context.SourceMap,
            width: 200,
            height: 40);
        var store = new MarkdownTextFormatCacheStore();
        MarkdownSemanticDocument document = snapshot.SemanticDocument;
        MarkdownTextFormatCache cache = store.GetOrCreate(document, context.ThemeSnapshot);
        int current = 0;

        for (int i = 0; i < 100; i++)
        {
            cache = store.GetOrCreate(document, context.ThemeSnapshot);
            current = cache.MoveAcrossBoundaries(
                current,
                (i & 1) == 0 ? 1 : -1,
                out _);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10_000; i++)
        {
            cache = store.GetOrCreate(document, context.ThemeSnapshot);
            current = cache.MoveAcrossBoundaries(
                current,
                (i & 1) == 0 ? 1 : -1,
                out _);
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.InRange(allocated, 0, 1_024);
        Assert.Equal(1, store.BuildCount);
        Assert.InRange(current, 0, document.Text.Length);
    }

    [Fact]
    public void CaretIsInactiveWithoutSelectionOrWhileVirtualChildHasFocus()
    {
        Assert.True(MarkdownAutomationPeer.IsCaretActive(
            FocusState.Keyboard,
            selectionEnabled: true,
            hasVirtualChildFocus: false));
        Assert.False(MarkdownAutomationPeer.IsCaretActive(
            FocusState.Keyboard,
            selectionEnabled: false,
            hasVirtualChildFocus: false));
        Assert.False(MarkdownAutomationPeer.IsCaretActive(
            FocusState.Keyboard,
            selectionEnabled: true,
            hasVirtualChildFocus: true));
        Assert.False(MarkdownAutomationPeer.IsCaretActive(
            FocusState.Unfocused,
            selectionEnabled: true,
            hasVirtualChildFocus: false));
    }

    [Fact]
    public void ViewportTransformUsesCenteredUniformMeetGeometry()
    {
        VectorViewportTransform transform = VectorViewportTransform.Create(
            new MarkdownVectorRectangle(10, 20, 100, 100),
            new Rect(0, 0, 200, 100));

        Assert.Equal(1, transform.Scale, precision: 3);
        Rect mapped = transform.Transform(new MarkdownVectorRectangle(10, 20, 20, 20));
        Assert.Equal(50, mapped.X, precision: 3);
        Assert.Equal(0, mapped.Y, precision: 3);
        Assert.Equal(20, mapped.Width, precision: 3);
        Assert.Equal(20, mapped.Height, precision: 3);
    }

    [Fact]
    public void VectorSemanticBoundsUsePaintTransformAndLocalHorizontalClip()
    {
        VectorSceneBox centered = CreateVectorBox(
            CreateScene(
                width: 200,
                height: 100,
                viewport: new MarkdownVectorRectangle(0, 0, 100, 100),
                semanticBounds: new MarkdownVectorRectangle(0, 20, 20, 20),
                MarkdownVectorSemanticFlags.None),
            availableWidth: 200);

        VectorSceneBox? clipped = null;
        try
        {
            Rect centeredBounds = centered.GetSemanticBounds(0);
            Assert.Equal(50, centeredBounds.X, precision: 3);
            Assert.Equal(20, centeredBounds.Y, precision: 3);
            Assert.Equal(20, centeredBounds.Width, precision: 3);
            Assert.Equal(20, centeredBounds.Height, precision: 3);

            clipped = CreateVectorBox(
                CreateScene(
                    width: 200,
                    height: 100,
                    viewport: new MarkdownVectorRectangle(0, 0, 200, 100),
                    semanticBounds: new MarkdownVectorRectangle(80, 10, 40, 30),
                    MarkdownVectorSemanticFlags.None),
                availableWidth: 100);
            Rect visible = clipped.GetVisibleSemanticBounds(0);
            Assert.Equal(100, clipped.VisibleContentBounds.Width, precision: 3);
            Assert.Equal(80, visible.X, precision: 3);
            Assert.Equal(20, visible.Width, precision: 3);
            Assert.True(clipped.SetHorizontalOffset(100));
            visible = clipped.GetVisibleSemanticBounds(0);
            Assert.Equal(0, visible.X, precision: 3);
            Assert.Equal(20, visible.Width, precision: 3);
        }
        finally
        {
            clipped?.Dispose();
            centered.Dispose();
        }
    }

    [Fact]
    public void LinkedOnlyVectorSemanticParticipatesInTextHitTestingNotSelection()
    {
        VectorSceneBox box = CreateVectorBox(
            CreateScene(
                width: 100,
                height: 100,
                viewport: new MarkdownVectorRectangle(0, 0, 100, 100),
                semanticBounds: new MarkdownVectorRectangle(10, 10, 60, 30),
                MarkdownVectorSemanticFlags.Linked),
            availableWidth: 100);
        try
        {
            var point = new Point(30, 20);

            Assert.False(box.HitTest(point, out _));
            Assert.True(box.TryGetTextPositionAt(point, out DocumentPosition position));
            Assert.Equal((box.BlockIndex, 0), (position.BlockIndex, position.InlineIndex));

            using var snapshot = new LayoutSnapshot(
                new BlockBox[] { box },
                new MarkdownSourceMap(new string(' ', 100)),
                width: 100,
                height: 100);
            Assert.True(snapshot.TryHitTestVectorText(point, out position));
            Assert.Equal("Linked node".Length, snapshot.SemanticDocument.Text.Length);
            Assert.InRange(
                snapshot.SemanticDocument.TextOffsetFromDocumentPosition(position),
                0,
                snapshot.SemanticDocument.Text.Length);
        }
        finally
        {
            box.Dispose();
        }
    }

    private static VectorSceneBox CreateVectorBox(
        MarkdownVectorScene scene,
        float availableWidth)
    {
        var builder = new MarkdownContentBuilder();
        builder.AddVectorScene(
            scene,
            new SourceSpan(0, 1),
            MarkdownStyleRole.Diagram,
            MarkdownAccessibilityRole.Diagram,
            accessibilityName: "Test diagram");
        MarkdownContent content = Assert.Single(builder.Build().Items);
        var box = new VectorSceneBox(CreateContext(), content, MarkdownElementKeys.Diagram)
        {
            BlockIndex = 0,
        };
        box.Measure(availableWidth);
        box.Arrange(0, 0, availableWidth);
        return box;
    }

    private static MarkdownVectorScene CreateScene(
        float width,
        float height,
        MarkdownVectorRectangle viewport,
        MarkdownVectorRectangle semanticBounds,
        MarkdownVectorSemanticFlags flags)
    {
        var semantic = new MarkdownVectorSemanticItem(
            sourceId: 1,
            MarkdownVectorSemanticRole.Node,
            "Linked node",
            description: null,
            parentIndex: -1,
            new SourceSpan(0, 1),
            semanticBounds,
            flags);
        IReadOnlyList<MarkdownVectorLinkAction> links =
            (flags & MarkdownVectorSemanticFlags.Linked) != 0
                ? new[] { new MarkdownVectorLinkAction(0, "https://example.test", action: null) }
                : Array.Empty<MarkdownVectorLinkAction>();
        return new MarkdownVectorScene(
            width,
            height,
            baseline: height,
            Array.Empty<MarkdownVectorCommand>(),
            referenceFontSize: 0,
            viewport,
            new[] { semantic },
            links);
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
