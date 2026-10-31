using MarkdownRenderer.Layout;
using MarkdownRenderer.Parsing;
using Xunit;

namespace MarkdownRenderer.Tests;

public sealed class SafeHtmlParserTests
{
    [Fact]
    public void ParseTagsFindsSafeTagsAcrossMixedText()
    {
        IReadOnlyList<SafeHtmlTag> tags = SafeHtmlParser.ParseTags(
            "<details><summary>Translations:</summary>\nMarkdown body");

        Assert.Equal(["details", "summary", "summary"], tags.Select(tag => tag.Name));
        Assert.Equal(SafeHtmlTagKind.Opening, tags[0].Kind);
        Assert.Equal(SafeHtmlTagKind.Closing, tags[2].Kind);
    }

    [Fact]
    public void DetailsScopeSuppressesSiblingMarkdownUntilClosingTag()
    {
        SafeHtmlBlockScopeTracker tracker = new();
        IReadOnlyDictionary<string, bool> collapsed = new Dictionary<string, bool>();

        Assert.False(tracker.Process(
            "<details>\n<summary>Translations:</summary>",
            sourceOffset: 17,
            collapsed));
        Assert.True(tracker.IsContentSuppressed);
        Assert.True(tracker.Process("</details>", sourceOffset: 100, collapsed));
        Assert.False(tracker.IsContentSuppressed);
    }

    [Fact]
    public void DetailsScopeHonorsOpenAttributeAndUserOverride()
    {
        SafeHtmlBlockScopeTracker openTracker = new();
        Assert.False(openTracker.Process(
            "<details open><summary>Visible</summary>",
            sourceOffset: 40,
            new Dictionary<string, bool>()));
        Assert.False(openTracker.IsContentSuppressed);

        SafeHtmlBlockScopeTracker overrideTracker = new();
        Assert.False(overrideTracker.Process(
            "<details><summary>Visible</summary>",
            sourceOffset: 40,
            new Dictionary<string, bool> { ["40"] = true }));
        Assert.False(overrideTracker.IsContentSuppressed);
    }

    [Fact]
    public void Parse_PreservesTopRepositoryLogoStructureAndDimensions()
    {
        const string html = """
            <div align="center">
              <a href="https://example.com/project">
                <picture>
                  <source media="(prefers-color-scheme: dark)" srcset="media/logo-dark.svg">
                  <img width="500" height="350" src="media/logo.svg" alt="Project logo">
                </picture>
              </a>
            </div>
            """;

        SafeHtmlDocument document = SafeHtmlParser.Parse(html);
        SafeHtmlElement div = Assert.IsType<SafeHtmlElement>(Assert.Single(document.Root.Children));
        Assert.Equal("div", div.Name);
        Assert.Equal(SafeHtmlAlignment.Center, SafeHtmlParser.GetAlignment(div));

        SafeHtmlElement anchor = Assert.IsType<SafeHtmlElement>(div.Children.OfType<SafeHtmlElement>().Single());
        Assert.True(SafeHtmlParser.TryGetSafeLink(anchor, out string link));
        Assert.Equal("https://example.com/project", link);

        SafeHtmlElement image = Descendants(div).Single(element => element.Name == "img");
        Assert.True(SafeHtmlParser.TryGetSafeImageSource(image, out string source));
        Assert.Equal("media/logo.svg", source);
        Assert.Equal(new SafeHtmlLength(500, false), GetLength(image, "width"));
        Assert.Equal(new SafeHtmlLength(350, false), GetLength(image, "height"));
        Assert.False(document.IsTruncated);
    }

