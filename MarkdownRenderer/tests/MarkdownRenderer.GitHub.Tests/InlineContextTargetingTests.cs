using MarkdownRenderer.Document;
using MarkdownRenderer.Extensions;
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

public sealed class InlineContextTargetingTests
{
    [Fact]
    public void LinkedHeading_InheritsHeadingTypographyInsteadOfBodySizedLinkTypography()
    {
        var body = new ElementStyle { FontFamily = "Segoe UI", FontSize = 16f };
        var heading = new ElementStyle
        {
            FontFamily = "Segoe UI",
            FontSize = 28f,
            FontWeight = new Windows.UI.Text.FontWeight { Weight = 600 },
        };
        var linkStyle = new ElementStyle
        {
            FontFamily = "Segoe UI",
            FontSize = 16f,
            FontWeight = new Windows.UI.Text.FontWeight { Weight = 400 },
            Foreground = Color.FromArgb(255, 9, 105, 218),
        };
        MarkdownLayoutContext context = CreateContext(
            style: body,
            headingStyle: heading,
            linkStyle: linkStyle);
        var plain = new InlineContainerBox(context, MarkdownElementKeys.Heading2)
        {
            BlockIndex = context.NextBlockIndex(),
        };
        var linked = new InlineContainerBox(context, MarkdownElementKeys.Heading2)
        {
            BlockIndex = context.NextBlockIndex(),
        };
        try
        {
            var plainRun = new TextRun("roadmap.sh");
            var linkedRun = new LinkRun("roadmap.sh", "https://roadmap.sh/");
            plain.Add(plainRun);
            linked.Add(linkedRun);
            _ = plain.Measure(600f);
            _ = linked.Measure(600f);
            plain.Arrange(0f, 0f, 600f);
            linked.Arrange(0f, 0f, 600f);

            Rect plainRect = plain.GetRunRect(plainRun.InlineIndex);
            Rect linkedRect = linked.GetRunRect(linkedRun.InlineIndex);
            Assert.True(plainRect.Width > 0);
            Assert.Equal(plainRect.Width, linkedRect.Width, precision: 1);
            Assert.Equal(plainRect.Height, linkedRect.Height, precision: 1);
        }
        finally
        {
            plain.Dispose();
            linked.Dispose();
        }
    }

    [Fact]
    public void TallInlineImage_UsesContentAwareRtlLayoutAndKeepsImageHitGeometry()
    {
        var bodyStyle = new ElementStyle
        {
            FontFamily = "Segoe UI",
            FontSize = 14f,
            LineHeightMultiplier = 1.4f,
        };
        MarkdownLayoutContext context = CreateContext(
            FlowDirection.RightToLeft,
            "ar-SA",
            bodyStyle);
        var paragraph = new InlineContainerBox(context, MarkdownElementKeys.Body)
        {
            BlockIndex = context.NextBlockIndex(),
            TextAlignment = CanvasHorizontalAlignment.Right,
        };
        try
        {
            paragraph.Add(new TextRun("واجهة "));
            var image = new InlineImageRun(
                context,
                "Rust logo",
                "https://example.test/rust.svg",
                requestedWidth: new SafeHtmlLength(120f, IsPercent: false),
                requestedHeight: new SafeHtmlLength(155f, IsPercent: false));
            paragraph.Add(image);
            paragraph.Add(new LineBreakRun(isHard: true));
            var followingText = new TextRun(
                "النص العربي يلتف حول الرموز التعبيرية 😀 ويستمر عبر عدة أسطر لاختبار مواضع الفقرات واتجاه الكتابة.");
            paragraph.Add(followingText);

            float measuredHeight = paragraph.Measure(220f);
            paragraph.Arrange(12f, 24f, 220f);
            Rect imageBounds = Assert.Single(paragraph.EnumerateInlineImageRects()).Rect;
            Rect followingBounds = paragraph.GetRunRect(followingText.InlineIndex);
            CanvasLineMetrics[] lineMetrics = Assert.IsType<CanvasLineMetrics[]>(
                paragraph.GetLineMetricsSnapshot());
            var imageCenter = new Point(
                imageBounds.Left + imageBounds.Width / 2,
                imageBounds.Top + imageBounds.Height / 2);

            Assert.Equal(120d, imageBounds.Width, precision: 1);
            Assert.Equal(155d, imageBounds.Height, precision: 1);
            Assert.True(measuredHeight >= imageBounds.Height + 18d);
            Assert.True(
                lineMetrics[0].Height >= imageBounds.Height,
                $"The image line should accommodate the object: line={lineMetrics[0].Height:F2}, image={imageBounds.Height:F2}.");
            Assert.True(
                followingBounds.Top >= imageBounds.Bottom - 0.5,
                $"Following RTL text at y={followingBounds.Top:F2} overlaps the image ending at y={imageBounds.Bottom:F2}.");
            Assert.True(lineMetrics.Length >= 3, "The long Arabic/emoji run should wrap after the image line.");
            Assert.Same(image, paragraph.RunAtVisualPoint(imageCenter));
        }
        finally
        {
            paragraph.Dispose();
        }
    }

