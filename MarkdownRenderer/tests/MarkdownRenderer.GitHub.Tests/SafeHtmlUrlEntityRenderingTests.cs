using MarkdownRenderer.Accessibility;
using MarkdownRenderer.Document;
using MarkdownRenderer.GitHub;
using MarkdownRenderer.Html;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Parsing;
using MarkdownRenderer.Selection;
using MarkdownRenderer.Theming;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml;
using Windows.UI;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

public sealed class SafeHtmlUrlEntityRenderingTests
{
    [Theory]
    [InlineData("", "")]
    [InlineData("prefix ", " suffix")]
    [InlineData("<p>", "</p>")]
    [InlineData("<div>\n\n", "\n\n</div>")]
    public async Task AuthoredLinkKeepsItsOnceDecodedDestinationInLayoutAndHtmlCopy(
        string prefix,
        string suffix)
    {
        const string anchor = "<a href=\"https://example.test/?q=&amp;amp;v=1\">label</a>";
        using LayoutSnapshot snapshot = await Build(prefix + anchor + suffix);
        LinkRun run = Assert.Single(Runs(snapshot).OfType<LinkRun>());

        Assert.Equal("https://example.test/?q=&amp;v=1", run.Url);
        Assert.Equal("label", run.Text);
        MarkdownSemanticDocument semantic = snapshot.SemanticDocument;
        MarkdownSemanticNode link = Assert.Single(
            MarkdownSemanticDocument.EnumerateDepthFirst(semantic.Root),
            static node => node.Role == MarkdownSemanticRole.Link);
        Assert.Equal("label", semantic.GetText(link));
        Assert.True(semantic.TryGetDocumentRange(link.TextStart, link.TextEnd, out DocumentRange range));
        Assert.False(range.IsEmpty);
        // CF_HTML must re-encode the semantic URL once, preserving the original
        // destination when the receiving application decodes the attribute.
        Assert.Equal(
            "<div data-markdown-renderer=\"1\">" + anchor + "</div>",
            MarkdownRenderedHtmlWriter.BuildFragment(semantic, link.TextStart, link.TextEnd));
    }

    [Theory]
    [InlineData("", "")]
    [InlineData("prefix ", " suffix")]
    [InlineData("<p>", "</p>")]
    public async Task LinkedImageKeepsBothOnceDecodedUrlsAndAuthoredImageSource(
        string prefix,
        string suffix)
    {
        const string image = "<img src=\"https://example.test/badge.png?q=&amp;amp;v=1\" alt=\"Badge\">";
        string source = prefix + "<a href=\"https://example.test/?q=&amp;amp;v=1\">" +
            image + "</a>" + suffix;
        using LayoutSnapshot snapshot = await Build(source);
        InlineImageRun run = Assert.Single(Runs(snapshot).OfType<InlineImageRun>());

        Assert.Equal("https://example.test/badge.png?q=&amp;v=1", run.Url);
        Assert.Equal("https://example.test/?q=&amp;v=1", run.LinkUrl);
        Assert.Equal("Badge", run.AltText);
        Assert.Equal(image, source.Substring(run.SourceSpan.Start, run.SourceSpan.Length));
        MarkdownSemanticNode imageNode = Assert.Single(
            MarkdownSemanticDocument.EnumerateDepthFirst(snapshot.SemanticDocument.Root),
            static node => node.Role == MarkdownSemanticRole.Image);
        Assert.Equal("Badge", snapshot.SemanticDocument.GetText(imageNode));
    }

    [Fact]
    public async Task PictureSourceSetIsNotDecodedAgainAfterSourceSelection()
    {
        const string source = "<p><picture><source type='image/png' " +
            "srcset='https://example.test/selected.png?q=&amp;amp;v=1'>" +
            "<img src='fallback.png' alt='Picture'></picture></p>";
        using LayoutSnapshot snapshot = await Build(source);

        InlineImageRun run = Assert.Single(Runs(snapshot).OfType<InlineImageRun>());
        Assert.Equal("https://example.test/selected.png?q=&amp;v=1", run.Url);
        Assert.Equal("Picture", run.AltText);
    }

