using System.Collections.Generic;
using Markdig.Syntax;
using MarkdownRenderer.CodeBlocks;
using MarkdownRenderer.Document;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Parsing;
using MarkdownRenderer.Theming;
using Microsoft.Graphics.Canvas;
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
        var revisedKey = firstKey with { ProviderRevision = 2 };

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
}
