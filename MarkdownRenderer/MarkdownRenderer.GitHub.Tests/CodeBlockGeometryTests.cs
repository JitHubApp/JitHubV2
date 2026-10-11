using System.Collections.Generic;
using Markdig.Syntax;
using MarkdownRenderer.CodeBlocks;
using MarkdownRenderer.Document;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Parsing;
using MarkdownRenderer.Theming;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Windows.UI;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

public sealed class CodeBlockGeometryTests
{
    [Fact]
    public void CachedHighlightSpansAreNotReappliedToAnUnchangedBlock()
    {
        var style = new ElementStyle();
        var styles = new Dictionary<string, ElementStyle>
        {
            [MarkdownElementKeys.Body] = style,
            [MarkdownElementKeys.CodeBlock] = style,
        };
        Color transparent = Color.FromArgb(0, 0, 0, 0);
        var theme = new ThemeSnapshot(
            styles,
            new Dictionary<string, ElementStyleOverride>(),
            transparent, transparent, transparent, transparent,
            isDark: false, isHighContrast: false, textScaleFactor: 1);
        var context = new MarkdownLayoutContext(
            CanvasDevice.GetSharedDevice(), theme, new MarkdownSourceMap("code"),
            new MarkdownExtensionRegistry(), FlowDirection.LeftToRight);
        var metadata = CodeBlockMetadata.FromDeclarative(
            new SourceSpan(0, 3), "code", "text", new Dictionary<string, string>());
        var block = new CodeBlockBox(context, metadata, "code", false, false);
        CodeBlockHighlightSpan[] spans = [new(0, 2, Color.FromArgb(255, 1, 2, 3))];
        var firstKey = new CodeBlockHighlightCacheKey("text", 1, 4, CodeBlockThemeVariant.Light, 1, 1);
        var revisedKey = new CodeBlockHighlightCacheKey(
            firstKey.Language, firstKey.CodeHash, firstKey.CodeLength,
            firstKey.ThemeVariant, firstKey.ProviderIdentity, ProviderRevision: 2);

        try
        {
            Assert.True(block.ApplySyntaxHighlighting(firstKey, spans));
            Assert.True(block.HasAppliedSyntaxHighlighting(firstKey));
            Assert.False(block.ApplySyntaxHighlighting(firstKey, spans));
            Assert.True(block.ApplySyntaxHighlighting(
                revisedKey, [new(0, 2, Color.FromArgb(255, 4, 5, 6))]));
        }
        finally
        {
            block.Dispose();
        }
    }

    [Theory]
    [InlineData(0, 0, 1, 1, true)]
    [InlineData(0, 0, 0, 1, false)]
    [InlineData(0, 0, 1, 0, false)]
    [InlineData(double.NaN, 0, 1, 1, false)]
    [InlineData(0, double.PositiveInfinity, 1, 1, false)]
    [InlineData(0, 0, double.PositiveInfinity, 1, false)]
    [InlineData(0, 0, 1, double.NaN, false)]
    [InlineData(double.MaxValue, 0, 1, 1, false)]
    [InlineData(0, 0, double.MaxValue, 1, false)]
    public void DrawableRectangleRequiresFinitePositiveGeometry(
        double x,
        double y,
        double width,
        double height,
        bool expected)
    {
        Assert.Equal(expected, CodeBlockBox.IsDrawableRectangle(new Rect(x, y, width, height)));
    }

