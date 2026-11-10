using System;
using System.Collections.Generic;
using MarkdownRenderer.Layout;

namespace MarkdownRenderer.Html.Internal.Renderers;

/// <summary>
/// Inherited inline state while building safe-HTML runs: link target,
/// style, heading semantics, class aliases and the source projection.
/// </summary>
internal readonly record struct HtmlInlineContext(
    string? LinkUrl,
    string? LinkTitle,
    string? StyleKey,
    string? HeadingKey,
    IReadOnlyList<string> StyleAliases,
    bool InsideLink = false,
    MarkdownSourceProjection? SourceProjection = null)
{
    public static HtmlInlineContext Empty => new(null, null, null, null, Array.Empty<string>());

    public SourceSpan MapSourceRange(int sourceStart, int sourceLength)
        => SourceProjection?.MapRangeOrEmpty(sourceStart, sourceLength)
            ?? new SourceSpan(sourceStart, sourceLength);
}
