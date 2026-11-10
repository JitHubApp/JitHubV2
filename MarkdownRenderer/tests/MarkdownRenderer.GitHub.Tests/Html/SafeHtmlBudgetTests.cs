using MarkdownRenderer.Html;
using MarkdownRenderer.Layout.Internal.Inlines.Runs;
using MarkdownRenderer.Layout.Internal.Snapshots;
using Xunit;
using static MarkdownRenderer.GitHub.Tests.TestSupport.Fixtures.SafeHtmlRenderingFixture;

namespace MarkdownRenderer.GitHub.Tests.Html;

/// <summary>
/// Checks that safe HTML input, tag, attribute, depth and node budgets are
/// enforced across inline and block HTML and that their fallback notice
/// is localized through the host string provider.
/// </summary>
public sealed class SafeHtmlBudgetTests
{
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
}
