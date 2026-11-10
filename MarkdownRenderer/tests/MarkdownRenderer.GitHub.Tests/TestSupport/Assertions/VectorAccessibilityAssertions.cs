using MarkdownRenderer.Accessibility.Internal.Semantics;
using MarkdownRenderer.Controls.Internal.Input.Links;
using MarkdownRenderer.Document;
using MarkdownRenderer.Extensions;
using MarkdownRenderer.Layout.Internal.Boxes;
using MarkdownRenderer.Layout.Internal.Inlines;
using MarkdownRenderer.Layout.Internal.Inlines.Runs;
using MarkdownRenderer.Layout.Internal.Snapshots;
using MarkdownRenderer.Layout.Internal.Vectors;
using MarkdownRenderer.Layout.Internal.Vectors.Drawing;
using MarkdownRenderer.Selection;
using MarkdownRenderer.Selection.Internal;
using MarkdownRenderer.Theming;
using MarkdownRenderer.Theming.Internal;
using Windows.Foundation;
using Windows.UI;
using Xunit;
using LinkTarget = MarkdownRenderer.Controls.Internal.Input.Links.LinkTarget;

namespace MarkdownRenderer.GitHub.Tests.TestSupport.Assertions;

/// <summary>
/// Scene fixtures, pixel readers and shared assertions for
/// <see cref="VectorAccessibilityContractTests"/>,
/// <see cref="VectorSelectionGeometryTests"/> and
/// <see cref="VectorKeyboardFocusTests"/>.
/// </summary>
internal static class VectorAccessibilityAssertions
{
    /// <summary>
    /// Collects the visible selection rectangles and adorner backgrounds inside one paint pass.
    /// </summary>
    internal static (List<Rect> Rects, List<SelectionBackgroundGeometry> Backgrounds) CollectVisibleHighlights(
        LayoutSnapshot snapshot,
        SelectionController selection,
        double width,
        int targetHeight)
    {
        var selectionRects = new List<Rect>();
        var selectionBackgrounds = new List<SelectionBackgroundGeometry>();
        Assert.True(snapshot.TryBeginPaint());
        try
        {
            selection.AppendVisibleHighlightRects(
                snapshot,
                new Rect(0, 0, width, targetHeight),
                selectionRects,
                selectionBackgrounds);
        }
        finally
        {
            snapshot.EndPaint();
        }

        return (selectionRects, selectionBackgrounds);
    }

    /// <summary>
    /// Asserts that the selection background shows only through the transparent scene
    /// content, and that scene ink and the outline keep their selected colors.
    /// </summary>
    internal static void AssertSelectedTransparentScenePixels(
        Color[] pixels,
        VectorSceneBox vector,
        ThemeSnapshot theme,
        Color selectedBackground,
        Color white,
        bool highContrast)
    {
        Rect contentBounds = vector.VisibleContentBounds;
        Color transparentScenePixel = ReadPixel(
            pixels,
            100,
            contentBounds.Left + 5,
            contentBounds.Top + 5);
        Color blackScenePixel = ReadPixel(
            pixels,
            100,
            contentBounds.Left + 11,
            contentBounds.Top + 11);
        Color outlinePixel = ReadPixel(
            pixels,
            100,
            contentBounds.Left,
            contentBounds.Top + contentBounds.Height / 2);
        Color outsideScenePixel = ReadPixel(
            pixels,
            100,
            vector.Bounds.Right - 10,
            contentBounds.Top + 5);

        Color expectedTransparentPixel = highContrast
            ? selectedBackground
            : BlendOverWhite(selectedBackground, 0.25f);
        AssertColorClose(expectedTransparentPixel, transparentScenePixel, tolerance: 2);
        Assert.Equal(white, outsideScenePixel);
        AssertColorClose(
            highContrast
                ? theme.SelectionForegroundColor
                : Color.FromArgb(0xFF, 0, 0, 0),
            blackScenePixel,
            tolerance: 1);
        AssertColorClose(
            highContrast
                ? theme.SelectionForegroundColor
                : selectedBackground,
            outlinePixel,
            tolerance: 8);
    }

    /// <summary>
    /// Creates a diagram with one linked and one plain text label that share one semantic paint style.
    /// </summary>
    internal static MarkdownVectorScene CreateLinkedTextStyleScene(
        MarkdownVectorPaintStyle sharedPaint,
        out MarkdownVectorCommand[] commands)
    {
        var hostText = new MarkdownVectorTextStyle(
            "Scene fallback",
            18,
            fontWeight: 650,
            italic: true,
            fontRole: MarkdownVectorFontRole.Host);
        MarkdownVectorSemanticItem[] semantics =
        [
            new(
                1,
                MarkdownVectorSemanticRole.Diagram,
                "Text style diagram",
                null,
                -1,
                new SourceSpan(0, 10),
                new MarkdownVectorRectangle(0, 0, 160, 50)),
            new(
                2,
                MarkdownVectorSemanticRole.Node,
                "Linked label",
                null,
                0,
                new SourceSpan(0, 5),
                new MarkdownVectorRectangle(5, 5, 70, 30),
                MarkdownVectorSemanticFlags.Linked),
            new(
                3,
                MarkdownVectorSemanticRole.Node,
                "Plain label",
                null,
                0,
                new SourceSpan(6, 4),
                new MarkdownVectorRectangle(85, 5, 70, 30)),
        ];
        commands =
        [
            MarkdownVectorCommand.DrawText(
                "Linked label",
                new MarkdownVectorPoint(10, 25),
                hostText,
                sharedPaint,
                semanticIndex: 1),
            MarkdownVectorCommand.DrawText(
                "Plain label",
                new MarkdownVectorPoint(90, 25),
                new MarkdownVectorTextStyle("Authored face", 14, fontWeight: 350),
                sharedPaint,
                semanticIndex: 2),
        ];
        return new MarkdownVectorScene(
            160,
            50,
            40,
            commands,
            referenceFontSize: 16,
            new MarkdownVectorRectangle(0, 0, 160, 50),
            semantics,
            [new MarkdownVectorLinkAction(1, "https://example.test/linked", action: null)]);
    }

