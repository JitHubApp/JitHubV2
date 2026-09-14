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
