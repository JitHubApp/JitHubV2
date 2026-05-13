using Markdig;
using Markdig.Extensions.Footnotes;
using Markdig.Extensions.Tables;
using Markdig.Syntax;
using MarkdownRenderer.Gfm.Renderers;
using MarkdownRenderer.Parsing;

namespace MarkdownRenderer.Gfm;

/// <summary>
/// Convenience entry point that registers GitHub-flavored markdown features on
/// a <see cref="MarkdownExtensionRegistry"/>: pipe tables, task lists, autolinks,
/// strikethrough, footnotes, emoji shortcodes, and GitHub-style alerts.
/// </summary>
public static class GfmExtensions
{
    public static MarkdownExtensionRegistry UseGitHubFlavoredMarkdown(this MarkdownExtensionRegistry registry)
    {
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
}
