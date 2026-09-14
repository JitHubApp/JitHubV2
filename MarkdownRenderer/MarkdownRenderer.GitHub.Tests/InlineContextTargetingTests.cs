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
