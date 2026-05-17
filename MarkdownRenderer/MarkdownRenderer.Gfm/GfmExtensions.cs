using Markdig;
using Markdig.Extensions.DefinitionLists;
using Markdig.Extensions.Figures;
using Markdig.Extensions.Footnotes;
using Markdig.Extensions.Tables;
using Markdig.Syntax;
using MarkdownRenderer.Controls;
using MarkdownRenderer.Hosting;
using MarkdownRenderer.Gfm.Renderers;
using MarkdownRenderer.Parsing;
using MarkdownRenderer.Theming;

namespace MarkdownRenderer.Gfm;

/// <summary>
/// Convenience entry point that registers GitHub-flavored markdown features on
/// a <see cref="MarkdownExtensionRegistry"/>: pipe tables, task lists, autolinks,
/// strikethrough, footnotes, emoji shortcodes, and GitHub-style alerts.
/// </summary>
public static class GfmExtensions
{
    /// <summary>
    /// Adds GitHub-flavored markdown parsing and rendering support to a registry.
    /// </summary>
    /// <param name="registry">Registry to configure.</param>
    /// <returns>The same registry for fluent chaining.</returns>
    public static MarkdownExtensionRegistry UseGitHubFlavoredMarkdown(this MarkdownExtensionRegistry registry)
    {
        if (registry is null) throw new System.ArgumentNullException(nameof(registry));

        registry.ConfigurePipeline(p =>
        {
            p.UsePipeTables();
            p.UseTaskLists();
            p.UseAutoLinks();
            p.UseEmphasisExtras();
            p.UseFootnotes();
            p.UseEmojiAndSmiley();
            p.UseGenericAttributes();
        });

        registry.RegisterRenderer<Table>(new TableRenderer());
        registry.RegisterRenderer<ListItemBlock>(new TaskListItemRenderer());
        registry.RegisterRenderer<QuoteBlock>(new AlertRenderer());
        registry.RegisterRenderer<FootnoteGroup>(new FootnoteRenderer());

        return registry;
    }

    /// <summary>
    /// Configures a control builder to use GitHub-flavored markdown.
    /// </summary>
    /// <param name="builder">Builder to configure.</param>
    /// <returns>The same builder for fluent chaining.</returns>
    public static MarkdownRendererControlBuilder UseGitHubFlavoredMarkdown(this MarkdownRendererControlBuilder builder)
    {
        if (builder is null) throw new System.ArgumentNullException(nameof(builder));
        return builder.ConfigureExtensions(registry => registry.UseGitHubFlavoredMarkdown());
    }

    /// <summary>
    /// Adds Markdown Extra style features that are not part of GitHub-flavored markdown:
    /// definition lists, abbreviations, and figure/caption blocks.
    /// </summary>
    /// <param name="registry">Registry to configure.</param>
    /// <returns>The same registry for fluent chaining.</returns>
    public static MarkdownExtensionRegistry UseMarkdownExtra(this MarkdownExtensionRegistry registry)
    {
        if (registry is null) throw new System.ArgumentNullException(nameof(registry));

        registry.ConfigurePipeline(p =>
        {
            p.UseDefinitionLists();
            p.UseAbbreviations();
            p.UseFigures();
        });

        registry.RegisterRenderer<DefinitionList>(new DefinitionListRenderer());
        registry.RegisterRenderer<Figure>(new FigureRenderer());

        return registry;
    }

    /// <summary>
    /// Configures a control builder to use Markdown Extra style features that are
    /// intentionally kept separate from the strict GFM helper.
    /// </summary>
    /// <param name="builder">Builder to configure.</param>
    /// <returns>The same builder for fluent chaining.</returns>
    public static MarkdownRendererControlBuilder UseMarkdownExtra(this MarkdownRendererControlBuilder builder)
    {
        if (builder is null) throw new System.ArgumentNullException(nameof(builder));
        return builder.ConfigureExtensions(registry => registry.UseMarkdownExtra());
    }
}
