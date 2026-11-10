using MarkdownRenderer.Accessibility.Internal.Semantics;
using MarkdownRenderer.Document;
using MarkdownRenderer.Extensions;
using MarkdownRenderer.Layout.Internal.Boxes;
using MarkdownRenderer.Layout.Internal.Context;
using MarkdownRenderer.Layout.Internal.Snapshots;
using MarkdownRenderer.Layout.Internal.Vectors;
using MarkdownRenderer.Selection;
using MarkdownRenderer.Selection.Internal;
using MarkdownRenderer.Theming;
using Microsoft.Graphics.Canvas;
using Windows.Foundation;
using Windows.UI;
using Xunit;
using static MarkdownRenderer.GitHub.Tests.VectorAccessibilityAssertions;
using static MarkdownRenderer.GitHub.Tests.VectorAccessibilityFixture;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>
/// Checks selection over vector scenes: atomic selection endpoints, the
/// repaint above opaque and transparent selection fills, and document
/// range rectangles compared with a linear oracle.
/// </summary>
public sealed class VectorSelectionGeometryTests
{
    [Fact]
    public void AtomicVectorSelectionEndpointUsesFullBoundsButOrdinaryHitTestStaysSemantic()
    {
        MarkdownVectorScene scene = CreateSemanticScene(includeLink: false);
        MarkdownLayoutContext context = CreateContext();
        var vector = new VectorSceneBox(context, CreateContent(scene), MarkdownElementKeys.Diagram)
        {
            BlockIndex = 2,
        };
        vector.Measure(240);
        vector.Arrange(0, 40, 240);
        using var snapshot = new LayoutSnapshot(
            new BlockBox[] { vector },
            context.SourceMap,
            width: 240,
            height: (float)vector.Bounds.Bottom);
        var upperBackground = new Point(vector.Bounds.Left + 2, vector.Bounds.Top + 2);
        var lowerBackground = new Point(vector.Bounds.Left + 2, vector.Bounds.Bottom - 2);

        Assert.False(vector.HitTest(upperBackground, out _));
        Assert.True(snapshot.HitTestSelectionEndpoint(upperBackground, out DocumentPosition before));
        Assert.Equal(new DocumentPosition(2, 0, 0), before);

        Assert.True(snapshot.HitTestSelectionEndpoint(lowerBackground, out DocumentPosition after));
        Assert.Equal(new DocumentPosition(2, 0, 1), after);
        Assert.False(snapshot.HitTestSelectionEndpoint(
            new Point(vector.Bounds.Right + 1, vector.Bounds.Bottom + 1),
            out _));

        var selectionFromPreviousBlock = new DocumentPosition(1, 0, 0);
        Assert.Empty(vector.GetSelectionRects(new DocumentRange(selectionFromPreviousBlock, before)));
        Assert.Single(vector.GetSelectionRects(new DocumentRange(selectionFromPreviousBlock, after)));
        Assert.Empty(vector.GetSelectionRects(new DocumentRange(
            after,
            new DocumentPosition(3, 0, 0))));
        Assert.Single(vector.GetSelectionRects(new DocumentRange(
            new DocumentPosition(2, 1, 0),
            new DocumentPosition(2, 1, 1))));
        Assert.Single(vector.GetSelectionRects(new DocumentRange(
            new DocumentPosition(2, 1, 0),
            new DocumentPosition(3, 0, 0))));

        const string source = "before\n```mermaid\ngraph TD\n```";
        var sourceMap = new MarkdownSourceMap(source);
        sourceMap.Add(1, 0, 6, new SourceSpan(0, 6));
        sourceMap.Add(2, 0, 1, new SourceSpan(7, source.Length - 7));
        Assert.Equal(
            source,
            sourceMap.Slice(new DocumentRange(selectionFromPreviousBlock, after)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SelectedVectorSceneRepaintsAboveOpaqueSelectionFill(bool highContrast)
    {
        var scene = new MarkdownVectorScene(
            width: 20,
            height: 20,
            baseline: 16,
            commands:
            [
                MarkdownVectorCommand.FillRectangle(
                    new MarkdownVectorRectangle(0, 0, 20, 20),
                    0xFFFF0000),
            ],
            referenceFontSize: 16);
        MarkdownLayoutContext context = CreateContext(isHighContrast: highContrast);
        var vector = new VectorSceneBox(context, CreateContent(scene), MarkdownElementKeys.Diagram)
        {
            BlockIndex = 1,
        };
        vector.Measure(100);
        vector.Arrange(0, 0, 100);

        try
        {
            int targetHeight = System.Math.Max(1, (int)System.Math.Ceiling(vector.Bounds.Height));
            using var target = new CanvasRenderTarget(
                context.ResourceCreator,
                100,
                targetHeight,
                96);
            Color selectionBackground = Color.FromArgb(0xFF, 0x00, 0x66, 0xCC);
            using (CanvasDrawingSession drawingSession = target.CreateDrawingSession())
            {
                drawingSession.Clear(Color.FromArgb(0, 0, 0, 0));
                drawingSession.FillRectangle(vector.Bounds, selectionBackground);
                vector.PaintSelectionForeground(
                    drawingSession,
                    new DocumentRange(
                        new DocumentPosition(1, 0, 0),
                        new DocumentPosition(1, 0, 1)),
                    context.ThemeSnapshot.SelectionForegroundColor,
                    new Rect(0, 0, 100, targetHeight));
            }

            int sampleX = System.Math.Clamp((int)vector.ContentBounds.Left + 10, 0, 99);
            int sampleY = System.Math.Clamp((int)vector.ContentBounds.Top + 10, 0, targetHeight - 1);
            Color pixel = target.GetPixelColors()[sampleY * 100 + sampleX];
            if (highContrast)
                Assert.Equal(context.ThemeSnapshot.SelectionForegroundColor, pixel);
            else
                Assert.NotEqual(selectionBackground, pixel);
        }
        finally
        {
            vector.Dispose();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SelectedTransparentVectorUsesClippedPaintBackgroundWithoutChangingSelectionGeometry(
        bool highContrast)
    {
        var scene = new MarkdownVectorScene(
            width: 20,
            height: 20,
            baseline: 16,
            commands:
            [
                MarkdownVectorCommand.FillRectangle(
                    new MarkdownVectorRectangle(10, 10, 4, 4),
                    0xFF000000),
            ],
            referenceFontSize: 16);
        MarkdownLayoutContext context = CreateContext(isHighContrast: highContrast);
        var vector = new VectorSceneBox(context, CreateContent(scene), MarkdownElementKeys.Diagram)
        {
            BlockIndex = 1,
        };
        vector.Measure(100);
        vector.Arrange(0, 0, 100);

        try
        {
            int targetHeight = System.Math.Max(1, (int)System.Math.Ceiling(vector.Bounds.Height));
            using var target = new CanvasRenderTarget(
                context.ResourceCreator,
                100,
                targetHeight,
                96);
            using var snapshot = new LayoutSnapshot(
                new BlockBox[] { vector },
                context.SourceMap,
                width: 100,
                height: targetHeight);
            var selection = new SelectionController();
            selection.SetAnchor(new DocumentPosition(1, 0, 0));
            selection.ExtendTo(new DocumentPosition(1, 0, 1));
            (List<Rect> selectionRects, List<SelectionBackgroundGeometry> selectionBackgrounds) =
                CollectVisibleHighlights(snapshot, selection, width: 100, targetHeight);

            // Selection, caret, and hit geometry continue to cover the full
            // atomic block. Only the adorner background follows scene content.
            Assert.Equal(vector.Bounds, Assert.Single(selectionRects));
            SelectionBackgroundGeometry background = Assert.Single(selectionBackgrounds);
            Assert.Equal(vector.VisibleContentBounds, background.Bounds);
            Assert.True(background.Bounds.Width < vector.Bounds.Width);
            Assert.Equal(0.25f, background.Opacity);
            Assert.True(vector.HitTestSelectionEndpoint(
                new Point(vector.Bounds.Right - 1, vector.Bounds.Top + 1),
                out _));

            Color white = Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF);
            Color selectedBackground = context.ThemeSnapshot.SelectionHighlightColor;
            using (CanvasDrawingSession drawingSession = target.CreateDrawingSession())
            {
                drawingSession.Clear(white);
                background.Paint(drawingSession, selectedBackground, highContrast);
                vector.PaintSelectionForeground(
                    drawingSession,
                    selection.Range,
                    context.ThemeSnapshot.SelectionForegroundColor,
                    new Rect(0, 0, 100, targetHeight));
            }

            AssertSelectedTransparentScenePixels(
                target.GetPixelColors(), vector, context.ThemeSnapshot, selectedBackground, white, highContrast);
        }
        finally
        {
            vector.Dispose();
        }
    }

    [Fact]
    public void DocumentRangeRectsMatchLinearOracleAtInclusiveVectorBoundaries()
    {
        MarkdownLayoutContext context = CreateContext();
        var vector = new VectorSceneBox(context, CreateContent(CreateSemanticScene(includeLink: false)), MarkdownElementKeys.Diagram)
        {
            BlockIndex = context.NextBlockIndex(),
        };
        vector.Measure(100);
        vector.Arrange(10, 20, 100);
        using var snapshot = new LayoutSnapshot(
            new BlockBox[] { vector },
            context.SourceMap,
            width: 100,
            height: (float)vector.Bounds.Bottom);

        MarkdownSemanticDocument builtDocument = snapshot.SemanticDocument;
        Assert.Contains(builtDocument.TextSpans, static span => span.VectorSceneBox is not null);
        Assert.NotEmpty(builtDocument.GetDocumentRects(0, 5));
        for (int index = 1; index < builtDocument.TextSpans.Count; index++)
        {
            MarkdownTextSpan previous = builtDocument.TextSpans[index - 1];
            MarkdownTextSpan current = builtDocument.TextSpans[index];
            Assert.True(previous.TextStart <= current.TextStart);
            Assert.True(previous.TextEnd <= current.TextStart);
            Assert.True(previous.TextEnd <= current.TextEnd);
        }

        var spans = new List<MarkdownTextSpan>(builtDocument.TextSpans);
        int boundary = spans[0].TextEnd;
        MarkdownTextSpan firstSpan = spans[0];
        Assert.NotNull(firstSpan.VectorSceneBox);
        spans.Insert(
            1,
            new MarkdownTextSpan(
                boundary,
                boundary,
                null,
                null,
                null,
                null,
                null,
                firstSpan.VectorSceneBox,
                firstSpan.VectorSemanticIndex));
        MarkdownSemanticDocument document = CreateSyntheticSemanticDocument(
            builtDocument.Text,
            builtDocument.Root,
            spans);

        for (int start = 0; start <= document.Text.Length; start++)
        {
            for (int end = start; end <= document.Text.Length; end++)
            {
                foreach (bool expand in new[] { false, true })
                {
                    Assert.Equal(
                        GetLinearDocumentRects(document, start, end, expand),
                        document.GetDocumentRects(start, end, expand).ToArray());
                }
            }
        }
    }

    [Fact]
    public void DocumentRangeRectsUseIndexedStartForFarTailRange()
    {
        const int spanCount = 8192;
        string text = new('x', spanCount * 2 + 1);
        var spans = new CountingSemanticSpanList(spanCount);
        for (int index = 0; index < spanCount; index++)
        {
            int start = index * 2;
            spans.Set(index, new MarkdownTextSpan(start, start + 1, null, null, null, null, null));
        }

        var root = new MarkdownSemanticNode(MarkdownSemanticRole.Document)
        {
            TextStart = 0,
            TextEnd = text.Length,
        };
        MarkdownSemanticDocument document = CreateSyntheticSemanticDocument(text, root, spans);
        spans.ResetReadCount();

        int lastSpanEnd = (spanCount - 1) * 2 + 1;
        Assert.Empty(document.GetDocumentRects(lastSpanEnd, lastSpanEnd));
        Assert.InRange(spans.IndexerReadCount, 1, 20);
    }

    private static MarkdownSemanticDocument CreateSyntheticSemanticDocument(
        string text,
        MarkdownSemanticNode root,
        IReadOnlyList<MarkdownTextSpan> spans)
        => new(root, text, spans);

    private static Rect[] GetLinearDocumentRects(
        MarkdownSemanticDocument document,
        int textStart,
        int textEnd,
        bool expandDegenerate)
    {
        textStart = System.Math.Clamp(textStart, 0, document.Text.Length);
        textEnd = System.Math.Clamp(textEnd, textStart, document.Text.Length);
        if (expandDegenerate && textStart == textEnd && document.Text.Length > 0)
        {
            if (textStart < document.Text.Length)
                textEnd = textStart + 1;
            else
                textStart = System.Math.Max(0, textStart - 1);
        }

        var result = new List<Rect>();
        foreach (MarkdownTextSpan span in document.TextSpans)
        {
            if (span.TextEnd < textStart || span.TextStart > textEnd)
                continue;
            int start = System.Math.Max(textStart, span.TextStart);
            int end = System.Math.Min(textEnd, span.TextEnd);
            if (end >= start)
            {
                IEnumerable<Rect> rectangles = MarkdownTextSpanProjection.GetDocumentRectsForSpan(span, start, end, false);
                result.AddRange(rectangles);
            }
        }

        return result.ToArray();
    }

    private sealed class CountingSemanticSpanList(int count) : IReadOnlyList<MarkdownTextSpan>
    {
        private readonly MarkdownTextSpan[] _items = new MarkdownTextSpan[count];

        internal int IndexerReadCount { get; private set; }
        public int Count => _items.Length;

        public MarkdownTextSpan this[int index]
        {
            get
            {
                IndexerReadCount++;
                return _items[index];
            }
        }

        internal void Set(int index, MarkdownTextSpan span) => _items[index] = span;
        internal void ResetReadCount() => IndexerReadCount = 0;

        public IEnumerator<MarkdownTextSpan> GetEnumerator()
        {
            for (int index = 0; index < _items.Length; index++)
                yield return this[index];
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
