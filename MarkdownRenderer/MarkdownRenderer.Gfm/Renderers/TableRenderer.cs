using System.Collections.Generic;
using System.Text;
using Markdig.Extensions.Tables;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Parsing;

namespace MarkdownRenderer.Gfm.Renderers;

/// <summary>
/// Renders a Markdig <see cref="Table"/> as a <see cref="TableBox"/>.
/// </summary>
public sealed class TableRenderer : MarkdownNodeRenderer<Table>
{
    public override BlockBox? BuildBlock(Table table, MarkdownLayoutContext context)
    {
        var headerRows = new List<string[]>();
        var bodyRows = new List<string[]>();

        int colCount = 0;
        foreach (var item in table)
        {
            if (item is not TableRow row) continue;
            var cells = new List<string>();
            foreach (var cellItem in row)
            {
                if (cellItem is TableCell cell)
                    cells.Add(ExtractCellText(cell));
            }
            if (cells.Count > colCount) colCount = cells.Count;

            if (row.IsHeader)
                headerRows.Add(cells.ToArray());
            else
                bodyRows.Add(cells.ToArray());
        }

        return new TableBox(context, headerRows.ToArray(), bodyRows.ToArray());
    }

    private static string ExtractCellText(TableCell cell)
    {
        var sb = new StringBuilder();
        foreach (var child in cell)
        {
            if (child is ParagraphBlock p && p.Inline is not null)
                GfmChildBuilder.FlattenInlines(p.Inline, sb);
        }
        return sb.ToString().Trim();
    }
}