    internal static Color ResolveFill(MarkdownVectorCommand command, VectorScenePaintPalette palette) =>
        VectorScenePaintResolver.ResolveColor(
            command.Style!.FillArgb,
            command.Style.Opacity,
            palette,
            command.Style.FillRole,
            highContrast: false);

    /// <summary>
    /// Asserts that only the linked math atom yields a pointer and keyboard link target with its URL, title and source.
    /// </summary>
    internal static void AssertLinkedMathActivationTargets(
        InlineContainerBox paragraph,
        InlineVectorSceneRun math,
        InlineVectorSceneRun unlinkedMath,
        string linkUrl,
        string linkTitle)
    {
        Assert.True(LinkTargets.TryCreateLinkedAtomicTarget(math, out LinkTarget target));
        Assert.False(LinkTargets.TryCreateLinkedAtomicTarget(unlinkedMath, out _));
        LinkTarget? keyboardTarget = LinkTargets.FindLinkTargetInBlock(
            paragraph,
            new DocumentPosition(paragraph.BlockIndex, math.InlineIndex, 0),
            point: null);

        Assert.NotNull(keyboardTarget);
        Assert.Equal(linkUrl, target.Url);
        Assert.Equal(linkTitle, target.Title);
        Assert.Equal(math.SourceSpan, target.SourceSpan);
        Assert.Null(target.Run);
        Assert.Equal(target.Url, keyboardTarget.Value.Url);
        Assert.Equal(target.Title, keyboardTarget.Value.Title);
        Assert.Equal(target.SourceSpan, keyboardTarget.Value.SourceSpan);
    }

    /// <summary>
    /// Asserts that linked math projects one Math semantic node, its text span and one
    /// boundary space, and no separate Link node.
    /// </summary>
    internal static void AssertLinkedMathHasOneSemanticNode(
        LayoutSnapshot snapshot,
        InlineVectorSceneRun math,
        string semanticText,
        string linkTitle)
    {
        MarkdownSemanticDocument document = snapshot.SemanticDocument;
        MarkdownSemanticNode mathNode = Assert.Single(
            MarkdownSemanticDocument.EnumerateDepthFirst(document.Root),
            node => ReferenceEquals(node.InlineRun, math));
        Assert.Equal(MarkdownSemanticRole.Math, mathNode.Role);
        Assert.Equal("x squared", mathNode.AccessibilityName);
        Assert.Equal(linkTitle, mathNode.HelpText);
        Assert.Equal(semanticText, document.GetText(mathNode));
        MarkdownTextSpan mathSpan = Assert.Single(
            document.TextSpans,
            span => ReferenceEquals(span.InlineRun, math) &&
                span.TextStart == mathNode.TextStart && span.TextEnd == mathNode.TextEnd);
        Assert.Equal(semanticText, document.Text[mathSpan.TextStart..mathSpan.TextEnd]);
        // Adjacent atomic labels need a semantic space, not a second Math
        // node or a duplicate formula. The boundary maps to this same atom.
        MarkdownTextSpan boundary = Assert.Single(
            document.TextSpans,
            span => ReferenceEquals(span.InlineRun, math) &&
                span.TextStart == mathNode.TextEnd);
        Assert.Equal(" ", document.Text[boundary.TextStart..boundary.TextEnd]);
        Assert.Equal(2, document.TextSpans.Count(span => ReferenceEquals(span.InlineRun, math)));
        Assert.DoesNotContain(
            MarkdownSemanticDocument.EnumerateDepthFirst(document.Root),
            node => ReferenceEquals(node.InlineRun, math) && node.Role == MarkdownSemanticRole.Link);
    }

    internal static Color ReadPixel(Color[] pixels, int width, double x, double y)
    {
        int pixelX = System.Math.Clamp((int)System.Math.Floor(x), 0, width - 1);
        int pixelY = System.Math.Clamp((int)System.Math.Floor(y), 0, pixels.Length / width - 1);
        return pixels[pixelY * width + pixelX];
    }

    internal static Color BlendOverWhite(Color foreground, float opacity)
    {
        float alpha = System.Math.Clamp(
            System.MathF.Round(foreground.A * opacity),
            0f,
            255f) / 255f;
        return Color.FromArgb(
            0xFF,
            BlendChannel(foreground.R, alpha),
            BlendChannel(foreground.G, alpha),
            BlendChannel(foreground.B, alpha));
    }

    private static byte BlendChannel(byte foreground, float alpha) =>
        (byte)System.Math.Clamp(
            (int)System.MathF.Round(foreground * alpha + 255f * (1f - alpha)),
            0,
            255);

    internal static void AssertColorClose(Color expected, Color actual, int tolerance)
    {
        Assert.InRange(System.Math.Abs(expected.A - actual.A), 0, tolerance);
        Assert.InRange(System.Math.Abs(expected.R - actual.R), 0, tolerance);
        Assert.InRange(System.Math.Abs(expected.G - actual.G), 0, tolerance);
        Assert.InRange(System.Math.Abs(expected.B - actual.B), 0, tolerance);
    }

    internal static Color ToColor(uint argb) => Color.FromArgb(
        (byte)(argb >> 24),
        (byte)(argb >> 16),
        (byte)(argb >> 8),
        (byte)argb);
}
