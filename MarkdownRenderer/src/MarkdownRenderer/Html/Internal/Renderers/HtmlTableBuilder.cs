using System;
using System.Collections.Generic;
using System.Linq;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Parsing.Internal.SafeHtml;
using MarkdownRenderer.Theming;

namespace MarkdownRenderer.Html.Internal.Renderers;

/// <summary>
/// Builds a safe-HTML table, its captions, and its preformatted cells.
/// </summary>
internal sealed class HtmlTableBuilder
{
    private const int MaxTableColumns = 64;

    private readonly IHtmlBlockBuilderHost _host;
    private readonly HtmlInlineBuilder _inline;

    internal HtmlTableBuilder(IHtmlBlockBuilderHost host, HtmlInlineBuilder inline)
    {
        _host = host;
        _inline = inline;
    }

    internal BlockBox? BuildTable(
        SafeHtmlElement table,
        MarkdownLayoutContext context,
        int sourceOffset,
        SafeHtmlAlignment alignment)
    {
        StackBox? captionStack = BuildCaptionStack(table, context, sourceOffset, alignment);

        List<HtmlTableRow> rows = [];
        CollectRows(table, inHeaderGroup: false, rows);
        int columnCount = Math.Min(
            MaxTableColumns,
            rows.Count == 0 ? 0 : rows.Max(row => row.Cells.Sum(cell => cell.ColumnSpan)));
        if (columnCount == 0)
        {
            return captionStack is null ? null : HtmlBoxes.ReturnIfNonEmpty(captionStack, context);
        }

        List<InlineContainerBox[]> headerRows = [];
        List<InlineContainerBox[]> bodyRows = [];
        var alignments = new TableBox.CellAlignment[columnCount];
        foreach (HtmlTableRow row in rows)
        {
            InlineContainerBox[] cells = BuildRowCells(row, columnCount, alignments, context, sourceOffset);
            (row.IsHeader ? headerRows : bodyRows).Add(cells);
        }

        return PublishTable(headerRows, bodyRows, alignments, captionStack, context);
    }

    private StackBox? BuildCaptionStack(
        SafeHtmlElement table,
        MarkdownLayoutContext context,
        int sourceOffset,
        SafeHtmlAlignment alignment)
    {
        SafeHtmlElement[] captions = table.Children
            .OfType<SafeHtmlElement>()
            .Where(static child => child.Name == "caption")
            .ToArray();
        StackBox? captionStack = captions.Length == 0
            ? null
            : HtmlBoxes.CreateStack(context, collapsePositiveAdjacentMargins: true);
        if (captionStack is not null)
        {
            foreach (SafeHtmlElement caption in captions)
            {
                BlockBox? captionBlock = _host.BuildElementBlock(caption, context, sourceOffset, alignment);
                if (captionBlock is not null)
                    HtmlBoxes.AddChild(captionStack, captionBlock, context);
            }
        }

        return captionStack;
    }

    private InlineContainerBox[] BuildRowCells(
        HtmlTableRow row,
        int columnCount,
        TableBox.CellAlignment[] alignments,
        MarkdownLayoutContext context,
        int sourceOffset)
    {
        var cells = new InlineContainerBox[columnCount];
        int column = 0;
        foreach (HtmlTableCell cell in row.Cells)
        {
            if (column >= columnCount)
            {
                break;
            }

            string key = row.IsHeader ? MarkdownElementKeys.TableHeader : MarkdownElementKeys.TableCell;
            SafeHtmlElement? preformatted = HtmlElementQueries.FindDescendant(cell.Element, "pre");
            bool hasMixedContent = preformatted is not null &&
                HasRenderableContentOutside(cell.Element, preformatted);
            InlineContainerBox box = preformatted is null || hasMixedContent
                ? HtmlBoxes.CreateInlineBox(context, key, SafeHtmlParser.GetAlignment(cell.Element))
                : BuildPreformattedTableCell(
                    preformatted,
                    context,
                    sourceOffset,
                    SafeHtmlParser.GetAlignment(cell.Element));
            if (preformatted is null || hasMixedContent)
                _inline.PopulateInline(box, cell.Element.Children, context, sourceOffset, HtmlInlineContext.Empty);
            cells[column] = box;
            if (alignments[column] == TableBox.CellAlignment.Default)
            {
                alignments[column] = HtmlAlignments.ToCellAlignment(SafeHtmlParser.GetAlignment(cell.Element));
            }

            column++;
            for (int span = 1; span < cell.ColumnSpan && column < columnCount; span++, column++)
            {
                cells[column] = HtmlBoxes.CreateInlineBox(context, key, SafeHtmlAlignment.Inherit);
            }
        }

        for (; column < columnCount; column++)
        {
            cells[column] = HtmlBoxes.CreateInlineBox(
                context,
                row.IsHeader ? MarkdownElementKeys.TableHeader : MarkdownElementKeys.TableCell,
                SafeHtmlAlignment.Inherit);
        }

        return cells;
    }

