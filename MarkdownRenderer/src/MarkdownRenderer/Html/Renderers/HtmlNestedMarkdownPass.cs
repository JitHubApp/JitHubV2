using Markdig.Syntax;

namespace MarkdownRenderer.Html.Renderers;

/// <summary>
/// Safe-HTML nesting state for one layout pass: how many nested Markdown
/// reparses enclose the current HTML block, and the root document of the HTML
/// block being built, whose link reference definitions nested fragments replay.
/// A layout context builds one pass at a time, so it is not synchronized.
/// </summary>
internal sealed class HtmlNestedMarkdownPass
{
    internal int Depth { get; set; }

    internal MarkdownDocument? RootDocument { get; set; }
}
