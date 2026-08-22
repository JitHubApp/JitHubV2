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

    private static SafeHtmlLength GetLength(SafeHtmlElement element, string name)
    {
        Assert.True(SafeHtmlParser.TryGetLength(element, name, out SafeHtmlLength length));
        return length;
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
