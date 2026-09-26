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

public sealed class InlineContextTargetingTests
{
    [Fact]
    public void InlineImageAndEmbedOffsetsPreserveUtf16OrderAcrossMixedRuns()
    {
        MarkdownLayoutContext context = CreateContext();
        var paragraph = new InlineContainerBox(context, MarkdownElementKeys.Body)
        {
            BlockIndex = context.NextBlockIndex(),
        };
        try
        {
            Assert.False(paragraph.HasInlineImages);
            Assert.False(paragraph.HasInlineEmbeds);
            paragraph.Add(new TextRun("A😀 ") { SourceSpan = new SourceSpan(0, 4) });
            var firstEmbed = new InlineEmbedRun(24, 24, static () => throw new InvalidOperationException())
            {
                SourceSpan = new SourceSpan(4, 1),
            };
            paragraph.Add(firstEmbed);
            var firstImage = new InlineImageRun(context, "first", "https://example.test/first.png")
            {
                SourceSpan = new SourceSpan(5, 1),
            };
            paragraph.Add(firstImage);
            paragraph.Add(new TextRun(" b") { SourceSpan = new SourceSpan(6, 2) });
            var secondImage = new InlineImageRun(context, "second", "https://example.test/second.png")
            {
                SourceSpan = new SourceSpan(8, 1),
            };
            paragraph.Add(secondImage);
            var secondEmbed = new InlineEmbedRun(24, 24, static () => throw new InvalidOperationException())
            {
                SourceSpan = new SourceSpan(9, 1),
            };
            paragraph.Add(secondEmbed);

            Assert.True(paragraph.HasInlineImages);
            Assert.True(paragraph.HasInlineEmbeds);
            Assert.Equal(5, paragraph.InlineImageRuns[0].Offset);
            Assert.Equal(8, paragraph.InlineImageRuns[1].Offset);

            _ = paragraph.Measure(600);
            paragraph.Arrange(12, 24, 600);
            var images = paragraph.EnumerateInlineImageRects().ToArray();
            var embeds = paragraph.EnumerateEmbedRects().ToArray();
            Assert.Equal(2, images.Length);
            Assert.Equal(2, embeds.Length);
            Assert.Same(firstImage, images[0].Run);
            Assert.Same(secondImage, images[1].Run);
            Assert.Same(firstEmbed, embeds[0].Run);
            Assert.Same(secondEmbed, embeds[1].Run);
            Assert.True(embeds[0].Rect.X < images[0].Rect.X);
            Assert.True(images[0].Rect.X < images[1].Rect.X);
            Assert.True(images[1].Rect.X < embeds[1].Rect.X);
        }
        finally
        {
            paragraph.Dispose();
        }
    }

    [Fact]
    public void VisualHitKeepsTrailingHalfOfInlineImageOwnedByImageRun()
    {
        MarkdownLayoutContext context = CreateContext();
        var paragraph = new InlineContainerBox(context, MarkdownElementKeys.Body)
        {
            BlockIndex = context.NextBlockIndex(),
        };
        try
        {
            paragraph.Add(new TextRun("before ")
            {
                SourceSpan = new SourceSpan(0, 7),
            });
            var image = new InlineImageRun(
                context,
                "audit image",
                "https://example.test/audit.png")
            {
                SourceSpan = new SourceSpan(7, 30),
            };
            paragraph.Add(image);
            var followingText = new TextRun(" after")
            {
                SourceSpan = new SourceSpan(37, 6),
            };
            paragraph.Add(followingText);

            _ = paragraph.Measure(600);
            paragraph.Arrange(12, 24, 600);
            Rect imageBounds = Assert.Single(paragraph.EnumerateInlineImageRects()).Rect;
            var trailingHalfPoint = new Point(
                imageBounds.Left + imageBounds.Width * 0.75,
                imageBounds.Top + imageBounds.Height / 2);

            Assert.True(paragraph.HitTest(trailingHalfPoint, out DocumentPosition caretPosition));
            Assert.Equal(followingText.InlineIndex, caretPosition.InlineIndex);
            Assert.Same(image, paragraph.RunAtVisualPoint(trailingHalfPoint));
        }
        finally
        {
            paragraph.Dispose();
        }
    }

    private static MarkdownLayoutContext CreateContext()
    {
        var style = new ElementStyle();
        var styles = new Dictionary<string, ElementStyle>(StringComparer.Ordinal)
        {
            [MarkdownElementKeys.Body] = style,
            [MarkdownElementKeys.ImageCaption] = style,
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
            new MarkdownSourceMap(new string(' ', 100)),
            new MarkdownExtensionRegistry(),
            FlowDirection.LeftToRight,
            language: "en-US");
    }
}
