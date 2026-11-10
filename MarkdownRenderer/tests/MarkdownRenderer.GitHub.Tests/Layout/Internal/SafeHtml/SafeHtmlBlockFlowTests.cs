using MarkdownRenderer.Images;
using MarkdownRenderer.Layout.Internal.Boxes;
using MarkdownRenderer.Layout.Internal.Images;
using MarkdownRenderer.Layout.Internal.Inlines;
using MarkdownRenderer.Layout.Internal.Inlines.Runs;
using MarkdownRenderer.Layout.Internal.Snapshots;
using MarkdownRenderer.Parsing.Internal;
using MarkdownRenderer.Theming;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Xunit;
using static MarkdownRenderer.GitHub.Tests.TestSupport.Fixtures.SafeHtmlRenderingFixture;

namespace MarkdownRenderer.GitHub.Tests.Layout.Internal.SafeHtml;

/// <summary>
/// Checks that opening HTML blocks, picture logos and large centered images
/// reserve their flow so following headings and prose stay below them,
/// including after a late image decode and changed-block relayout.
/// </summary>
public sealed class SafeHtmlBlockFlowTests
{
    [Fact]
    public void RankEightOpeningHtmlFlowsThroughMarkdownParserAsSiblingLayoutBlocks()
    {
        const string source =
            "<p align=\"center\">\n" +
            "  <a href=\"https://roadmap.sh/\"><img src=\"https://roadmap.sh/img/brand.png\" height=\"70\"></a>\n" +
            "  <h2 align=\"center\"><a href=\"https://roadmap.sh\">roadmap.sh</a></h2>\n" +
            "  <p align=\"center\">Community-driven roadmaps, articles and resources for developers</p>\n" +
            "  <p align=\"center\">\n" +
            "    <a href=\"https://roadmap.sh/roadmaps\">\n" +
            "\t        <img src=\"https://img.shields.io/badge/%E2%9C%A8-Roadmaps%20-0a0a0a.svg?style=flat&colorA=0a0a0a\" alt=\"roadmaps\" />\n" +
            "    </a>\n" +
            "    <a href=\"https://roadmap.sh/best-practices\">\n" +
            "\t        <img src=\"https://img.shields.io/badge/%E2%9C%A8-Best%20Practices-0a0a0a.svg?style=flat&colorA=0a0a0a\" alt=\"best practices\" />\n" +
            "    </a>\n" +
            "    <a href=\"https://roadmap.sh/questions\">\n" +
            "\t        <img src=\"https://img.shields.io/badge/%E2%9C%A8-Questions-0a0a0a.svg?style=flat&colorA=0a0a0a\" alt=\"videos\" />\n" +
            "    </a>\n" +
            "    <a href=\"https://www.youtube.com/channel/UCA0H2KIWgWTwpTFjSxp0now?sub_confirmation=1\">\n" +
            "\t        <img src=\"https://img.shields.io/badge/%E2%9C%A8-YouTube%20Channel-0a0a0a.svg?style=flat&colorA=0a0a0a\" alt=\"roadmaps\" />\n" +
            "    </a>\n" +
            "  </p>\n" +
            "</p>";

        IReadOnlyDictionary<string, ElementStyle> profileStyles = CreatePrimerParagraphAndHeading2Styles();

        // BuildGitHub runs Markdig's GFM pipeline and registered
        // HtmlBlockRenderer; this checks actual production boxes and margins.
        using LayoutSnapshot snapshot = BuildGitHub(
            source,
            keyedStyles: profileStyles,
            availableWidth: 660);
        StackBox htmlBlock = Assert.IsType<StackBox>(Assert.Single(snapshot.Blocks));
        Assert.Equal(4, htmlBlock.Children.Count);
        Assert.True(htmlBlock.CollapsePositiveAdjacentMargins);

        InlineContainerBox logo = Assert.IsType<InlineContainerBox>(htmlBlock.Children[0]);
        InlineContainerBox heading = Assert.IsType<InlineContainerBox>(htmlBlock.Children[1]);
        InlineContainerBox subtitle = Assert.IsType<InlineContainerBox>(htmlBlock.Children[2]);
        InlineContainerBox badges = Assert.IsType<InlineContainerBox>(htmlBlock.Children[3]);
        Assert.Equal(MarkdownElementKeys.Heading2, heading.ElementKey);
        Assert.Contains(heading.Runs.OfType<LinkRun>(), static run => run.Text == "roadmap.sh");
        Assert.Contains(subtitle.Runs, static run => run.Text.Contains("Community-driven roadmaps", StringComparison.Ordinal));
        Assert.Single(logo.Runs.OfType<InlineImageRun>(), static image => image.Url.EndsWith("/brand.png", StringComparison.Ordinal));
        Assert.Equal(4, badges.Runs.OfType<InlineImageRun>().Count());
        Assert.DoesNotContain(FlattenRuns(snapshot), static run => run.Text.Contains("<h2", StringComparison.Ordinal));

        Assert.Equal(new Thickness(0, 0, 0, 16), logo.Margin);
        Assert.Equal(new Thickness(0, 24, 0, 16), heading.Margin);
        Assert.Equal(40, logo.Margin.Bottom + heading.Margin.Top, precision: 1);
        double headingContentTop = heading.Bounds.Top + heading.Margin.Top;
        double logoContentBottom = logo.Bounds.Bottom - logo.Margin.Bottom;
        Assert.Equal(24, headingContentTop - logoContentBottom, precision: 1);

        // The opt-in is deliberately limited to safe-HTML local flow. Plain
        // Markdown continues to add its paragraph-bottom and heading-top
        // margins, protecting the application-wide block layout contract.
        using LayoutSnapshot markdownSnapshot = BuildGitHub(
            "Paragraph.\n\n## Heading",
            keyedStyles: profileStyles,
            availableWidth: 660);
        InlineContainerBox markdownParagraph = Assert.IsType<InlineContainerBox>(markdownSnapshot.Blocks[0]);
        InlineContainerBox markdownHeading = Assert.IsType<InlineContainerBox>(markdownSnapshot.Blocks[1]);
        double markdownGap = markdownHeading.Bounds.Top + markdownHeading.Margin.Top -
            (markdownParagraph.Bounds.Bottom - markdownParagraph.Margin.Bottom);
        Assert.Equal(40, markdownGap, precision: 1);
    }

