using MarkdownRenderer.Html;
using MarkdownRenderer.Layout.Internal.Inlines.Runs;
using MarkdownRenderer.Layout.Internal.Snapshots;
using MarkdownRenderer.Parsing.Internal;
using MarkdownRenderer.Parsing.Internal.SafeHtml;
using MarkdownRenderer.Theming;
using Xunit;
using static MarkdownRenderer.GitHub.Tests.SafeHtmlRenderingFixture;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>
/// Checks the safe HTML option and policy surface: option replacement,
/// disabled links and images, class allowlists, and exact literal
/// fallback for unknown markup and scripts.
/// </summary>
public sealed class SafeHtmlPolicyTests
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
    public void StandaloneScriptIsVisibleAsExactSelectableInertSource()
    {
        const string source = "<script data-note='literal'>unsafe-script-sentinel()</script>";

        using LayoutSnapshot snapshot = Build(source, SafeHtmlOptions.Default);
        InlineRun[] runs = FlattenRuns(snapshot).ToArray();

        Assert.Equal(source, string.Concat(runs.Select(static run => run.Text)));
        Assert.All(runs, static run => Assert.IsType<TextRun>(run));
        Assert.Equal(source, string.Concat(runs.Select(run =>
            source.Substring(run.SourceSpan.Start, run.SourceSpan.Length))));
    }

    [Fact]
    public void ScriptLiteralStaysInOrderWithSurroundingLinkedHtmlWithoutBecomingALink()
    {
        const string rawScript = "<script>unsafe-script-sentinel()</script>";
        const string source = "<p><a href='https://example.test'>before" + rawScript + "after</a></p>";

        using LayoutSnapshot snapshot = Build(source, SafeHtmlOptions.Default);
        InlineRun[] runs = FlattenRuns(snapshot).ToArray();
        string rendered = string.Concat(runs.Select(static run => run.Text));

        Assert.Equal("before" + rawScript + "after", rendered);
        Assert.Contains(runs, static run => run is LinkRun { Text: "before" });
        Assert.Contains(runs, static run => run is LinkRun { Text: "after" });

        InlineRun[] scriptRuns = runs.Where(run =>
                run.SourceSpan.Start >= source.IndexOf(rawScript, StringComparison.Ordinal) &&
                run.SourceSpan.Start < source.IndexOf(rawScript, StringComparison.Ordinal) + rawScript.Length)
            .ToArray();
        Assert.NotEmpty(scriptRuns);
        Assert.All(scriptRuns, static run => Assert.IsType<TextRun>(run));
        Assert.Equal(rawScript, string.Concat(scriptRuns.Select(static run => run.Text)));
    }

    [Fact]
    public void ScriptLiteralBetweenHtmlBlocksDoesNotDropOrReorderSiblingText()
    {
        const string rawScript = "<script>unsafe-script-sentinel()</script>";
        const string source = "<p>before block</p>" + rawScript + "<p>after block</p>";

        using LayoutSnapshot snapshot = Build(source, SafeHtmlOptions.Default);
        string rendered = string.Concat(FlattenRuns(snapshot).Select(static run => run.Text));

        Assert.Equal("before block" + rawScript + "after block", rendered);
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
}
