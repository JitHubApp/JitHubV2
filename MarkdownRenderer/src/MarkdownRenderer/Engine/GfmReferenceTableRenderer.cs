using System;
using Markdig.Extensions.Tables;
using Markdig.Renderers;
using Markdig.Renderers.Html;
using Markdig.Syntax;

namespace MarkdownRenderer;

// Adapted from Markdig's BSD-2-Clause HtmlTableRenderer. cmark-gfm 0.29
// serializes column alignment with the historical align attribute.
internal sealed class GfmReferenceTableRenderer : HtmlObjectRenderer<Table>
{
    protected override void Write(HtmlRenderer renderer, Table table)
    {
        if (!renderer.EnableHtmlForBlock)
        {
            WritePlainText(renderer, table);
            return;
        }

        renderer.EnsureLine();
        renderer.Write("<table").WriteAttributes(table).WriteLine('>');
        bool hasBody = false;
        bool hasHeader = false;
        bool headerOpen = false;
        foreach (Block rowObject in table)
        {
            var row = (TableRow)rowObject;
            if (row.IsHeader)
            {
                if (!hasHeader)
                {
                    renderer.WriteLine("<thead>");
                    headerOpen = true;
                }
                hasHeader = true;
            }
            else if (!hasBody)
            {
                if (headerOpen)
                {
                    renderer.WriteLine("</thead>");
                    headerOpen = false;
                }
                renderer.WriteLine("<tbody>");
                hasBody = true;
            }

            renderer.Write("<tr").WriteAttributes(row).WriteLine('>');
            for (int index = 0; index < row.Count; index++)
            {
                var cell = (TableCell)row[index];
                renderer.EnsureLine();
                renderer.Write(row.IsHeader ? "<th" : "<td");
                if (cell.ColumnSpan != 1)
                    renderer.Write($" colspan=\"{cell.ColumnSpan}\"");
                if (cell.RowSpan != 1)
                    renderer.Write($" rowspan=\"{cell.RowSpan}\"");
                if (table.ColumnDefinitions.Count > 0)
                    WriteAlignment(renderer, table, cell, index);
                renderer.WriteAttributes(cell);
                renderer.Write('>');

                bool previousImplicit = renderer.ImplicitParagraph;
                if (cell.Count == 1)
                    renderer.ImplicitParagraph = true;
                renderer.Write(cell);
                renderer.ImplicitParagraph = previousImplicit;
                renderer.WriteLine(row.IsHeader ? "</th>" : "</td>");
            }
            renderer.WriteLine("</tr>");
        }

        if (hasBody)
            renderer.WriteLine("</tbody>");
        else if (headerOpen)
            renderer.WriteLine("</thead>");
        renderer.WriteLine("</table>");
    }

    private static void WritePlainText(HtmlRenderer renderer, Table table)
    {
        bool previousImplicit = renderer.ImplicitParagraph;
        renderer.ImplicitParagraph = true;
        foreach (Block rowObject in table)
        {
            var row = (TableRow)rowObject;
            foreach (Block cellObject in row)
            {
                renderer.Write((TableCell)cellObject);
                renderer.Write(' ');
            }
        }
        renderer.ImplicitParagraph = previousImplicit;
    }

    private static void WriteAlignment(HtmlRenderer renderer, Table table, TableCell cell, int index)
    {
        int columnIndex = cell.ColumnIndex < 0 ||
            cell.ColumnIndex >= table.ColumnDefinitions.Count
                ? index
                : cell.ColumnIndex;
        columnIndex = Math.Min(columnIndex, table.ColumnDefinitions.Count - 1);
        TableColumnAlign? alignment = table.ColumnDefinitions[columnIndex].Alignment;
        if (alignment.HasValue)
        {
            string value = alignment.Value switch
            {
                TableColumnAlign.Left => "left",
                TableColumnAlign.Center => "center",
                TableColumnAlign.Right => "right",
                _ => string.Empty,
            };
            if (value.Length > 0)
                renderer.Write($" align=\"{value}\"");
        }
    }
}