    [Fact]
    public void PictureLogoKeepsCenteredLinksAndFollowingPageContentBelowItsBounds()
    {
        const string source = """
            <p align="center"><picture>
            <source media="(prefers-color-scheme: light)" srcset="https://example.test/rust-logo.svg">
            <img src="https://example.test/rust-logo.svg" alt="Rust" width="311" height="155">
            </picture><br>
            <a href="https://www.rust-lang.org">Rust homepage</a> |
            <a href="https://doc.rust-lang.org">API documentation</a></p>
            <p>Following page content remains below the logo.</p>
            """;

        using MarkdownEngine engine = new MarkdownEngineBuilder()
            .UseGitHubReadme()
            .Build();
        var registry = Assert.IsType<MarkdownExtensionRegistry>(engine.PresentationConfiguration);
        using LayoutSnapshot snapshot = Build(source, registry, availableWidth: 660f);
        InlineContainerBox host = Assert.Single(
            FlattenBoxes(snapshot).OfType<InlineContainerBox>(),
            static box => box.Runs.OfType<InlineImageRun>().Any() &&
                          box.Runs.OfType<LinkRun>().Count() == 2);
        InlineImageRun image = Assert.Single(host.Runs.OfType<InlineImageRun>());
        Rect imageBounds = Assert.Single(host.EnumerateInlineImageRects()).Rect;
        Rect[] linkBounds = host.Runs
            .OfType<LinkRun>()
            .Select(link => host.GetRunRect(link.InlineIndex))
            .ToArray();

        Assert.Equal("https://example.test/rust-logo.svg", image.Url);
        Assert.Equal(311d, imageBounds.Width, precision: 1);
        Assert.Equal(155d, imageBounds.Height, precision: 1);
        Assert.All(linkBounds, bounds => Assert.True(
            bounds.Top >= imageBounds.Bottom - 0.5,
            $"Centered link at y={bounds.Top:F2} overlaps the picture ending at y={imageBounds.Bottom:F2}."));
        Assert.True(
            snapshot.Size.Height >= imageBounds.Bottom,
            $"Document height {snapshot.Size.Height:F2} does not contain the picture ending at y={imageBounds.Bottom:F2}.");
        Assert.Contains(
            FlattenRuns(snapshot),
            static run => run is TextRun && run.Text.Contains("Following page content", StringComparison.Ordinal));
    }

