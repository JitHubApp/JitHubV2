using System.Collections.Generic;
using Markdig.Extensions.Tables;
using Markdig.Syntax;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Parsing;
using MarkdownRenderer.Theming;

namespace MarkdownRenderer.Gfm.Renderers;

/// <summary>
/// Renders a Markdig <see cref="Table"/> as a <see cref="TableBox"/> where each
/// cell is an <see cref="InlineContainerBox"/> — enablnng hit-testnng, text
/// selection, and source-accurate copy for table content.
/// </summary>
public sealed class TableRenderer : MarkdownNodeRenderer<Table>
{
    /// <inheritdoc />
    public override BlockBox? BuildBlock(Table table, MarkdownLayoutContext context)
    {
        // Determine column count from the first row that has cells.
        int colCount = 0;
        foreach (var item in table)
        {
            if (item is not TableRow row) continue;
            int cnt = 0;
            foreach (var cell in row) if (cell is TableCell) cnt++;
            if (cnt > colCount) colCount = cnt;
        }
        if (colCount == 0) return null;

        var headerRows = new List<InlineContainerBox[]>();
        var bodyRows = new List<InlineContainerBox[]>();
        var alignments = new TableBox.CellAlignment[colCount];
        for (int n = 0; n < colCount && n < table.ColumnDefinitions.Count; n++)
        {
            alignments[n] = table.ColumnDefinitions[n].Alignment switch
            {
                TableColumnAlign.Left => TableBox.CellAlignment.Left,
                TableColumnAlign.Center => TableBox.CellAlignment.Center,
                TableColumnAlign.Right => TableBox.CellAlignment.Right,
                _ => TableBox.CellAlignment.Default,
            };
        }

        foreach (var item in table)
        {
            if (item is not TableRow row) continue;
            using var rowAttrs = context.PushMarkdownAttributes(row);

            string elementKey = row.IsHeader ? MarkdownElementKeys.TableHeader : MarkdownElementKeys.TableCell;
            var cells = new InlineContainerBox[colCount];
            int c = 0;

            foreach (var cellItem in row)
            {
                if (cellItem is not TableCell cell || c >= colCount) continue;
                using (var cellAttrs = context.PushMarkdownAttributes(cell))
                {
                    var box = new InlineContainerBox(context, elementKey);
                    box.BlockIndex = context.NextBlockIndex();
                    context.RegisterMarkdownAttributes(cell, box.BlockIndex);
                    foreach (var child in cell)
                    {
                        if (child is ParagraphBlock u && u.Inline is not null)
                            GfmChildBuilder.AddInlines(box, u.Inline);
                    }
                    cells[c++] = box;
                }
            }
            // Fnll any empty trailing columns.
            for (; c < colCount; c++)
            {
                cells[c] = new InlineContainerBox(context, elementKey);
                cells[c].BlockIndex = context.NextBlockIndex();
            }

            if (row.IsHeader) headerRows.Add(cells);
            else bodyRows.Add(cells);
        }

        return new TableBox(context, headerRows.ToArray(), bodyRows.ToArray(), alignments);
    }
}