    [Fact]
    public void TextOnlyWrappedEmoji_KeepsExplicitCssLineHeight()
    {
        var bodyStyle = new ElementStyle
        {
            FontFamily = "Segoe UI",
            FontSize = 14f,
            LineHeightMultiplier = 1.4f,
        };
        MarkdownLayoutContext context = CreateContext(style: bodyStyle);
        var paragraph = new InlineContainerBox(context, MarkdownElementKeys.Body)
        {
            BlockIndex = context.NextBlockIndex(),
        };
        try
        {
            paragraph.Add(new TextRun(
                "😀 text-only line-height stays explicit while this sentence wraps across multiple lines."));
            _ = paragraph.Measure(150f);
            paragraph.Arrange(0f, 0f, 150f);

            CanvasLineMetrics[] lineMetrics = Assert.IsType<CanvasLineMetrics[]>(
                paragraph.GetLineMetricsSnapshot());

            Assert.True(lineMetrics.Length >= 2);
            Assert.All(
                lineMetrics,
                static line => Assert.Equal(19.6f, line.Height, precision: 1));
        }
        finally
        {
            paragraph.Dispose();
        }
    }

    [Fact]
    public void InlineEmbedsAndVectorScenes_ExpandTheirOwnLines()
    {
        var bodyStyle = new ElementStyle
        {
            FontFamily = "Segoe UI",
            FontSize = 14f,
            LineHeightMultiplier = 1.4f,
        };

        MarkdownLayoutContext embedContext = CreateContext(style: bodyStyle);
        var embedParagraph = new InlineContainerBox(embedContext, MarkdownElementKeys.Body)
        {
            BlockIndex = embedContext.NextBlockIndex(),
        };
        try
        {
            embedParagraph.Add(new InlineEmbedRun(
                120f,
                155f,
                static () => throw new InvalidOperationException()));
            embedParagraph.Add(new LineBreakRun(isHard: true));
            embedParagraph.Add(new TextRun("After embedded content"));
            _ = embedParagraph.Measure(300f);
            embedParagraph.Arrange(0f, 0f, 300f);

            CanvasLineMetrics[] embedLines = Assert.IsType<CanvasLineMetrics[]>(
                embedParagraph.GetLineMetricsSnapshot());

            Assert.True(embedLines[0].Height >= 155f);
            Assert.True(embedLines.Length >= 2);
        }
        finally
        {
            embedParagraph.Dispose();
        }

        MarkdownLayoutContext vectorContext = CreateContext(style: bodyStyle);
        var vectorParagraph = new InlineContainerBox(vectorContext, MarkdownElementKeys.Body)
        {
            BlockIndex = vectorContext.NextBlockIndex(),
        };
        try
        {
            var scene = new MarkdownVectorScene(
                80f,
                155f,
                155f,
                Array.Empty<MarkdownVectorCommand>());
            vectorParagraph.Add(new InlineVectorSceneRun(
                vectorContext,
                scene,
                semanticText: "vector",
                accessibilityName: "Vector scene",
                accessibilityDescription: null,
                elementKey: MarkdownElementKeys.Math));
            vectorParagraph.Add(new LineBreakRun(isHard: true));
            vectorParagraph.Add(new TextRun("After vector content"));
            _ = vectorParagraph.Measure(300f);
            vectorParagraph.Arrange(0f, 0f, 300f);

            CanvasLineMetrics[] vectorLines = Assert.IsType<CanvasLineMetrics[]>(
                vectorParagraph.GetLineMetricsSnapshot());

            Assert.True(vectorLines[0].Height >= 155f);
            Assert.True(vectorLines.Length >= 2);
        }
        finally
        {
            vectorParagraph.Dispose();
        }
    }

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

    private static MarkdownLayoutContext CreateContext(
        FlowDirection flowDirection = FlowDirection.LeftToRight,
        string language = "en-US",
        ElementStyle? style = null,
        ElementStyle? headingStyle = null,
        ElementStyle? linkStyle = null)
    {
        style ??= new ElementStyle();
        var styles = new Dictionary<string, ElementStyle>(StringComparer.Ordinal)
        {
            [MarkdownElementKeys.Body] = style,
            [MarkdownElementKeys.ImageCaption] = style,
            [MarkdownElementKeys.Heading2] = headingStyle ?? style,
            [MarkdownElementKeys.Link] = linkStyle ?? style,
            [MarkdownElementKeys.Math] = style,
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
            flowDirection,
            language: language);
    }
}
