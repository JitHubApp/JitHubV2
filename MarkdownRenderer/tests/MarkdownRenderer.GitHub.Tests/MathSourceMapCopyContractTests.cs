using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MarkdownRenderer.Accessibility;
using MarkdownRenderer.Document;
using MarkdownRenderer.Extensions;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Math;
using MarkdownRenderer.Parsing;
using MarkdownRenderer.Theming;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Windows.UI;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

public sealed class MathSourceMapCopyContractTests
{
    [Fact]
    public async Task BlockMathCopyUsesTheSameAtomicPositionAsSemanticsAndSelectionGeometry()
    {
        const string source = "$$\r\\frac{1}{2}\r$$";
        using MarkdownEngine engine = new MarkdownEngineBuilder()
            .UseProfile(MarkdownProfiles.GfmStrict)
            .UseMathematics()
            .Build();
        MarkdownDocument document = await engine.ParseAsync(source);
        Assert.Empty(document.Diagnostics);

        MarkdownSourceMap sourceMap = new(document.Source);
        MarkdownLayoutContext context = CreateContext(sourceMap);
        using LayoutSnapshot snapshot = new LayoutBuilder(context, semanticDocument: document)
            .Build(document.ParsedDocument!, availableWidth: 640f);

        VectorSceneBox math = Assert.Single(snapshot.Blocks.OfType<VectorSceneBox>());
        Assert.Equal(MarkdownAccessibilityRole.Math, math.AccessibilityRole);
        Assert.Empty(math.SemanticItems);
        Assert.True(math.Bounds.Width > 0);
        Assert.True(math.Bounds.Height > 0);

        MarkdownTextSpan semanticSpan = Assert.Single(
            snapshot.SemanticDocument.TextSpans,
            span => ReferenceEquals(span.VectorSceneBox, math));
        Assert.Equal(-1, semanticSpan.VectorSemanticIndex);
        DocumentPosition semanticStart = Assert.IsType<DocumentPosition>(
            snapshot.SemanticDocument.PositionFromTextOffset(semanticSpan.TextStart));
        DocumentPosition semanticEnd = Assert.IsType<DocumentPosition>(
            snapshot.SemanticDocument.PositionFromTextOffset(semanticSpan.TextEnd));

        // Unstructured vector scenes (including block math) are one atomic
        // document slot. Slot zero is shared with the source-map entry and the
        // selection endpoint APIs; -1 remains only the semantic-span sentinel.
        var expectedStart = new DocumentPosition(math.BlockIndex, 0, 0);
        var expectedEnd = new DocumentPosition(math.BlockIndex, 0, 1);
        Assert.Equal(expectedStart, semanticStart);
        Assert.Equal(expectedEnd, semanticEnd);
        Assert.Equal(semanticSpan.TextStart,
            snapshot.SemanticDocument.TextOffsetFromDocumentPosition(semanticStart));
        Assert.Equal(semanticSpan.TextEnd,
            snapshot.SemanticDocument.TextOffsetFromDocumentPosition(semanticEnd));
        var selectedMath = new DocumentRange(semanticStart, semanticEnd);

        Assert.True(sourceMap.TryMapRange(selectedMath, out SourceSpan sourceSpan));
        Assert.Equal(new SourceSpan(0, source.Length), sourceSpan);
        Assert.Equal(source, sourceMap.Slice(selectedMath));

        double pointerY = math.Bounds.Top + math.Bounds.Height / 2;
        var pointerStartPoint = new Point(math.Bounds.Left + 1, pointerY);
        var pointerEndPoint = new Point(math.Bounds.Right - 1, pointerY);
        Assert.True(math.HitTest(pointerStartPoint, out DocumentPosition pointerStart));
        Assert.True(math.HitTest(pointerEndPoint, out DocumentPosition pointerEnd));
        Assert.Equal(expectedStart, pointerStart);
        Assert.Equal(expectedEnd, pointerEnd);
        double endpointX = math.Bounds.Left + math.Bounds.Width / 2;
        var endpointBeforePoint = new Point(endpointX, math.Bounds.Top + 1);
        var endpointAfterPoint = new Point(endpointX, math.Bounds.Bottom - 1);
        Assert.True(math.HitTestSelectionEndpoint(endpointBeforePoint, out DocumentPosition endpointStart));
        Assert.True(math.HitTestSelectionEndpoint(endpointAfterPoint, out DocumentPosition endpointEnd));
        Assert.Equal(expectedStart, endpointStart);
        Assert.Equal(expectedEnd, endpointEnd);

        Rect selectedBounds = Assert.Single(math.GetSelectionRects(selectedMath));
        Assert.Equal(math.Bounds, selectedBounds);
    }

    private static MarkdownLayoutContext CreateContext(MarkdownSourceMap sourceMap)
    {
        var style = new ElementStyle { FontFamily = "Segoe UI", FontSize = 16f };
        string[] keys =
        [
            MarkdownElementKeys.Body,
            MarkdownElementKeys.Math,
            MarkdownElementKeys.Diagram,
        ];
        Color transparent = Color.FromArgb(0, 0, 0, 0);
        var theme = new ThemeSnapshot(
            keys.ToDictionary(static key => key, _ => style, System.StringComparer.Ordinal),
            new Dictionary<string, ElementStyleOverride>(),
            transparent,
            transparent,
            transparent,
            transparent,
            isDark: false,
            isHighContrast: false,
            textScaleFactor: 1f);
        return new MarkdownLayoutContext(
            CanvasDevice.GetSharedDevice(),
            theme,
            sourceMap,
            new MarkdownExtensionRegistry(),
            FlowDirection.LeftToRight);
    }
}
