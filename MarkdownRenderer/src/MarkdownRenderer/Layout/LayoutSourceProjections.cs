using System;
using Markdig.Syntax;

namespace MarkdownRenderer.Layout;

/// <summary>
/// Source projections that map the coordinates of transformed fragments back to
/// the original document while one layout pass consumes them: a Markdown
/// fragment projection and a composed raw-HTML line projection. Each push
/// returns a scope that restores the previous projection.
/// </summary>
internal sealed class LayoutSourceProjections
{
    private MarkdownSourceProjection? _markdown;
    private MarkdownSourceProjection? _safeHtml;

    /// <summary>Projection of the transformed Markdown fragment being built, if any.</summary>
    internal MarkdownSourceProjection? Markdown => _markdown;

    /// <summary>Projection of the composed raw-HTML string handed to SafeHtmlParser, if any.</summary>
    internal MarkdownSourceProjection? SafeHtml => _safeHtml;

    /// <summary>
    /// Scopes a transformed Markdown fragment's source mapping to the synchronous
    /// construction pass that consumes its already-parsed nodes.
    /// </summary>
    internal MarkdownSourceProjectionScope PushMarkdown(MarkdownSourceProjection projection)
    {
        ArgumentNullException.ThrowIfNull(projection);
        MarkdownSourceProjection? previous = _markdown;
        _markdown = projection;
        return new MarkdownSourceProjectionScope(this, previous, safeHtml: false);
    }

    /// <summary>
    /// Scopes a composed raw-HTML line projection. Its coordinates are local to
    /// the exact string handed to SafeHtmlParser, not to the Markdig AST.
    /// </summary>
    internal MarkdownSourceProjectionScope PushSafeHtml(MarkdownSourceProjection? projection)
    {
        MarkdownSourceProjection? previous = _safeHtml;
        _safeHtml = projection;
        return new MarkdownSourceProjectionScope(this, previous, safeHtml: true);
    }

    internal SourceSpan MapSafeHtmlSourceRange(int localStart, int localLength)
        => _safeHtml?.MapRangeOrEmpty(localStart, localLength)
            ?? new SourceSpan(localStart, localLength);

    internal void Restore(MarkdownSourceProjection? projection, bool safeHtml)
    {
        if (safeHtml)
            _safeHtml = projection;
        else
            _markdown = projection;
    }
}
