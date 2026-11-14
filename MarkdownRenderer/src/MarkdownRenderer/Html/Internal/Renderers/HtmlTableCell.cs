using MarkdownRenderer.Parsing.Internal.SafeHtml;

namespace MarkdownRenderer.Html.Internal.Renderers;

/// <summary>
/// One collected safe-HTML table cell and its clamped column span.
/// Construction-only: never an equality key or copied with <c>with</c>,
/// so it is immutable without record equality or printing helpers.
/// </summary>
internal sealed class HtmlTableCell(SafeHtmlElement Element, int ColumnSpan)
{
    internal SafeHtmlElement Element { get; } = Element;
    internal int ColumnSpan { get; } = ColumnSpan;
}
