using Markdig;
using MarkdownRenderer.Document;
using MarkdownRenderer.GitHub;
using MarkdownRenderer.Html;
using MarkdownRenderer.Hosting;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Parsing;
using MarkdownRenderer.Theming;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml;
using Windows.UI;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

public sealed class SafeHtmlRenderingTests
{
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
    public void GitHubDetailsAndSummaryKeepNestedMarkdownReadable()
    {
        const string source = """
            <details open>
            <summary>Installation notes</summary>

            Use **stable** packages and review the table below.

            | Channel | Status |
            | --- | --- |
            | Stable | Ready |

            </details>
            """;

        using LayoutSnapshot snapshot = Build(source, SafeHtmlOptions.Default);
        string rendered = string.Concat(FlattenRuns(snapshot).Select(static run => run.Text));

        Assert.Contains("Installation notes", rendered, StringComparison.Ordinal);
        Assert.Contains("stable", rendered, StringComparison.Ordinal);
        Assert.Contains("Stable", rendered, StringComparison.Ordinal);
        Assert.Contains("Ready", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("<details", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("<summary", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void DerivedEngineSafeHtmlOptionsReplaceTheExistingFeatureRenderer()
    {
        const string source =
            "<p><a href='https://example.com'>linked</a> " +
            "<img src='https://example.com/image.png' alt='diagram'></p>";
        using MarkdownEngine original = new MarkdownEngineBuilder()
            .UseGitHubReadme()
            .Build();
        using MarkdownEngine derived = original.ToBuilder()
            .UseSafeHtml(new SafeHtmlOptions(enableLinks: false, enableImages: false))
            .Build();
        var registry = Assert.IsType<MarkdownExtensionRegistry>(derived.PresentationConfiguration);

        using LayoutSnapshot snapshot = Build(source, registry);

        AssertDisabledLinkAndImageFallback(snapshot);
    }

    [Fact]
    public void PerViewSafeHtmlOptionsReplaceTheExistingGitHubFeatureRenderer()
    {
        const string source =
            "<p><a href='https://example.com'>linked</a> " +
            "<img src='https://example.com/image.png' alt='diagram'></p>";
        using MarkdownEngine engine = new MarkdownEngineBuilder()
            .UseGitHubReadme()
            .Build();
        MarkdownExtensionRegistry registry = GitHubReadmeExtensions.GetGitHubReadmeRegistry(
            engine,
            viewRegistry: null,
            new SafeHtmlOptions(enableLinks: false, enableImages: false));

        using LayoutSnapshot snapshot = Build(source, registry);

        AssertDisabledLinkAndImageFallback(snapshot);
    }

    [Fact]
    public void ExistingStrictSafeHtmlOptionsSurviveFollowingGitHubDefaults()
    {
        const string source =
            "<p><a href='https://example.com'>linked</a> " +
            "<img src='https://example.com/image.png' alt='diagram'></p>";
        using MarkdownEngine engine = new MarkdownEngineBuilder()
            .UseSafeHtml(new SafeHtmlOptions(enableLinks: false, enableImages: false))
            .UseGitHubReadme()
            .Build();
        var registry = Assert.IsType<MarkdownExtensionRegistry>(engine.PresentationConfiguration);

        using LayoutSnapshot snapshot = Build(source, registry);

        AssertDisabledLinkAndImageFallback(snapshot);
        SafeHtmlRenderPolicy policy = Assert.IsType<SafeHtmlRenderPolicy>(registry.SafeHtmlPolicy);
        Assert.False(policy.EnableLinks);
        Assert.False(policy.EnableImages);
    }

    [Fact]
    public void DisabledLinksAndImagesRemainReadableButNonInteractive()
    {
        const string source =
            "<p><a href='https://example.com'>linked</a> " +
            "<img src='https://example.com/image.png' alt='diagram'></p>";
        var options = new SafeHtmlOptions(enableLinks: false, enableImages: false);

        using LayoutSnapshot snapshot = Build(source, options);
        InlineRun[] runs = FlattenRuns(snapshot).ToArray();

        Assert.DoesNotContain(runs, static run => run is LinkRun);
        Assert.DoesNotContain(runs, static run => run is InlineImageRun);
        Assert.Contains("linked", string.Concat(runs.Select(static run => run.Text)), StringComparison.Ordinal);
        Assert.Contains("diagram", string.Concat(runs.Select(static run => run.Text)), StringComparison.Ordinal);
    }

    [Fact]
    public void InlineHtmlUsesTheSameLinkImageAndClassPolicyAsHtmlBlocks()
    {
        const string source =
            "before <a class='approved rejected' href='https://example.com'>linked</a> " +
            "<img src='https://example.com/image.png' alt='diagram'> after";
        var options = new SafeHtmlOptions(
            enableLinks: false,
            enableImages: false,
            allowedStyleClasses: ["approved"]);

        using LayoutSnapshot snapshot = Build(source, options);
        InlineRun[] runs = FlattenRuns(snapshot).ToArray();
        InlineRun linked = Assert.Single(runs, static run => run.Text == "linked");

        Assert.DoesNotContain(runs, static run => run is LinkRun or InlineImageRun);
        Assert.Contains(MarkdownElementKeys.Class("approved"), linked.StyleAliases);
        Assert.DoesNotContain(MarkdownElementKeys.Class("rejected"), linked.StyleAliases);
        Assert.Contains("diagram", string.Concat(runs.Select(static run => run.Text)), StringComparison.Ordinal);
    }

    [Fact]
    public void HtmlBlockCollapsesWhitespaceAcrossInlineFormattingBoundaries()
    {
        const string source =
            "<p align='center'><strong>Codex CLI</strong> is a coding agent. " +
            "For an IDE, <a href='https://example.test/install'>install the extension</a>." +
            "<br>If you prefer, run <code>codex app</code> or visit " +
            "<a href='https://example.test/app'>the Codex App page</a>.</p>";

        using LayoutSnapshot snapshot = Build(source, SafeHtmlOptions.Default);
        string rendered = string.Concat(FlattenRuns(snapshot).Select(static run => run.Text));

        Assert.Equal(
            "Codex CLI is a coding agent. For an IDE, install the extension.\n" +
            "If you prefer, run codex app or visit the Codex App page.",
            rendered);
        Assert.Equal(2, FlattenRuns(snapshot).OfType<LinkRun>().Count());
    }

    [Fact]
    public void MarkdownLinkContainingInlineHtmlImageRemainsAVisibleLinkedImage()
    {
        const string source =
            "[<img src=\"https://run.pstmn.io/button.svg\" alt=\"Run In Postman\" " +
            "style=\"width: 128px; height: 32px;\">](https://example.test/collection)";

        using LayoutSnapshot snapshot = Build(source, SafeHtmlOptions.Default);
        InlineImageRun image = Assert.Single(FlattenRuns(snapshot).OfType<InlineImageRun>());

        Assert.Equal("https://run.pstmn.io/button.svg", image.Url);
        Assert.Equal("Run In Postman", image.AltText);
        Assert.Equal("https://example.test/collection", image.LinkUrl);
        Assert.True(image.IsLinked);
        Assert.Equal(new SourceSpan(0, source.Length), image.SourceSpan);
    }

    [Fact]
    public void MarkdownLinkContainingHtmlImageAndTextPreservesBothLinkedContents()
    {
        const string source =
            "[<img src=\"doc/images/icons/AdvancedPaste.png\" alt=\"Advanced Paste icon\" width=\"32\"> " +
            "Advanced Paste](https://learn.microsoft.com/windows/powertoys/advanced-paste)";

        using LayoutSnapshot snapshot = Build(source, SafeHtmlOptions.Default);
        InlineRun[] runs = FlattenRuns(snapshot).ToArray();

        InlineImageRun image = Assert.Single(runs.OfType<InlineImageRun>());
        LinkRun label = Assert.Single(runs.OfType<LinkRun>());
        Assert.Equal("doc/images/icons/AdvancedPaste.png", image.Url);
        Assert.Equal("Advanced Paste icon", image.AltText);
        Assert.Equal("https://learn.microsoft.com/windows/powertoys/advanced-paste", image.LinkUrl);
        Assert.Equal(" Advanced Paste", label.Text);
        Assert.Equal(image.LinkUrl, label.Url);
    }

    [Fact]
    public void UnknownMarkupUsesExactLiteralFallbackIncludingSuppressedChildren()
    {
        const string source = "<custom DATA-x='v'>a<script>alert(1)</script></custom>";

        using LayoutSnapshot snapshot = Build(source, SafeHtmlOptions.Default);

        Assert.Equal(source, string.Concat(FlattenRuns(snapshot).Select(static run => run.Text)));
    }

    [Fact]
    public void UnknownMarkupCanKeepOnlySafeTextContent()
    {
        const string source = "<custom>visible<script>hidden()</script></custom>";
        var options = new SafeHtmlOptions(
            unknownElementBehavior: SafeHtmlUnknownElementBehavior.KeepTextContent);

        using LayoutSnapshot snapshot = Build(source, options);
        string rendered = string.Concat(FlattenRuns(snapshot).Select(static run => run.Text));

        Assert.Equal("visible", rendered);
    }

    [Fact]
    public void OnlyHostAllowlistedHtmlClassesReachStyleSelectors()
    {
        const string source = "<p><span class='approved rejected'>styled</span></p>";
        var options = new SafeHtmlOptions(allowedStyleClasses: ["approved"]);

        using LayoutSnapshot snapshot = Build(source, options);
        InlineRun run = Assert.Single(FlattenRuns(snapshot), static candidate => candidate.Text == "styled");

        Assert.Contains(MarkdownElementKeys.Class("approved"), run.StyleAliases);
        Assert.DoesNotContain(MarkdownElementKeys.Class("rejected"), run.StyleAliases);
    }

    [Fact]
    public void HostLoweredInputBudgetProducesLocalizedFallbackNoticeInsteadOfBlankOutput()
    {
        var options = new SafeHtmlOptions(
            budgets: new SafeHtmlBudgets(
                maxInputLength: 16,
                maxNodeCount: 20,
                maxNestingDepth: 8,
                maxAttributeCount: 4,
                maxAttributeValueLength: 16,
                maxTagLength: 16));

        using LayoutSnapshot snapshot = Build("<p>012345678901234567890123456789</p>", options);
        string rendered = string.Concat(FlattenRuns(snapshot).Select(static run => run.Text));

        Assert.Contains("omitted", rendered, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void HtmlVisibleFallbackTextUsesTheHostStringProvider()
    {
        var options = new SafeHtmlOptions(
            budgets: new SafeHtmlBudgets(
                maxInputLength: 16,
                maxNodeCount: 20,
                maxNestingDepth: 8,
                maxAttributeCount: 4,
                maxAttributeValueLength: 16,
                maxTagLength: 16));

        using LayoutSnapshot snapshot = Build(
            "<p>012345678901234567890123456789</p>",
            options,
            new TestStringProvider());

        Assert.Contains(
            "localized budget notice",
            string.Concat(FlattenRuns(snapshot).Select(static run => run.Text)),
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void InlineHtmlHonorsLoweredInputAndTagLimits(bool limitInput)
    {
        var options = new SafeHtmlOptions(budgets: new SafeHtmlBudgets(
            maxInputLength: limitInput ? 16 : 1024,
            maxTagLength: limitInput ? 1024 : 16));
        using LayoutSnapshot snapshot = Build(
            "prefix <a href='https://example.test/target'>link</a>", options,
            new TestStringProvider());

        InlineRun[] runs = FlattenRuns(snapshot).ToArray();
        Assert.DoesNotContain(runs, static run => run is LinkRun);
        Assert.Contains("localized budget notice", string.Concat(runs.Select(static run => run.Text)));
    }

    [Fact]
    public void InlineHtmlHonorsAttributeLimits()
    {
        var options = new SafeHtmlOptions(budgets: new SafeHtmlBudgets(
            maxAttributeCount: 1, maxAttributeValueLength: 4));
        using LayoutSnapshot snapshot = Build(
            "prefix <a title='long title' href='https://example.test'>link</a>", options);

        Assert.DoesNotContain(FlattenRuns(snapshot), static run => run is LinkRun);
        Assert.Contains("link", string.Concat(FlattenRuns(snapshot).Select(static run => run.Text)));
    }

    [Fact]
    public void DeepInlineHtmlStopsAtTheDefaultDepthCeiling()
    {
        string source = "prefix " + string.Concat(Enumerable.Repeat("<span>", 2_000)) + "unreachable";
        using LayoutSnapshot snapshot = Build(source, SafeHtmlOptions.Default, new TestStringProvider());

        string rendered = string.Concat(FlattenRuns(snapshot).Select(static run => run.Text));
        Assert.Contains("localized budget notice", rendered);
        Assert.DoesNotContain("unreachable", rendered);
    }

    [Fact]
    public void InlineDepthLimitAllowsTheExactBoundaryButRejectsAnotherScope()
    {
        var options = new SafeHtmlOptions(budgets: new SafeHtmlBudgets(maxNestingDepth: 2));
        using LayoutSnapshot allowed = Build("prefix <span><b>visible</b></span>", options);
        Assert.Contains("visible", string.Concat(FlattenRuns(allowed).Select(static run => run.Text)));

        using LayoutSnapshot rejected = Build("prefix <span><b><i>unreachable</i></b></span>", options);
        string rendered = string.Concat(FlattenRuns(rejected).Select(static run => run.Text));
        Assert.Contains("omitted", rendered);
        Assert.DoesNotContain("unreachable", rendered);
    }

    [Fact]
    public void InlineNodeBudgetCannotBeResetWithSiblingTags()
    {
        var options = new SafeHtmlOptions(budgets: new SafeHtmlBudgets(maxNodeCount: 3));
        using LayoutSnapshot snapshot = Build(
            "prefix <span>one</span><a href='https://example.test'>two</a>", options);

        InlineRun[] runs = FlattenRuns(snapshot).ToArray();
        Assert.DoesNotContain(runs, static run => run is LinkRun);
        Assert.Contains("omitted", string.Concat(runs.Select(static run => run.Text)));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CrossBlockHtmlScopesShareTheConfiguredBudgets(bool limitDepth)
    {
        var options = new SafeHtmlOptions(budgets: new SafeHtmlBudgets(
            maxNestingDepth: limitDepth ? 2 : 64,
            maxInputLength: limitDepth ? 4096 : 32));
        const string source = "<div align='right'>\n\n<div>\n\n<div>\n\n<div>\n\nvisible markdown";
        using LayoutSnapshot snapshot = Build(source, options, new TestStringProvider());

        string rendered = string.Concat(FlattenRuns(snapshot).Select(static run => run.Text));
        Assert.Contains("localized budget notice", rendered);
        Assert.Contains("visible markdown", rendered);
        Assert.Equal(1, rendered.Split("localized budget notice").Length - 1);
    }

    private static LayoutSnapshot Build(
        string source,
        SafeHtmlOptions options,
        IMarkdownStringProvider? stringProvider = null)
    {
        var registry = new MarkdownExtensionRegistry().ConfigureSafeHtmlRegistry(options);
        return Build(source, registry, stringProvider);
    }

    private static LayoutSnapshot Build(
        string source,
        MarkdownExtensionRegistry registry,
        IMarkdownStringProvider? stringProvider = null)
    {
        Markdig.Syntax.MarkdownDocument document = Markdown.Parse(source, registry.BuildPipeline());
        var style = new ElementStyle();
        string[] keys =
        [
            MarkdownElementKeys.Body,
            MarkdownElementKeys.Strong,
            MarkdownElementKeys.Emphasis,
            MarkdownElementKeys.Link,
            MarkdownElementKeys.ImageCaption,
            MarkdownElementKeys.CodeInline,
            MarkdownElementKeys.CodeBlock,
            MarkdownElementKeys.CodeBlockHeader,
            MarkdownElementKeys.CodeBlockLanguage,
            MarkdownElementKeys.CodeBlockLanguage,
            MarkdownElementKeys.CodeBlockGutter,
            MarkdownElementKeys.CodeBlockLineNumber,
            MarkdownElementKeys.Strikethrough,
            MarkdownElementKeys.Subscript,
            MarkdownElementKeys.Superscript,
            MarkdownElementKeys.Inserted,
            MarkdownElementKeys.Marked,
            MarkdownElementKeys.ThematicBreak,
            MarkdownElementKeys.Quote,
            MarkdownElementKeys.ListMarker,
            MarkdownElementKeys.Table,
            MarkdownElementKeys.TableHeader,
            MarkdownElementKeys.TableCell,
        ];
        var styles = keys.Distinct(StringComparer.Ordinal)
            .ToDictionary(static key => key, _ => style, StringComparer.Ordinal);
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
        var context = new MarkdownLayoutContext(
            CanvasDevice.GetSharedDevice(),
            theme,
            new MarkdownSourceMap(source),
            registry,
            FlowDirection.LeftToRight)
        {
            StringProvider = stringProvider,
        };
        return new LayoutBuilder(context).Build(document, 800);
    }

    private static void AssertDisabledLinkAndImageFallback(LayoutSnapshot snapshot)
    {
        InlineRun[] runs = FlattenRuns(snapshot).ToArray();
        Assert.DoesNotContain(runs, static run => run is LinkRun);
        Assert.DoesNotContain(runs, static run => run is InlineImageRun);
        string rendered = string.Concat(runs.Select(static run => run.Text));
        Assert.Contains("linked", rendered, StringComparison.Ordinal);
        Assert.Contains("diagram", rendered, StringComparison.Ordinal);
    }

    private static IEnumerable<InlineRun> FlattenRuns(LayoutSnapshot snapshot)
    {
        foreach (BlockBox block in snapshot.Blocks)
        {
            foreach (InlineRun run in FlattenRuns(block))
                yield return run;
        }
    }

    private static IEnumerable<InlineRun> FlattenRuns(BlockBox block)
    {
        if (block is InlineContainerBox inline)
        {
            foreach (InlineRun run in inline.Runs)
                yield return run;
        }
        else if (block is StackBox stack)
        {
            foreach (BlockBox child in stack.Children)
            {
                foreach (InlineRun run in FlattenRuns(child))
                    yield return run;
            }
        }
    }

    private sealed class TestStringProvider : IMarkdownStringProvider
    {
        public string? GetString(string key, System.Globalization.CultureInfo culture) =>
            key == MarkdownStringKeys.HtmlBudgetExceeded ? "localized budget notice" : null;
    }
}