    [Fact]
    public void Parse_RankEightDeveloperRoadmapFragmentClosesParagraphsBeforeBlockStarts()
    {
        // This is the pinned rank-8 README's opening logo, heading, subtitle,
        // and badge markup. Chromium implicitly closes the first <p> at <h2>
        // and the subtitle <p> at the badge <p>.
        const string html =
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

        SafeHtmlDocument document = SafeHtmlParser.Parse(html);
        SafeHtmlElement[] blocks = document.Root.Children.OfType<SafeHtmlElement>().ToArray();
        Assert.False(document.IsTruncated);
        Assert.Equal(["p", "h2", "p", "p"], blocks.Select(element => element.Name));

        SafeHtmlElement logoParagraph = blocks[0];
        SafeHtmlElement logoLink = Assert.Single(logoParagraph.Children.OfType<SafeHtmlElement>());
        SafeHtmlElement logo = Assert.IsType<SafeHtmlElement>(Assert.Single(logoLink.Children));
        Assert.Equal("img", logo.Name);
        Assert.True(SafeHtmlParser.TryGetSafeLink(logoLink, out string logoHref));
        Assert.Equal("https://roadmap.sh/", logoHref);
        Assert.True(SafeHtmlParser.TryGetSafeImageSource(logo, out string logoSource));
        Assert.Equal("https://roadmap.sh/img/brand.png", logoSource);

        SafeHtmlElement heading = blocks[1];
        SafeHtmlElement headingLink = Assert.IsType<SafeHtmlElement>(Assert.Single(heading.Children));
        Assert.Equal("roadmap.sh", Assert.IsType<SafeHtmlText>(Assert.Single(headingLink.Children)).DecodedText);

        SafeHtmlElement subtitle = blocks[2];
        Assert.Equal(
            "Community-driven roadmaps, articles and resources for developers",
            Assert.IsType<SafeHtmlText>(Assert.Single(subtitle.Children)).DecodedText);

        SafeHtmlElement badges = blocks[3];
        SafeHtmlElement[] badgeLinks = badges.Children.OfType<SafeHtmlElement>().ToArray();
        Assert.Equal(4, badgeLinks.Length);
        Assert.All(badgeLinks, link => Assert.True(SafeHtmlParser.TryGetSafeLink(link, out _)));
        Assert.All(badgeLinks, link =>
            Assert.True(SafeHtmlParser.TryGetSafeImageSource(
                Assert.Single(link.Children.OfType<SafeHtmlElement>()), out _)));

        // Implicit closure adds no synthetic source markup; explicit opening
        // spans remain anchored to their original byte-decoded character offsets.
        Assert.Equal(FindOpeningTag(html, "<p align=\"center\">", 0), logoParagraph.SourceStart);
        Assert.Equal("<p align=\"center\">".Length, logoParagraph.SourceLength);
        Assert.Equal(FindOpeningTag(html, "<h2 align=\"center\">", 0), heading.SourceStart);
        Assert.Equal(FindOpeningTag(html, "<p align=\"center\">", logoParagraph.SourceStart + 1), subtitle.SourceStart);
        Assert.Equal(
            FindOpeningTag(html, "<p align=\"center\">", subtitle.SourceStart + 1),
            badges.SourceStart);
        Assert.Equal("", logoParagraph.RawClosingTag);
        Assert.Equal("</p>", badges.RawClosingTag);
    }

    [Fact]
    public void Parse_ImplicitParagraphClosureHandlesMalformedNestingWithoutRelaxingSafety()
    {
        const string html = "<p><em>one<p><a href=\"javascript:alert(1)\">two</a><div><script><img src='https://example.com/tracker.png'>alert(1)</script>three</div>tail";

        SafeHtmlDocument document = SafeHtmlParser.Parse(html);
        SafeHtmlElement[] blocks = document.Root.Children.OfType<SafeHtmlElement>().ToArray();
        Assert.False(document.IsTruncated);
        Assert.Equal(["p", "p", "div"], blocks.Select(element => element.Name));

        SafeHtmlElement firstParagraph = blocks[0];
        SafeHtmlElement emphasis = Assert.IsType<SafeHtmlElement>(Assert.Single(firstParagraph.Children));
        Assert.Equal("em", emphasis.Name);
        Assert.Equal(html.IndexOf("<em>", StringComparison.Ordinal), emphasis.SourceStart);

        SafeHtmlElement secondParagraph = blocks[1];
        SafeHtmlElement unsafeLink = Assert.IsType<SafeHtmlElement>(Assert.Single(secondParagraph.Children));
        Assert.False(SafeHtmlParser.TryGetSafeLink(unsafeLink, out _));
        Assert.Equal(html.IndexOf("<p>", html.IndexOf("<p>", StringComparison.Ordinal) + 1, StringComparison.Ordinal), secondParagraph.SourceStart);

        SafeHtmlElement div = blocks[2];
        SafeHtmlElement script = Assert.IsType<SafeHtmlElement>(div.Children[0]);
        Assert.True(SafeHtmlParser.IsSuppressedElement(script.Name));
        Assert.Empty(script.Children);
        Assert.IsType<SafeHtmlText>(div.Children[1]);
        Assert.Equal("tail", Assert.IsType<SafeHtmlText>(document.Root.Children[^1]).DecodedText);
    }