    [Fact]
    public void CenteredLargeHtmlImageReservesFlowBeforeFollowingHeadingAndProse()
    {
        const string source = """
            免费的编程中文书籍索引
            ============================

            [![](https://img.shields.io/github/issues/justjavac/free-programming-books-zh_CN.svg)](https://github.com/justjavac/free-programming-books-zh_CN/issues)  [![](https://img.shields.io/github/forks/justjavac/free-programming-books-zh_CN.svg)](https://github.com/justjavac/free-programming-books-zh_CN/network) [![](https://img.shields.io/github/stars/justjavac/free-programming-books-zh_CN.svg)](https://github.com/justjavac/free-programming-books-zh_CN/stargazers) [![](https://travis-ci.org/justjavac/free-programming-books-zh_CN.svg?branch=master)](https://travis-ci.org/justjavac/free-programming-books-zh_CN) [![](https://img.shields.io/github/release/justjavac/free-programming-books-zh_CN.svg)](https://github.com/justjavac/free-programming-books-zh_CN/releases)

            免费的编程中文书籍索引，欢迎投稿。

            - 国外程序员在 [stackoverflow](http://stackoverflow.com/questions/1711/what-is-the-single-most-influential-book-every-programmer-should-read/1713%231713) 推荐的程序员必读书籍，[中文版](http://justjavac.com/other/2012/05/15/qualified-programmer-should-read-what-books.html "一个合格的程序员应该读过哪些书")。
            - 程序员应该阅读的非编程类书籍有哪些？ [中文版](what-non-programming-books-should-programmers-read.md)（原帖为 stackoverflow 上已删除的[问题 38210](https://web.archive.org/web/20170103152138/http://stackoverflow.com/questions/38210/what-non-programming-books-should-programmers-read)）
            - [github](https://github.com/EbookFoundation/free-programming-books) 上的一个流行的编程书籍索引  [中文版](https://github.com/EbookFoundation/free-programming-books/blob/main/books/free-programming-books-zh.md)

            如果这个仓库对你有帮助，欢迎 star。如果这个仓库帮你提升了技能找到了工作，可以请我喝杯咖啡：

            <p align="center"><img src="https://example.test/buy-me-a-coffee-wechat.png" width="320" height="320" alt="" /></p>

            ## 参与交流

            欢迎大家将珍藏已久的经典免费书籍共享出来，您可以：
            """;

        using MarkdownEngine engine = new MarkdownEngineBuilder()
            .UseGitHubReadme()
            .Build();
        var registry = Assert.IsType<MarkdownExtensionRegistry>(engine.PresentationConfiguration);
        using LayoutSnapshot snapshot = Build(
            source,
            registry,
            lazy: true,
            availableWidth: 660,
            measureAllLazyViewports: false);
        InlineContainerBox imageHost = Assert.Single(
            FlattenBoxes(snapshot).OfType<InlineContainerBox>(),
            static box => box.Runs.OfType<InlineImageRun>()
                .Any(static image => image.Url == "https://example.test/buy-me-a-coffee-wechat.png"));
        InlineImageRun image = Assert.Single(imageHost.Runs.OfType<InlineImageRun>());
        Rect imageBounds = Assert.Single(imageHost.EnumerateInlineImageRects()).Rect;
        InlineContainerBox heading = Assert.Single(
            FlattenBoxes(snapshot).OfType<InlineContainerBox>(),
            static box => box.Runs.OfType<TextRun>()
                .Any(static run => run.Text.Contains("参与交流", StringComparison.Ordinal)));
        InlineContainerBox prose = Assert.Single(
            FlattenBoxes(snapshot).OfType<InlineContainerBox>(),
            static box => box.Runs.OfType<TextRun>()
                .Any(static run => run.Text.Contains("欢迎大家将珍藏已久的经典免费书籍共享出来", StringComparison.Ordinal)));

        Assert.Equal(320f, image.DesiredWidth, precision: 1);
        Assert.Equal(320f, image.DesiredHeight, precision: 1);
        Assert.Equal(320d, imageBounds.Width, precision: 1);
        Assert.Equal(320d, imageBounds.Height, precision: 1);
        Assert.True(
            imageBounds.Bottom <= heading.Bounds.Top + 0.5,
            $"The following heading at y={heading.Bounds.Top:F2} overlaps the painted 320-DIP image ending at y={imageBounds.Bottom:F2}; image paragraph flow ends at y={imageHost.Bounds.Bottom:F2}.");
        Assert.True(
            heading.Bounds.Bottom <= prose.Bounds.Top + 0.5,
            $"The following prose at y={prose.Bounds.Top:F2} overlaps its heading ending at y={heading.Bounds.Bottom:F2}.");
        Assert.True(
            imageHost.Bounds.Bottom <= heading.Bounds.Top + 0.5,
            $"The image paragraph's flow box ends at y={imageHost.Bounds.Bottom:F2}, after the following heading starts at y={heading.Bounds.Top:F2}.");
    }