    [Theory]
    [InlineData("", "")]
    [InlineData("prefix ", " suffix")]
    [InlineData("<p>", "</p>")]
    public async Task VideoPosterKeepsItsOnceDecodedDestination(string prefix, string suffix)
    {
        const string video = "<video src='clip.mp4' " +
            "poster='https://example.test/poster.png?q=&amp;amp;v=1' " +
            "aria-label='Clip'></video>";
        using LayoutSnapshot snapshot = await Build(prefix + video + suffix);

        InlineImageRun run = Assert.Single(Runs(snapshot).OfType<InlineImageRun>());
        Assert.Equal("https://example.test/poster.png?q=&amp;v=1", run.Url);
        Assert.Equal("Clip", run.AltText);
    }

    [Theory]
    [InlineData("&amp;amp;", "&amp;")]
    [InlineData("&amp;lt;script&amp;gt;", "&lt;script&gt;")]
    [InlineData("&#38;amp;", "&amp;")]
    public async Task BlockImageFallbackDoesNotDecodeAuthoredAltTwiceWhenImagesDisabled(
        string authoredAlt,
        string expectedText)
    {
        string source = $"<p><img src='https://example.test/image.png' alt='{authoredAlt}'></p>";
        using LayoutSnapshot snapshot = await Build(source, enableImages: false);
        InlineRun[] runs = Runs(snapshot).ToArray();

        Assert.DoesNotContain(runs, static run => run is InlineImageRun);
        Assert.Equal(expectedText, string.Concat(runs.OfType<TextRun>().Select(static run => run.Text)));
    }

    [Theory]
    [InlineData("&amp;amp;", "&amp;")]
    [InlineData("&amp;lt;script&amp;gt;", "&lt;script&gt;")]
    [InlineData("&#38;amp;", "&amp;")]
    public async Task BlockImageFallbackDoesNotDecodeAuthoredAltTwiceForRejectedSource(
        string authoredAlt,
        string expectedText)
    {
        string source = $"<p><img src='javascript:alert(1)' alt='{authoredAlt}'></p>";
        using LayoutSnapshot snapshot = await Build(source, enableImages: true);
        InlineRun[] runs = Runs(snapshot).ToArray();

        Assert.DoesNotContain(runs, static run => run is InlineImageRun);
        Assert.Equal(expectedText, string.Concat(runs.OfType<TextRun>().Select(static run => run.Text)));
    }

    [Theory]
    [InlineData(false, "https://example.test/image.png", "&amp;amp;", "&amp;")]
    [InlineData(false, "https://example.test/image.png", "&amp;lt;script&amp;gt;", "&lt;script&gt;")]
    [InlineData(false, "https://example.test/image.png", "&#38;amp;", "&amp;")]
    [InlineData(true, "javascript:alert(1)", "&amp;amp;", "&amp;")]
    [InlineData(true, "javascript:alert(1)", "&amp;lt;script&amp;gt;", "&lt;script&gt;")]
    [InlineData(true, "javascript:alert(1)", "&#38;amp;", "&amp;")]
    public async Task InlineImageFallbackKeepsTheSingleDecodedAltText(
        bool enableImages,
        string imageSource,
        string authoredAlt,
        string expectedText)
    {
        string source = $"before <img src='{imageSource}' alt='{authoredAlt}'> after";
        using LayoutSnapshot snapshot = await Build(source, enableImages);
        InlineRun[] runs = Runs(snapshot).ToArray();

        Assert.DoesNotContain(runs, static run => run is InlineImageRun);
        Assert.Equal($"before {expectedText} after", string.Concat(runs.Select(static run => run.Text)));
    }