    [Fact]
    public void Parse_ExplicitParagraphClosuresAndInlineContentKeepTheirOriginalTree()
    {
        const string html = "<p>before <span>inline</span> after</p><div><p>nested</p></div>";

        SafeHtmlDocument document = SafeHtmlParser.Parse(html);
        SafeHtmlElement[] blocks = document.Root.Children.OfType<SafeHtmlElement>().ToArray();
        Assert.False(document.IsTruncated);
        Assert.Equal(["p", "div"], blocks.Select(element => element.Name));
        Assert.Equal("</p>", blocks[0].RawClosingTag);
        Assert.Equal(["span"], blocks[0].Children.OfType<SafeHtmlElement>().Select(element => element.Name));
        Assert.Equal("</p>", Assert.IsType<SafeHtmlElement>(Assert.Single(blocks[1].Children)).RawClosingTag);
        Assert.Equal("</div>", blocks[1].RawClosingTag);
    }

    [Fact]
    public void Parse_ImplicitParagraphClosureStopsAtButtonScopeBoundary()
    {
        const string html = "<p><button><div>inside</div></button>after</p>";

        SafeHtmlDocument document = SafeHtmlParser.Parse(html);
        SafeHtmlElement paragraph = Assert.IsType<SafeHtmlElement>(Assert.Single(document.Root.Children));
        SafeHtmlElement button = Assert.IsType<SafeHtmlElement>(paragraph.Children[0]);

        Assert.Equal("p", paragraph.Name);
        Assert.Equal("button", button.Name);
        Assert.Equal("div", Assert.IsType<SafeHtmlElement>(Assert.Single(button.Children)).Name);
        Assert.Equal("</p>", paragraph.RawClosingTag);
        Assert.Equal("after", Assert.IsType<SafeHtmlText>(paragraph.Children[1]).DecodedText);
    }

    [Fact]
    public void Parse_SupportsUnquotedAttributesEntitiesAndSafeInlineFormatting()
    {
        const string html = "<sup><a href=docs/guide.md>Guide&nbsp;&amp;&nbsp;setup</a></sup><br>";

        SafeHtmlDocument document = SafeHtmlParser.Parse(html);
        SafeHtmlElement sup = Assert.IsType<SafeHtmlElement>(document.Root.Children[0]);
        SafeHtmlElement anchor = Assert.IsType<SafeHtmlElement>(Assert.Single(sup.Children));
        SafeHtmlText text = Assert.IsType<SafeHtmlText>(Assert.Single(anchor.Children));

        Assert.True(SafeHtmlParser.TryGetSafeLink(anchor, out string link));
        Assert.Equal("docs/guide.md", link);
        Assert.Equal("Guide\u00A0&\u00A0setup", text.DecodedText);
        Assert.Equal("Guide\u00A0&\u00A0setup", SafeHtmlParser.CollapseWhitespace(text.RawText));
        Assert.Equal("br", Assert.IsType<SafeHtmlElement>(document.Root.Children[1]).Name);
    }

    [Fact]
    public void Parse_DropsRawContentPayloadFromTheTree()
    {
        const string html = "<script><img src='https://example.com/tracker.png'>alert(1)</script><p>Visible</p>";

        SafeHtmlDocument document = SafeHtmlParser.Parse(html);
        SafeHtmlElement script = Assert.IsType<SafeHtmlElement>(document.Root.Children[0]);
        SafeHtmlElement paragraph = Assert.IsType<SafeHtmlElement>(document.Root.Children[1]);

        Assert.True(SafeHtmlParser.IsSuppressedElement(script.Name));
        Assert.Empty(script.Children);
        Assert.Equal("Visible", Assert.IsType<SafeHtmlText>(Assert.Single(paragraph.Children)).DecodedText);
    }