    [Fact]
    public async Task LateCenteredHtmlImageDecodeKeepsFollowingContentOutOfPublishedImageBounds()
    {
        const string source = """
            If this repository helps you, you can buy me a coffee:

            <p align="center"><img src="https://example.test/buy-me-a-coffee-wechat.png" width="320" height="320" alt="" /></p>

            ## 参与交流

            欢迎大家将珍藏已久的经典免费书籍共享出来，您可以：
            """;
        var resolver = new DeferredImageResolver();
        using MarkdownEngine engine = new MarkdownEngineBuilder()
            .UseGitHubReadme()
            .Build();
        var registry = Assert.IsType<MarkdownExtensionRegistry>(engine.PresentationConfiguration);
        using LayoutSnapshot snapshot = Build(
            source,
            registry,
            lazy: true,
            availableWidth: 660,
            measureAllLazyViewports: false,
            imageResolver: resolver);

        InlineContainerBox imageHost = Assert.Single(
            FlattenBoxes(snapshot).OfType<InlineContainerBox>(),
            static box => box.Runs.OfType<InlineImageRun>().Any());
        InlineImageRun image = Assert.Single(imageHost.Runs.OfType<InlineImageRun>());
        InlineContainerBox heading = FindInlineBoxContaining(snapshot, "参与交流");
        InlineContainerBox prose = FindInlineBoxContaining(snapshot, "欢迎大家将珍藏已久的经典免费书籍共享出来");
        Rect initialImageRect = Assert.Single(imageHost.EnumerateInlineImageRects()).Rect;
        Assert.True(initialImageRect.Bottom <= heading.Bounds.Top + 0.5,
            "The initial lazy measurement must reserve the explicit 320-DIP image height.");

        var imagePublished = new TaskCompletionSource<(bool LayoutInvalidated, double HostBottom, double HeadingTop, bool HasBitmap)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<LoadCompletedEventArgs> onLoadCompleted = (_, args) =>
            imagePublished.TrySetResult((
                args.LayoutInvalidated,
                imageHost.Bounds.Bottom,
                heading.Bounds.Top,
                image.Image.Bitmap is not null));
        image.Image.LoadCompleted += onLoadCompleted;
        try
        {
            image.Image.EnsureLoading();
            await resolver.ResolveStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            resolver.Complete(MarkdownImageResolution.Resolved(new MarkdownImageAsset(
                Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR4nGP6zwAAAgcBApocMXEAAAAASUVORK5CYII="),
                ContentType: "image/png",
                CacheKey: $"safe-html-late-image-{Guid.NewGuid():N}")));

            // Observe the exact first publication point, before the control's
            // queued block relayout can run. This is where a newly available
            // CanvasBitmap can first be painted over neighboring content.
            (bool layoutInvalidated, double hostBottomAtPublish, double headingTopAtPublish, bool hasBitmap) =
                await imagePublished.Task.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.True(hasBitmap, "The resolver/decode path must publish the raster before signaling completion.");
            Assert.True(layoutInvalidated, "The first raster publication must request the host's normal relayout path.");
            Assert.True(
                hostBottomAtPublish <= headingTopAtPublish + 0.5,
                $"At bitmap publication the image paragraph ends at y={hostBottomAtPublish:F2}, after the heading starts at y={headingTopAtPublish:F2}.");

            AssertChangedBlockRelayoutKeepsFollowingContentBelowImage(snapshot, imageHost, image, heading, prose);
        }
        finally
        {
            // Always release the deferred source so a failed assertion cannot
            // leave an image-load task waiting after the snapshot is disposed.
            resolver.Complete(MarkdownImageResolution.Unavailable);
            image.Image.LoadCompleted -= onLoadCompleted;
        }
    }