    [Fact]
    public void InlineRangeRectanglesPreserveLogicalAndBufferRangeSemantics()
    {
        const string text = "selection geometry";
        CanvasDevice device = CanvasDevice.GetSharedDevice();
        var box = CreateInlineBox(device, text);
        box.Add(new TextRun(text));

        try
        {
            box.Measure(400);
            box.Arrange(0, 0, 400);

            var logicalRange = new DocumentRange(
                new DocumentPosition(1, 0, 2),
                new DocumentPosition(1, 0, 7));
            Rect[] logicalRects = box.GetRangeRects(logicalRange).ToArray();
            Rect[] bufferRects = box.GetBufferRangeRects(2, 5).ToArray();

            Assert.NotEmpty(logicalRects);
            Assert.Equal(bufferRects, logicalRects);
            Assert.Empty(box.GetRangeRects(new DocumentRange(
                new DocumentPosition(1, 0, 4),
                new DocumentPosition(1, 0, 4))));
            // Logical range geometry expects document-order endpoints; normal
            // selection callers normalize before reaching this leaf box.
            Assert.Empty(box.GetRangeRects(new DocumentRange(
                new DocumentPosition(1, 0, 7),
                new DocumentPosition(1, 0, 2))));
            Assert.Single(box.GetBufferRangeRects(4, 0));
            Assert.Empty(box.GetBufferRangeRects(text.Length, 0));
            Assert.Empty(box.GetBufferRangeLineRects(text.Length, 0));

            Rect[] lineRects = box.GetBufferRangeLineRects(2, 5).ToArray();
            Assert.Single(lineRects);
            Assert.True(lineRects[0].Width > 0);
            Assert.True(lineRects[0].Height > 0);
        }
        finally
        {
            box.Dispose();
        }
    }

    [Fact]
    public void InlineRangeRectanglesMapAcrossAdjacentRuns()
    {
        const string text = "alpha beta gamma";
        CanvasDevice device = CanvasDevice.GetSharedDevice();
        var box = CreateInlineBox(device, text);
        box.Add(new TextRun("alpha "));
        box.Add(new TextRun("beta "));
        box.Add(new TextRun("gamma"));

        try
        {
            box.Measure(400);
            box.Arrange(0, 0, 400);

            // Starts inside the first run and ends inside the third. The
            // equivalent buffer offsets cross both run boundaries.
            var logicalRange = new DocumentRange(
                new DocumentPosition(1, 0, 2),
                new DocumentPosition(1, 2, 3));
            Rect[] logicalRects = box.GetRangeRects(logicalRange).ToArray();
            Rect[] bufferRects = box.GetBufferRangeRects(2, 12).ToArray();

            Assert.NotEmpty(logicalRects);
            Assert.Equal(bufferRects, logicalRects);
        }
        finally
        {
            box.Dispose();
        }
    }

    [Fact]
    public void InlineRangeRectanglesIncludeWholeBoxOnlyForRangesCrossingItsBlock()
    {
        const string text = "middle block";
        CanvasDevice device = CanvasDevice.GetSharedDevice();
        var box = CreateInlineBox(device, text);
        box.Add(new TextRun(text));

        try
        {
            box.Measure(400);
            box.Arrange(0, 0, 400);

            Rect[] fullBox = box.GetBufferRangeRects(0, text.Length).ToArray();
            Rect[] spanningBlocks = box.GetRangeRects(new DocumentRange(
                new DocumentPosition(0, 0, 0),
                new DocumentPosition(2, 0, 0))).ToArray();

            Assert.NotEmpty(fullBox);
            Assert.Equal(fullBox, spanningBlocks);
            Assert.Empty(box.GetRangeRects(new DocumentRange(
                new DocumentPosition(0, 0, 0),
                new DocumentPosition(0, 0, 1))));
            Assert.Empty(box.GetRangeRects(new DocumentRange(
                new DocumentPosition(2, 0, 0),
                new DocumentPosition(3, 0, 1))));
        }
        finally
        {
            box.Dispose();
        }
    }

