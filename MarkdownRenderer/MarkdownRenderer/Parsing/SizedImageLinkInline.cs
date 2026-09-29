using Markdig.Syntax.Inlines;

namespace MarkdownRenderer.Parsing;

/// <summary>
/// Parser-level image link with intrinsic presentation dimensions. Extension
/// packages use this node when their syntax defines a size independently of the
/// referenced image's pixel dimensions (for example, GitHub image emoji).
/// </summary>
internal sealed class SizedImageLinkInline : LinkInline
{
    internal SafeHtmlLength? RequestedWidth { get; init; }

    internal SafeHtmlLength? RequestedHeight { get; init; }
}