    private static BlockBox PublishTable(
        List<InlineContainerBox[]> headerRows,
        List<InlineContainerBox[]> bodyRows,
        TableBox.CellAlignment[] alignments,
        StackBox? captionStack,
        MarkdownLayoutContext context)
    {
        InlineContainerBox[][] publishedHeaderRows = headerRows.ToArray();
        InlineContainerBox[][] publishedBodyRows = bodyRows.ToArray();
        var tableBox = new TableBox(context, publishedHeaderRows, publishedBodyRows, alignments)
        {
            BlockIndex = context.NextBlockIndex(),
        };
        context.RegisterConstructionBlock(tableBox);
        foreach (InlineContainerBox[] row in publishedHeaderRows)
        {
            foreach (InlineContainerBox cell in row)
                context.AdoptConstructionBlock(cell);
        }
        foreach (InlineContainerBox[] row in publishedBodyRows)
        {
            foreach (InlineContainerBox cell in row)
                context.AdoptConstructionBlock(cell);
        }

        if (captionStack is null)
            return tableBox;
        if (captionStack.Children.Count == 0)
        {
            context.DiscardConstructionBlock(captionStack);
            return tableBox;
        }

        HtmlBoxes.AddChild(captionStack, tableBox, context);
        return captionStack;
    }

    private static InlineContainerBox BuildPreformattedTableCell(
        SafeHtmlElement pre,
        MarkdownLayoutContext context,
        int sourceOffset,
        SafeHtmlAlignment alignment)
    {
        HtmlPreformattedTextProjection? textProjection = HtmlPreformattedTextProjection.Create(
            pre,
            context,
            sourceOffset);
        string text = textProjection?.Text ?? string.Empty;
        SourceSpan sourceSpan = textProjection?.SourceSpan ?? SourceSpan.Empty;
        var box = new InlineContainerBox(context, MarkdownElementKeys.CodeBlock)
        {
            BlockIndex = context.NextBlockIndex(),
            TextAlignment = HtmlAlignments.ToCanvasAlignment(alignment),
            CodeBlockTextOffset = 0,
            CodeBlockTextLength = text.Length,
        };
        context.RegisterConstructionBlock(box);
        if (text.Length > 0)
        {
            box.Add(new TextRun(text)
            {
                ElementKey = MarkdownElementKeys.CodeBlock,
                SourceSpan = sourceSpan,
            }, textProjection!.SourceProjection!);
        }

        return box;
    }

    private static bool HasRenderableContentOutside(
        SafeHtmlElement root,
        SafeHtmlElement excludedPreformatted)
    {
        foreach (SafeHtmlNode child in root.Children)
        {
            if (ReferenceEquals(child, excludedPreformatted))
                continue;

            if (child is SafeHtmlText text)
            {
                if (!string.IsNullOrWhiteSpace(text.DecodedText))
                    return true;
                continue;
            }

            if (child is not SafeHtmlElement element ||
                SafeHtmlParser.IsSuppressedElement(element.Name))
            {
                continue;
            }

            if (element.Name is "img" or "picture" or "video" or "audio" or "svg" or "input" or "hr" ||
                HasRenderableContentOutside(element, excludedPreformatted))
            {
                return true;
            }
        }

        return false;
    }

    private static void CollectRows(SafeHtmlElement element, bool inHeaderGroup, List<HtmlTableRow> rows)
    {
        // A caption is a sibling block rendered above its table. Its descendants
        // are not rows in the containing table (and may include a nested table).
        if (element.Name == "caption")
            return;

        bool headerGroup = inHeaderGroup || element.Name == "thead";
        if (element.Name == "tr")
        {
            List<HtmlTableCell> cells = [];
            bool hasHeaderCell = false;
            foreach (SafeHtmlElement cell in element.Children
                         .OfType<SafeHtmlElement>()
                         .Where(child => child.Name is "td" or "th"))
            {
                hasHeaderCell |= cell.Name == "th";
                int span = 1;
                if (cell.TryGetAttribute("colspan", out string value) && int.TryParse(value, out int parsed))
                {
                    span = Math.Clamp(parsed, 1, MaxTableColumns);
                }

                cells.Add(new HtmlTableCell(cell, span));
            }

            if (cells.Count > 0)
            {
                rows.Add(new HtmlTableRow(cells, headerGroup || hasHeaderCell));
            }

            return;
        }

        foreach (SafeHtmlElement child in element.Children.OfType<SafeHtmlElement>())
        {
            CollectRows(child, headerGroup, rows);
        }
    }
}