    private static Dictionary<string, ElementStyle> CreatePrimerParagraphAndHeading2Styles()
    {
        // These are the canonical Primer profile's paragraph and h2 margins.
        // The test checks the production path with unequal adjacent margins,
        // where CSS normal flow keeps max(16px, 24px), not their 40px sum.
        var bodyStyle = new ElementStyle
        {
            FontSize = 16,
            LineHeightMultiplier = 1.5f,
            Margin = new Thickness(0, 0, 0, 16),
        };
        var heading2Style = new ElementStyle
        {
            FontSize = 24,
            FontWeight = new Windows.UI.Text.FontWeight { Weight = 600 },
            LineHeightMultiplier = 1.25f,
            Margin = new Thickness(0, 24, 0, 16),
        };
        return new Dictionary<string, ElementStyle>(StringComparer.Ordinal)
        {
            [MarkdownElementKeys.Body] = bodyStyle,
            [MarkdownElementKeys.Heading2] = heading2Style,
        };
    }

    private static InlineContainerBox FindInlineBoxContaining(LayoutSnapshot snapshot, string text) =>
        Assert.Single(
            FlattenBoxes(snapshot).OfType<InlineContainerBox>(),
            box => box.Runs.OfType<TextRun>()
                .Any(run => run.Text.Contains(text, StringComparison.Ordinal)));

    private static void AssertChangedBlockRelayoutKeepsFollowingContentBelowImage(
        LayoutSnapshot snapshot,
        InlineContainerBox imageHost,
        InlineImageRun image,
        InlineContainerBox heading,
        InlineContainerBox prose)
    {
        // Exercise the same changed-block relayout requested by the host
        // after the asynchronous publication.
        snapshot.RelayoutChangedBlocks(
            [imageHost.BlockIndex],
            660,
            CancellationToken.None);

        Rect loadedImageRect = Assert.Single(imageHost.EnumerateInlineImageRects()).Rect;
        Assert.NotNull(image.Image.Bitmap);
        Assert.Equal(320d, loadedImageRect.Width, precision: 1);
        Assert.Equal(320d, loadedImageRect.Height, precision: 1);
        Assert.True(
            loadedImageRect.Bottom <= heading.Bounds.Top + 0.5,
            $"After async relayout the heading at y={heading.Bounds.Top:F2} overlaps the image ending at y={loadedImageRect.Bottom:F2}.");
        Assert.True(
            heading.Bounds.Bottom <= prose.Bounds.Top + 0.5,
            $"The prose at y={prose.Bounds.Top:F2} overlaps the heading ending at y={heading.Bounds.Bottom:F2}.");
    }
}
