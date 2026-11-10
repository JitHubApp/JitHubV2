using MarkdownRenderer.GitHub.Tests.TestSupport.Fixtures;
using MarkdownRenderer.Html;
using MarkdownRenderer.Layout.Internal.Inlines;
using MarkdownRenderer.Layout.Internal.Inlines.Runs;
using MarkdownRenderer.Layout.Internal.Snapshots;
using MarkdownRenderer.Parsing.Internal.SafeHtml;
using Xunit;
using static MarkdownRenderer.GitHub.Tests.TestSupport.Fixtures.SafeHtmlRenderingFixture;

namespace MarkdownRenderer.GitHub.Tests.Html.Internal.Renderers;

/// <summary>
/// Checks the image-like GitHub HTML: alt text fallbacks, picture source
/// selection, comments, video attachments, emoji images and inline SVG.
/// </summary>
public sealed class SafeHtmlImageMarkupTests
{
    [Fact]
    public void MissingAltAnimatedImageUsesItsFileNameLikeGitHub()
    {
        const string source = "<p><img src='/art/android-PullRefreshLayout.gif' width='49%'></p>";

        using LayoutSnapshot snapshot = Build(source, SafeHtmlOptions.Default);
        InlineImageRun image = Assert.Single(FlattenRuns(snapshot).OfType<InlineImageRun>());

        Assert.Equal("android-PullRefreshLayout.gif", image.AltText);
        Assert.Contains("android-PullRefreshLayout.gif", snapshot.SemanticDocument.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingAltStaticImageKeepsTheLocalizedGenericName()
    {
        const string source = "<p><img src='/art/preview.webp'></p>";

        using LayoutSnapshot snapshot = Build(source, SafeHtmlOptions.Default);
        InlineImageRun image = Assert.Single(FlattenRuns(snapshot).OfType<InlineImageRun>());

        Assert.Equal("Image", image.AltText);
    }

    [Fact]
    public void GitHubStandaloneHtmlCommentsAreNotRendered()
    {
        const string source = """
            Before

            <!-- generated-section:start -->

            After
            """;

        using LayoutSnapshot snapshot = Build(source, SafeHtmlOptions.Default);
        string rendered = string.Concat(FlattenRuns(snapshot).Select(static run => run.Text));

        Assert.Contains("Before", rendered, StringComparison.Ordinal);
        Assert.Contains("After", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("generated-section", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("<!--", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void GitHubMultilineHtmlCommentsSuppressContainedMarkdownAndImages()
    {
        const string source = """
            Visible before

            <!-- hidden:start
            ![must not load](https://example.test/hidden.png)
            **hidden contributor**
            hidden:end -->

            Visible after
            """;

        using LayoutSnapshot snapshot = Build(source, SafeHtmlOptions.Default);
        InlineRun[] runs = FlattenRuns(snapshot).ToArray();
        string rendered = string.Concat(runs.Select(static run => run.Text));

        Assert.Contains("Visible before", rendered, StringComparison.Ordinal);
        Assert.Contains("Visible after", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("hidden contributor", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain(runs, static run => run is InlineImageRun);
    }

    [Fact]
    public void GitHubPictureMarkupUsesThemeMatchedSourceWithoutLeakingHtml()
    {
        const string source = """
            <picture>
              <source media="(prefers-color-scheme: dark)" srcset="dark.svg">
              <source media="(prefers-color-scheme: light)" srcset="light.svg">
              <img src="fallback.svg" alt="Project logo" width="240">
            </picture>
            """;

        using LayoutSnapshot snapshot = Build(source, SafeHtmlOptions.Default);
        InlineRun[] runs = FlattenRuns(snapshot).ToArray();

        InlineImageRun image = Assert.Single(runs.OfType<InlineImageRun>());
        Assert.Equal("light.svg", image.Url);
        Assert.Equal("Project logo", image.AltText);
        Assert.DoesNotContain("<picture", string.Concat(runs.Select(static run => run.Text)));
    }

    [Fact]
    public void GitHubPictureMarkupUsesSingleTypedRasterSource()
    {
        const string source = """
            <picture>
              <source type="image/gif" srcset="animation.gif">
              <img src="poster.jpg" alt="Animated preview">
            </picture>
            """;

        SafeHtmlDocument parsed = SafeHtmlParser.Parse(source);
        SafeHtmlElement picture = Assert.IsType<SafeHtmlElement>(Assert.Single(parsed.Root.Children));
        SafeHtmlElement typedSource = Assert.Single(
            picture.Children.OfType<SafeHtmlElement>(),
            static element => element.Name == "source");
        Assert.Equal("image/gif", typedSource.Attributes["type"]);
        Assert.Equal("animation.gif", typedSource.Attributes["srcset"]);

        using LayoutSnapshot snapshot = Build(source, SafeHtmlOptions.Default);
        InlineImageRun image = Assert.Single(FlattenRuns(snapshot).OfType<InlineImageRun>());

        Assert.Equal("animation.gif", image.Url);
        Assert.Equal("Animated preview", image.AltText);
    }

    [Theory]
    [InlineData("<source type='image/avif' srcset='candidate.avif'>")]
    [InlineData("<source type='image/gif' srcset='one.gif 1x, two.gif 2x'>")]
    [InlineData("<source type='image/gif' srcset='one.gif 2x'>")]
    [InlineData("<source type='image/gif' media='(min-width: 500px)' srcset='candidate.gif'>")]
    [InlineData("<source media='(prefers-color-scheme: light) and (min-width: 500px)' srcset='candidate.gif'>")]
    [InlineData("<source type='image/avif' media='(prefers-color-scheme: light)' srcset='candidate.avif'>")]
    [InlineData("<source type='image/gif' sizes='50vw' srcset='candidate.gif'>")]
    [InlineData("<source srcset='candidate.gif'>")]
    public void GitHubPictureMarkupUsesFallbackWhenSourceSelectionIsAmbiguousOrUnsupported(string sourceMarkup)
    {
        string source = "<picture>\n" + sourceMarkup +
            "\n<img src='poster.jpg' alt='Fallback preview'>\n</picture>";

        using LayoutSnapshot snapshot = Build(source, SafeHtmlOptions.Default);
        InlineImageRun image = Assert.Single(FlattenRuns(snapshot).OfType<InlineImageRun>());

        Assert.Equal("poster.jpg", image.Url);
        Assert.Equal("Fallback preview", image.AltText);
    }

    [Fact]
    public void ExplicitEmptyImageAltRemainsDecorative()
    {
        const string source = "<img src='decoration.png' alt='' width='16' height='1'>";

        using LayoutSnapshot snapshot = Build(source, SafeHtmlOptions.Default);
        InlineImageRun image = Assert.Single(FlattenRuns(snapshot).OfType<InlineImageRun>());

        Assert.Empty(image.AltText);
        Assert.Empty(image.AccessibleText);
        Assert.Empty(FlattenRuns(snapshot).OfType<InlineSpacerRun>());
    }

    [Fact]
    public void GitHubVideoAttachmentUsesAtomicSafePlaceholderWithoutLeakingSignedMarkup()
    {
        const string source =
            "<video src='https://private-user-images.githubusercontent.com/demo.mp4?jwt=secret' " +
            "width='640' height='360' controls muted></video>";

        using LayoutSnapshot snapshot = Build(source, SafeHtmlOptions.Default);
        InlineImageRun media = Assert.Single(FlattenRuns(snapshot).OfType<InlineImageRun>());
        string rendered = string.Concat(FlattenRuns(snapshot).Select(static run => run.Text));

        Assert.StartsWith("data:image/svg+xml;base64,", media.Url, StringComparison.Ordinal);
        Assert.Null(media.LinkUrl);
        media.Measure(800, 20);
        Assert.InRange(media.DesiredWidth, 639, 641);
        Assert.InRange(media.DesiredHeight, 359, 361);
        Assert.DoesNotContain("jwt", rendered, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<video", rendered, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GitHubImageEmojiUsesTheNormalSizedInlineImagePipeline()
    {
        const string source = "Native :rocket: emoji and custom :octocat: image.";

        using LayoutSnapshot snapshot = BuildGitHub(source);
        InlineContainerBox paragraph = Assert.Single(snapshot.Blocks.OfType<InlineContainerBox>());
        InlineImageRun image = Assert.Single(paragraph.Runs.OfType<InlineImageRun>());
        var placement = Assert.Single(paragraph.EnumerateInlineImageRects());

        Assert.Equal(":octocat:", image.AltText);
        Assert.Equal(
            "https://github.githubassets.com/images/icons/emoji/octocat.png",
            image.Url);
        Assert.Equal(20, placement.Rect.Width, precision: 1);
        Assert.Equal(20, placement.Rect.Height, precision: 1);
        Assert.Contains(
            paragraph.Runs.OfType<TextRun>(),
            static run => run.Text.Contains("🚀", StringComparison.Ordinal));
    }

    [Fact]
    public void GitHubInlineSvgUsesTheIsolatedSvgImagePipeline()
    {
        const string source = """
            <a href="https://applitools.example/">
            <svg width="170" height="32" viewBox="0 0 170 32" xmlns="http://www.w3.org/2000/svg">
              <title>Applitools</title>
              <path d="M0 0h170v32H0z" fill="#00A298"></path>
            </svg>
            </a>
            """;

        using LayoutSnapshot snapshot = Build(source, SafeHtmlOptions.Default);
        InlineRun[] runs = FlattenRuns(snapshot).ToArray();

        InlineImageRun image = Assert.Single(runs.OfType<InlineImageRun>());
        Assert.StartsWith("data:image/svg+xml;base64,", image.Url, StringComparison.Ordinal);
        Assert.Equal("Applitools", image.AltText);
        Assert.Equal("https://applitools.example/", image.LinkUrl);
        Assert.Equal(170, image.Image.MeasuredImageWidth);
        string markup = System.Text.Encoding.UTF8.GetString(
            Convert.FromBase64String(image.Url[(image.Url.IndexOf(',') + 1)..]));
        Assert.Contains("<path", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("<svg", string.Concat(runs.Select(static run => run.Text)));
    }
}
