using System.Reflection;
using Markdig;
using Markdig.Extensions.Footnotes;
using Markdig.Extensions.Tables;
using Markdig.Syntax;
using MarkdownRenderer.Gfm;
using MarkdownRenderer.GitHub;
using MarkdownRenderer.Html;
using MarkdownRenderer.Parsing;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

public sealed class GitHubReadmeProfileTests
{
    [Fact]
    public void StrictGfm_RegistersOnlyGfmRenderers()
    {
        var registry = new MarkdownExtensionRegistry().ConfigureGfmRegistry();

        Assert.True(HasRenderer<Table>(registry));
        Assert.True(HasRenderer<ListItemBlock>(registry));
        Assert.False(HasRenderer<QuoteBlock>(registry));
        Assert.False(HasRenderer<FootnoteGroup>(registry));
        Assert.False(HasRenderer<HtmlBlock>(registry));
    }

    [Fact]
    public void StrictGfm_DoesNotEnableGitHubReadmeSyntaxAdditions()
    {
        MarkdownPipeline pipeline = new MarkdownExtensionRegistry().ConfigureGfmRegistry().BuildPipeline();
        MarkdownDocument document = Markdown.Parse("Hello :rocket: [note][^1]\n\n[^1]: Footnote", pipeline);

        Assert.DoesNotContain(document, block => block is FootnoteGroup);
        Assert.Contains(FlattenLiteralText(document), text => text.Contains(":rocket:", StringComparison.Ordinal));
    }

    [Fact]
    public void StrictGfm_EnablesOnlyStrikethroughFromEmphasisExtras()
    {
        MarkdownPipeline pipeline = new MarkdownExtensionRegistry().ConfigureGfmRegistry().BuildPipeline();
        string html = Markdown.ToHtml("~~strike~~ ~sub~ ^super^ ++inserted++ ==marked==", pipeline);

        Assert.Contains("<del>strike</del>", html, StringComparison.Ordinal);
        Assert.Contains("~sub~", html, StringComparison.Ordinal);
        Assert.Contains("^super^", html, StringComparison.Ordinal);
        Assert.Contains("++inserted++", html, StringComparison.Ordinal);
        Assert.Contains("==marked==", html, StringComparison.Ordinal);
    }

    [Fact]
    public void GitHubReadme_ComposesGfmAndGitHubSpecificRenderers()
    {
        var registry = new MarkdownExtensionRegistry().ConfigureGitHubReadmeRegistry();

        Assert.True(HasRenderer<Table>(registry));
        Assert.True(HasRenderer<ListItemBlock>(registry));
        Assert.True(HasRenderer<QuoteBlock>(registry));
        Assert.True(HasRenderer<FootnoteGroup>(registry));
        Assert.True(HasRenderer<HtmlBlock>(registry));
    }

    [Fact]
    public void GitHubReadme_EnablesFootnotesAndEmojiWithoutChangingSafeHtmlBoundary()
    {
        MarkdownPipeline pipeline = new MarkdownExtensionRegistry().ConfigureGitHubReadmeRegistry().BuildPipeline();
        MarkdownDocument document = Markdown.Parse("Hello :rocket: [note][^1]\n\n[^1]: Footnote", pipeline);

        Assert.Contains(document, block => block is FootnoteGroup);
        Assert.DoesNotContain(FlattenLiteralText(document), text => text.Contains(":rocket:", StringComparison.Ordinal));
        Assert.False(SafeHtmlFeature.Capabilities.ExecutesScript);
        Assert.False(SafeHtmlFeature.Capabilities.HasDirectNetworkAccess);
    }

    [Fact]
    public void GitHubOnlyRenderers_AreShippedByGitHubAssembly()
    {
        Assembly gfmAssembly = typeof(GfmExtensions).Assembly;
        Assembly gitHubAssembly = typeof(GitHubReadmeExtensions).Assembly;

        Assert.DoesNotContain(gfmAssembly.GetTypes(), type =>
            type.Name is "AlertRenderer" or "FootnoteRenderer" or "HtmlBlockRenderer");
        Assert.Contains(gitHubAssembly.GetTypes(), type => type.Name == "AlertRenderer");
        Assert.Contains(gitHubAssembly.GetTypes(), type => type.Name == "FootnoteRenderer");
        Assert.DoesNotContain(gitHubAssembly.GetTypes(), type => type.Name == "HtmlBlockRenderer");
        Assert.Contains(typeof(SafeHtmlFeature).Assembly.GetTypes(), type => type.Name == "HtmlBlockRenderer");
    }

    private static bool HasRenderer<TNode>(MarkdownExtensionRegistry registry) where TNode : class
    {
        MethodInfo method = typeof(MarkdownExtensionRegistry).GetMethod(
            "TryGetRenderer",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Renderer registry lookup contract was not found.");
        object?[] arguments = [typeof(TNode), null];
        return (bool)(method.Invoke(registry, arguments) ?? false);
    }

    private static IEnumerable<string> FlattenLiteralText(MarkdownObject markdownObject)
    {
        if (markdownObject is Markdig.Syntax.Inlines.LiteralInline literal)
        {
            yield return literal.Content.ToString();
        }

        if (markdownObject is ContainerBlock blocks)
        {
            foreach (Block child in blocks)
            {
                foreach (string value in FlattenLiteralText(child))
                {
                    yield return value;
                }
            }
        }

        if (markdownObject is LeafBlock { Inline: { } inline })
        {
            foreach (Markdig.Syntax.Inlines.Inline child in inline)
            {
                foreach (string value in FlattenLiteralText(child))
                {
                    yield return value;
                }
            }
        }

        if (markdownObject is Markdig.Syntax.Inlines.ContainerInline inlines)
        {
            foreach (Markdig.Syntax.Inlines.Inline child in inlines)
            {
                foreach (string value in FlattenLiteralText(child))
                {
                    yield return value;
                }
            }
        }
    }
}