    [Theory]
    [InlineData(-5, 2, 0, 2)]
    [InlineData(3, int.MaxValue, 3, 15)]
    [InlineData(int.MinValue, int.MaxValue, 0, 18)]
    public void BufferRangeRectanglesClampNegativeAndOversizedArguments(
        int from,
        int length,
        int expectedFrom,
        int expectedLength)
    {
        const string text = "selection geometry";
        CanvasDevice device = CanvasDevice.GetSharedDevice();
        var box = CreateInlineBox(device, text);
        box.Add(new TextRun(text));

        try
        {
            box.Measure(400);
            box.Arrange(0, 0, 400);

            Rect[] actual = box.GetBufferRangeRects(from, length).ToArray();
            Rect[] expected = box.GetBufferRangeRects(expectedFrom, expectedLength).ToArray();

            Assert.NotEmpty(expected);
            Assert.Equal(expected, actual);
            Assert.Empty(box.GetBufferRangeRects(int.MaxValue, 1));
        }
        finally
        {
            box.Dispose();
        }
    }

    [Fact]
    public void BufferLineRectanglesSelectWrappedLineAtCharacterBoundary()
    {
        const string text = "alpha beta gamma delta epsilon zeta eta theta";
        CanvasDevice device = CanvasDevice.GetSharedDevice();
        var box = CreateInlineBox(device, text);
        box.Add(new TextRun(text));

        try
        {
            box.Measure(100);
            box.Arrange(0, 0, 100);

            CanvasLineMetrics[]? lines = box.GetLineMetricsSnapshot();
            if (lines is null)
            {
                Rect[] characterRects = box.GetBufferRangeRects(0, text.Length).ToArray();
                Rect[] fallbackRects = box.GetBufferRangeLineRects(0, text.Length).ToArray();
                Assert.NotEmpty(characterRects);
                Assert.Equal(characterRects, fallbackRects);
                return;
            }

            Assert.True(lines.Length >= 3, $"Expected wrapped text; got {lines.Length} lines.");
            Assert.All(lines, line => Assert.True(line.CharacterCount > 0));

            int secondLineStart = lines[0].CharacterCount;
            int secondLineLength = lines[1].CharacterCount;
            Rect[] secondLine = box.GetBufferRangeLineRects(secondLineStart, secondLineLength).ToArray();

            Rect selectedLine = Assert.Single(secondLine);
            Assert.Equal(box.GetTextOriginY() + lines[0].Height, selectedLine.Y, precision: 1);
            Assert.True(selectedLine.Width > 0);
            Assert.True(selectedLine.Height > 0);
        }
        finally
        {
            box.Dispose();
        }
    }

    [Fact]
    public void EmptyInlineBufferProducesNoLogicalOrBufferRectangles()
    {
        const string text = "";
        CanvasDevice device = CanvasDevice.GetSharedDevice();
        var box = CreateInlineBox(device, text);
        box.Add(new TextRun(text));

        try
        {
            box.Measure(400);
            box.Arrange(0, 0, 400);

            var emptyRange = new DocumentRange(
                new DocumentPosition(1, 0, 0),
                new DocumentPosition(1, 0, 0));
            Assert.Empty(box.GetRangeRects(emptyRange));
            Assert.Empty(box.GetBufferRangeRects(0, 0));
            Assert.Empty(box.GetBufferRangeLineRects(0, 0));
        }
        finally
        {
            box.Dispose();
        }
    }

    private static InlineContainerBox CreateInlineBox(CanvasDevice device, string source)
    {
        var style = new ElementStyle();
        Color transparent = Color.FromArgb(0, 0, 0, 0);
        var theme = new ThemeSnapshot(
            new Dictionary<string, ElementStyle> { [MarkdownElementKeys.Body] = style },
            new Dictionary<string, ElementStyleOverride>(),
            transparent, transparent, transparent, transparent,
            isDark: false, isHighContrast: false, textScaleFactor: 1);
        var context = new MarkdownLayoutContext(
            device,
            theme,
            new MarkdownSourceMap(source),
            new MarkdownExtensionRegistry(),
            FlowDirection.LeftToRight);
        return new InlineContainerBox(context, MarkdownElementKeys.Body) { BlockIndex = 1 };
    }
}
