using System.Collections.Generic;

namespace MarkdownRenderer.Html.Renderers;

/// <summary>
/// One collected safe-HTML table row.
/// Construction-only: never an equality key or copied with <c>with</c>,
/// so it is immutable without record equality or printing helpers.
/// </summary>
internal sealed class HtmlTableRow(IReadOnlyList<HtmlTableCell> Cells, bool IsHeader)
{
    internal IReadOnlyList<HtmlTableCell> Cells { get; } = Cells;
    internal bool IsHeader { get; } = IsHeader;
}