    [Theory]
    [InlineData("R&amp;amp;D", "R&amp;D")]
    [InlineData("R&#38;amp;D", "R&amp;D")]
    [InlineData("<em>R&amp;amp;D</em>", "R&amp;D")]
    public async Task DetailsDisclosurePreservesAlreadyDecodedSummaryText(
        string authoredSummary,
        string expectedText)
    {
        string source = $"<details><summary>{authoredSummary}</summary><p>body</p></details>";
        using LayoutSnapshot snapshot = await Build(source);
        LinkRun disclosure = Assert.Single(
            Runs(snapshot).OfType<LinkRun>(),
            static run => run.DisclosureId is not null);

        Assert.Equal($"\u25B8 {expectedText}", disclosure.Text);
        Assert.Equal(expectedText, disclosure.AccessibilityName);
    }

    [Theory]
    [InlineData("R&amp;amp;D", "R&amp;D")]
    [InlineData("R&#38;amp;D", "R&amp;D")]
    [InlineData("<em>R&amp;amp;D</em>", "R&amp;D")]
    public async Task MarkdownBearingGitHubDetailsPreservesDecodedSummaryText(
        string authoredSummary,
        string expectedText)
    {
        string source = $"<details>\n<summary>{authoredSummary}</summary>\nbody\n</details>";
        using LayoutSnapshot snapshot = await Build(
            source,
            inspectParsedDocument: static parsed =>
                Assert.Contains(parsed, static block => block is GitHubDetailsBlock));
        LinkRun disclosure = Assert.Single(
            Runs(snapshot).OfType<LinkRun>(),
            static run => run.DisclosureId is not null);

        Assert.Equal($"\u25B8 {expectedText}", disclosure.Text);
        Assert.Equal(expectedText, disclosure.AccessibilityName);
    }

    private static async Task<LayoutSnapshot> Build(
        string source,
        bool enableImages = true,
        Action<Markdig.Syntax.MarkdownDocument>? inspectParsedDocument = null)
    {
        using MarkdownEngine engine = new MarkdownEngineBuilder()
            .UseGitHubReadme(new SafeHtmlOptions(enableImages: enableImages))
            .Build();
        MarkdownDocument semanticDocument = await engine.ParseAsync(source);
        Markdig.Syntax.MarkdownDocument document = semanticDocument.ParsedDocument ??
            throw new InvalidOperationException("The engine did not retain its parsed document.");
        inspectParsedDocument?.Invoke(document);
        MarkdownExtensionRegistry registry = Assert.IsType<MarkdownExtensionRegistry>(
            engine.PresentationConfiguration);
        var style = new ElementStyle();
        var theme = new ThemeSnapshot(
            new Dictionary<string, ElementStyle>
            {
                [MarkdownElementKeys.Body] = style,
                [MarkdownElementKeys.Link] = style,
                [MarkdownElementKeys.ImageCaption] = style,
            },
            new Dictionary<string, ElementStyleOverride>(),
            default(Color), default, default, default,
            isDark: false, isHighContrast: false, textScaleFactor: 1);
        var context = new MarkdownLayoutContext(
            CanvasDevice.GetSharedDevice(forceSoftwareRenderer: true),
            theme,
            new MarkdownSourceMap(source),
            registry,
            FlowDirection.LeftToRight);
        // No image resolver is installed: these tests inspect URL identity,
        // semantics and copy without network access or loading remote images.
        return new LayoutBuilder(context, semanticDocument: semanticDocument).Build(document, 600);
    }

    private static IEnumerable<InlineRun> Runs(LayoutSnapshot snapshot) =>
        snapshot.Blocks.SelectMany(Runs);

    private static IEnumerable<InlineRun> Runs(BlockBox block) => block switch
    {
        InlineContainerBox inline => inline.Runs,
        StackBox stack => stack.Children.SelectMany(Runs),
        _ => [],
    };
}