    [Theory]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("file:///C:/secret.txt", false)]
    [InlineData("data:text/html,hello", false)]
    [InlineData("data:image/svg+xml,%3Csvg%3E%3C/svg%3E", true)]
    [InlineData("https://example.com/image.png", true)]
    [InlineData("../images/logo.svg", true)]
    public void ImagePolicy_AllowsOnlyExpectedSchemes(string source, bool expected)
    {
        SafeHtmlElement image = ElementWithAttribute("img", "src", source);
        Assert.Equal(expected, SafeHtmlParser.TryGetSafeImageSource(image, out _));
    }

    [Theory]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("data:text/html,hello", false)]
    [InlineData("mailto:hello@example.com", true)]
    [InlineData("#contents", true)]
    [InlineData("docs/guide.md", true)]
    public void LinkPolicy_AllowsOnlyExpectedSchemes(string href, bool expected)
    {
        SafeHtmlElement anchor = ElementWithAttribute("a", "href", href);
        Assert.Equal(expected, SafeHtmlParser.TryGetSafeLink(anchor, out _));
    }

    [Fact]
    public void Dimensions_ReadSafeStyleSubsetAndClampExtremes()
    {
        SafeHtmlElement image = new(
            "img",
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["style"] = "color: red; width: 128px; height: 50%; position: fixed",
            },
            0,
            0);

        Assert.Equal(new SafeHtmlLength(128, false), GetLength(image, "width"));
        Assert.Equal(new SafeHtmlLength(50, true), GetLength(image, "height"));

        SafeHtmlElement huge = ElementWithAttribute("img", "width", "999999999");
        Assert.Equal(new SafeHtmlLength(8192, false), GetLength(huge, "width"));
    }

    [Fact]
    public void TagSequence_RecognizesSplitMarkdownAlignmentScopes()
    {
        Assert.True(SafeHtmlParser.TryParseTagSequence(
            "<div align=\"center\" markdown=\"1\">\n<!-- note -->",
            out IReadOnlyList<SafeHtmlTag> opening));
        Assert.Single(opening);
        Assert.Equal(SafeHtmlTagKind.Opening, opening[0].Kind);
        Assert.Equal(SafeHtmlAlignment.Center, SafeHtmlParser.GetAlignment(opening[0]));

        Assert.True(SafeHtmlParser.TryParseTagSequence("\n</div>\n", out IReadOnlyList<SafeHtmlTag> closing));
        Assert.Equal(SafeHtmlTagKind.Closing, Assert.Single(closing).Kind);
    }

    [Fact]
    public void AlignmentScopesTrackSplitNestedOpeningsAndClosings()
    {
        SafeHtmlBlockScopeTracker tracker = new();
        IReadOnlyDictionary<string, bool> disclosureStates = new Dictionary<string, bool>();

        Assert.True(tracker.Process("<div align=\"center\">", 0, disclosureStates));
        Assert.Equal(SafeHtmlAlignment.Center, tracker.CurrentAlignment);
        Assert.True(tracker.Process("<div align=\"right\">", 20, disclosureStates));
        Assert.Equal(SafeHtmlAlignment.Right, tracker.CurrentAlignment);
        Assert.True(tracker.Process("</div>", 41, disclosureStates));
        Assert.Equal(SafeHtmlAlignment.Center, tracker.CurrentAlignment);
        Assert.True(tracker.Process("</div>", 48, disclosureStates));
        Assert.Equal(SafeHtmlAlignment.Inherit, tracker.CurrentAlignment);

        SafeHtmlBlockScopeTracker sameBlockTracker = new();
        Assert.False(sameBlockTracker.Process(
            "<div align=\"center\"><h3>Same block</h3></div>",
            0,
            disclosureStates));
        Assert.Equal(SafeHtmlAlignment.Inherit, sameBlockTracker.CurrentAlignment);
    }

    [Fact]
    public void NestedScopeCopyKeepsBudgetDenialAndClosedDetailsSuppression()
    {
        SafeHtmlParseLimits limits = SafeHtmlParseLimits.Default with { MaxInputLength = 40 };
        SafeHtmlBlockScopeTracker parent = new(limits);
        IReadOnlyDictionary<string, bool> disclosureStates = new Dictionary<string, bool>();

        Assert.False(parent.Process(
            "<details><summary>title</summary>",
            sourceOffset: 0,
            disclosureStates));
        Assert.True(parent.IsContentSuppressed);

        Assert.False(parent.Process(
            "<p>" + new string('x', 20) + "</p>",
            sourceOffset: 33,
            disclosureStates));
        Assert.True(parent.BudgetExceeded);
        Assert.True(parent.IsContentSuppressed);

        // A nested scope copy cannot turn a parent denial into a fresh active
        // HTML tracker or discard a closed disclosure's suppression state.
        SafeHtmlBlockScopeTracker nested = parent.CloneForNestedContent();
        Assert.True(nested.BudgetExceeded);
        Assert.True(nested.IsContentSuppressed);
        Assert.Equal(SafeHtmlAlignment.Inherit, nested.CurrentAlignment);
        Assert.False(nested.Process("<div align=\"center\">", 60, disclosureStates));
        Assert.True(nested.BudgetExceeded);
        Assert.True(nested.IsContentSuppressed);
        Assert.Equal(SafeHtmlAlignment.Inherit, nested.CurrentAlignment);
    }

    [Fact]
    public void Parse_BoundsDeepAndOversizedInputWithoutThrowing()
    {
        string deep = string.Concat(Enumerable.Repeat("<div>", SafeHtmlParser.MaxNestingDepth + 20)) +
            "visible" +
            string.Concat(Enumerable.Repeat("</div>", SafeHtmlParser.MaxNestingDepth + 20));
        SafeHtmlDocument deepDocument = SafeHtmlParser.Parse(deep);
        Assert.True(deepDocument.IsTruncated);

        string oversized = "<p>" + new string('x', SafeHtmlParser.MaxInputLength + 1) + "</p>";
        SafeHtmlDocument oversizedDocument = SafeHtmlParser.Parse(oversized);
        Assert.True(oversizedDocument.IsTruncated);
    }

    [Fact]
    public async Task MalformedTagRetriesStopAtALinearScanWorkCeiling()
    {
        string source = string.Concat(Enumerable.Repeat("<a'", 50_000));
        var limits = new SafeHtmlParseLimits(
            MaxInputLength: source.Length,
            MaxNodeCount: 1_000_000,
            MaxNestingDepth: SafeHtmlParser.MaxNestingDepth,
            MaxAttributeCount: SafeHtmlParser.MaxAttributeCount,
            MaxAttributeValueLength: SafeHtmlParser.MaxAttributeValueLength,
            MaxTagLength: SafeHtmlParser.MaxTagLength);

        SafeHtmlDocument document = await Task.Run(() => SafeHtmlParser.Parse(source, limits))
            .WaitAsync(TimeSpan.FromSeconds(5));
        (IReadOnlyList<SafeHtmlTag> Tags, bool ScanBudgetExceeded) tagResult = await Task.Run(() =>
            {
                IReadOnlyList<SafeHtmlTag> parsed = SafeHtmlParser.ParseTags(
                    source,
                    limits,
                    CancellationToken.None,
                    out bool scanBudgetExceeded);
                return (parsed, scanBudgetExceeded);
            })
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(document.IsTruncated);
        Assert.InRange(document.Root.Children.Count, 1, 32);
        Assert.True(tagResult.ScanBudgetExceeded);
        Assert.Empty(tagResult.Tags);
    }

    [Fact]
    public void ParserEntryPointsObserveCancellationBeforeScanning()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        SafeHtmlParseLimits limits = SafeHtmlParseLimits.Default;

        Assert.ThrowsAny<OperationCanceledException>(() =>
            SafeHtmlParser.Parse("<p>ignored</p>", limits, cancellation.Token));
        Assert.ThrowsAny<OperationCanceledException>(() =>
            SafeHtmlParser.TryParseSingleTag(
                "<p>",
                limits,
                cancellation.Token,
                out _,
                out _));
        Assert.ThrowsAny<OperationCanceledException>(() =>
            SafeHtmlParser.TryParseTagSequence(
                "<p></p>",
                limits,
                cancellation.Token,
                out _,
                out _));
        Assert.ThrowsAny<OperationCanceledException>(() =>
            SafeHtmlParser.ParseTags(
                "<p></p>",
                limits,
                cancellation.Token,
                out _));
    }

    private static SafeHtmlLength GetLength(SafeHtmlElement element, string name)
    {
        Assert.True(SafeHtmlParser.TryGetLength(element, name, out SafeHtmlLength length));
        return length;
    }

    private static int FindOpeningTag(string html, string tag, int startIndex)
    {
        int index = html.IndexOf(tag, startIndex, StringComparison.Ordinal);
        Assert.True(index >= 0, $"Expected opening tag {tag} after source offset {startIndex}.");
        return index;
    }

    private static SafeHtmlElement ElementWithAttribute(string name, string attribute, string value) => new(
        name,
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [attribute] = value },
        0,
        0);

    private static IEnumerable<SafeHtmlElement> Descendants(SafeHtmlElement element)
    {
        foreach (SafeHtmlElement child in element.Children.OfType<SafeHtmlElement>())
        {
            yield return child;
            foreach (SafeHtmlElement descendant in Descendants(child))
            {
                yield return descendant;
            }
        }
    }
}
