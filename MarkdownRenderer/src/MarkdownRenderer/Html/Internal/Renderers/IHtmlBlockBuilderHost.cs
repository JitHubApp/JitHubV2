using System.Collections.Generic;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Parsing.Internal.SafeHtml;

namespace MarkdownRenderer.Html.Internal.Renderers;

/// <summary>
/// The block-building calls that table and details builders make back into
/// <see cref="HtmlBlockBuilder"/> for their nested block content.
/// </summary>
internal interface IHtmlBlockBuilderHost
{
    BlockBox? BuildElementBlock(
        SafeHtmlElement element,
        MarkdownLayoutContext context,
        int sourceOffset,
        SafeHtmlAlignment inheritedAlignment);

    void AppendBlocks(
        StackBox destination,
        IReadOnlyList<SafeHtmlNode> nodes,
        MarkdownLayoutContext context,
        int sourceOffset,
        SafeHtmlAlignment inheritedAlignment);
}
