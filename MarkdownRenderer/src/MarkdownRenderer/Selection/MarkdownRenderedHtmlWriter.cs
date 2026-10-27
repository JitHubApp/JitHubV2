using System;
using System.Collections.Generic;
using System.Net;
using System.Text;
using MarkdownRenderer.Accessibility;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Theming;

namespace MarkdownRenderer.Selection;

/// <summary>
/// Serializes a selected range from the committed semantic display plan into
/// a conservative CF_HTML fragment. Source HTML is never reparsed or executed.
/// </summary>
internal static class MarkdownRenderedHtmlWriter
{
    private const string InlineCodeOpenTag = "<code style=\"white-space: pre-wrap\">";

    public static string BuildFragment(
        MarkdownSemanticDocument document,
        int textStart,
        int textEnd)
    {
        textStart = Math.Clamp(textStart, 0, document.Text.Length);
        textEnd = Math.Clamp(textEnd, textStart, document.Text.Length);
        if (textEnd <= textStart)
            return string.Empty;

        var output = new StringBuilder(Math.Max(64, textEnd - textStart + 32));
        output.Append("<div data-markdown-renderer=\"1\">");
        AppendStructuredRange(output, document, document.Root, textStart, textEnd);
        output.Append("</div>");
        return output.ToString();
    }

    private static void AppendStructuredRange(
        StringBuilder output,
        MarkdownSemanticDocument document,
        MarkdownSemanticNode root,
        int textStart,
        int textEnd)
    {
        int cursor = textStart;

        // Structural markup comes from the committed semantic tree, never from
        // reparsing source. Advancing past a handled parent prevents copying its
        // descendants twice. Cell-local traversal visits only that cell's tree.
        foreach (MarkdownSemanticNode node in MarkdownSemanticDocument.EnumerateDepthFirst(root))
        {
            if ((node.Role != MarkdownSemanticRole.CodeBlock && node.Role != MarkdownSemanticRole.Table) ||
                node.TextEnd <= cursor || node.TextStart >= textEnd)
            {
                continue;
            }

            int start = Math.Max(cursor, node.TextStart);
            int end = Math.Min(textEnd, node.TextEnd);
            if (start >= end)
                continue;

            if (node.Role == MarkdownSemanticRole.Table)
            {
                if (!TryGetSelectedTableRows(node, start, end, out int firstRow, out int lastRow))
                    continue;

                AppendInlineRange(output, document, cursor, start);
                AppendTable(output, document, node, start, end, firstRow, lastRow);
            }
            else
            {
                AppendInlineRange(output, document, cursor, start);
                // One preformatted region per logical block, not per shaping
                // run or virtualized chunk, retains authored whitespace.
                output.Append("<pre><code>");
                output.Append(WebUtility.HtmlEncode(document.Text.Substring(start, end - start)));
                output.Append("</code></pre>");
            }
            cursor = end;
        }

        AppendInlineRange(output, document, cursor, textEnd);
    }

    private static bool TryGetSelectedTableRows(
        MarkdownSemanticNode table,
        int textStart,
        int textEnd,
        out int firstRow,
        out int lastRow)
    {
        firstRow = int.MaxValue;
        lastRow = -1;
        if (table.RowCount <= 0 || table.ColumnCount <= 0)
            return false;

        if (textStart <= table.TextStart && textEnd >= table.TextEnd)
        {
            firstRow = 0;
            lastRow = table.RowCount - 1;
            return true;
        }

        foreach (MarkdownSemanticNode cell in table.Children)
        {
            if (cell.TextStart < textEnd && GetTableCellContentEnd(cell) > textStart)
            {
                firstRow = Math.Min(firstRow, cell.Row);
                lastRow = Math.Max(lastRow, cell.Row);
            }
        }
        // A range containing only a generated cell separator is still copied
        // as its selected text, not silently replaced by an empty table.
        return lastRow >= firstRow;
    }

    private static void AppendTable(
        StringBuilder output,
        MarkdownSemanticDocument document,
        MarkdownSemanticNode table,
        int textStart,
        int textEnd,
        int firstRow,
        int lastRow)
    {
        output.Append("<table><tbody>");
        IReadOnlyList<MarkdownSemanticNode> cells = table.Children;
        int cellIndex = 0;
        for (int row = firstRow; row <= lastRow; row++)
        {
            while (cellIndex < cells.Count && cells[cellIndex].Row < row)
                cellIndex++;
            bool isHeaderRow = cellIndex < cells.Count && cells[cellIndex].Row == row && cells[cellIndex].IsHeader;
            output.Append("<tr>");
            for (int column = 0; column < table.ColumnCount; column++)
            {
                MarkdownSemanticNode? cell = cellIndex < cells.Count &&
                    cells[cellIndex].Row == row && cells[cellIndex].Column == column
                    ? cells[cellIndex++]
                    : null;
                bool isHeader = cell?.IsHeader ?? isHeaderRow;
                output.Append(isHeader ? "<th>" : "<td>");
                if (cell is not null)
                {
                    int start = Math.Max(textStart, cell.TextStart);
                    int end = Math.Min(textEnd, GetTableCellContentEnd(cell));
                    if (start < end)
                        AppendStructuredRange(output, document, cell, start, end);
                }
                // Empty/unselected cells keep their column, without copying
                // any unselected text, labels, destinations, or source markup.
                output.Append(isHeader ? "</th>" : "</td>");
            }
            output.Append("</tr>");
        }
        output.Append("</tbody></table>");
    }

    private static int GetTableCellContentEnd(MarkdownSemanticNode cell)
    {
        // BuildTable wraps its one committed InlineContainerBox node. That
        // child's range excludes the synthetic per-cell block separator but
        // includes authored hard breaks and preformatted content unchanged.
        return cell.Children.Count == 1 ? cell.Children[0].TextEnd : cell.TextEnd;
    }

    private static void AppendInlineRange(
        StringBuilder output,
        MarkdownSemanticDocument document,
        int textStart,
        int textEnd)
    {
        if (textStart >= textEnd)
            return;

        int cursor = textStart;
        int spanStart = document.GetTextSpanStartIndex(textStart);
        for (int i = spanStart; i < document.TextSpans.Count; i++)
        {
            MarkdownTextSpan span = document.TextSpans[i];
            if (span.TextEnd <= textStart)
                continue;
            if (span.TextStart >= textEnd)
                break;

            int start = Math.Max(textStart, span.TextStart);
            int end = Math.Min(textEnd, span.TextEnd);
            if (start > cursor)
                AppendEncoded(output, document.Text, cursor, start);
            if (end > start)
                AppendSpan(output, document, span, start, end);
            cursor = Math.Max(cursor, end);
        }

        if (cursor < textEnd)
            AppendEncoded(output, document.Text, cursor, textEnd);
    }

    private static void AppendSpan(
        StringBuilder output,
        MarkdownSemanticDocument document,
        MarkdownTextSpan span,
        int start,
        int end)
    {
        string? open = null;
        string? close = null;
        if (span.InlineRun is LinkRun link && TryGetSafeLink(link.Url, out string safeUrl))
        {
            open = $"<a href=\"{WebUtility.HtmlEncode(safeUrl)}\">";
            close = "</a>";
        }
        else if (span.InlineRun is InlineImageRun)
        {
            open = "<span role=\"img\">";
            close = "</span>";
        }
        else if (span.InlineRun is AbbreviationRun abbreviation &&
                 !string.IsNullOrWhiteSpace(abbreviation.Expansion))
        {
            open = $"<abbr title=\"{WebUtility.HtmlEncode(abbreviation.Expansion)}\">";
            close = "</abbr>";
        }
        else if (span.ImageBox is not null)
        {
            open = "<span role=\"img\">";
            close = "</span>";
        }
        else
        {
            string elementKey = string.IsNullOrEmpty(span.InlineRun?.ElementKey)
                ? span.InlineBox?.ElementKey ?? string.Empty
                : span.InlineRun.ElementKey;
            (open, close) = TagsFor(elementKey);
        }

        IReadOnlyList<string>? modifiers = span.InlineRun?.StyleModifierKeys;
        if (modifiers is not null)
        {
            for (int index = 0; index < modifiers.Count; index++)
                output.Append(TagsFor(modifiers[index]).Open);
        }

        if (open is not null)
            output.Append(open);
        if (span.InlineRun is LinkRun styledLink &&
            styledLink.InlineStyleMap is { } map &&
            map.IsValidForTextLength(styledLink.TextLength) &&
            span.TextEnd - span.TextStart == styledLink.TextLength &&
            document.Text.AsSpan(span.TextStart, styledLink.TextLength).SequenceEqual(styledLink.Text))
        {
            AppendStyledLinkText(output, document.Text, span.TextStart, map, start, end);
        }
        else
        {
            AppendEncoded(output, document.Text, start, end);
        }
        if (close is not null)
            output.Append(close);

        if (modifiers is not null)
        {
            for (int index = modifiers.Count - 1; index >= 0; index--)
                output.Append(TagsFor(modifiers[index]).Close);
        }
    }

    private static void AppendStyledLinkText(
        StringBuilder output,
        string text,
        int runStart,
        LinkInlineStyleMap map,
        int textStart,
        int textEnd)
    {
        int cursor = textStart;
        var path = new List<LinkInlineStylePath>();
        for (int index = 0; index < map.Count; index++)
        {
            LinkInlineStyleSpan styled = map.GetSpan(index);
            int start = Math.Max(textStart, runStart + styled.Start);
            int end = Math.Min(textEnd, runStart + styled.Start + styled.Length);
            if (start >= end)
                continue;

            if (cursor < start)
                AppendEncoded(output, text, cursor, start);

            // A path is leaf-to-root. Open in source ancestry order and close
            // in reverse, without recursion or serializing authored CSS/HTML.
            path.Clear();
            for (LinkInlineStylePath? current = styled.Path; current is not null; current = current.Parent)
                path.Add(current);
            for (int depth = path.Count - 1; depth >= 0; depth--)
                output.Append(TagsFor(path[depth].StyleKey).Open);
            AppendEncoded(output, text, start, end);
            for (int depth = 0; depth < path.Count; depth++)
                output.Append(TagsFor(path[depth].StyleKey).Close);
            cursor = end;
        }

        if (cursor < textEnd)
            AppendEncoded(output, text, cursor, textEnd);
    }

    private static (string? Open, string? Close) TagsFor(string elementKey) => elementKey switch
    {
        MarkdownElementKeys.Strong => ("<strong>", "</strong>"),
        MarkdownElementKeys.Emphasis => ("<em>", "</em>"),
        MarkdownElementKeys.Strikethrough => ("<s>", "</s>"),
        MarkdownElementKeys.CodeInline => (InlineCodeOpenTag, "</code>"),
        MarkdownElementKeys.CodeBlock => (InlineCodeOpenTag, "</code>"),
        MarkdownElementKeys.Subscript => ("<sub>", "</sub>"),
        MarkdownElementKeys.Superscript => ("<sup>", "</sup>"),
        MarkdownElementKeys.Inserted => ("<ins>", "</ins>"),
        MarkdownElementKeys.Marked => ("<mark>", "</mark>"),
        _ => (null, null),
    };

    private static void AppendEncoded(
        StringBuilder output,
        string text,
        int start,
        int end)
    {
        string encoded = WebUtility.HtmlEncode(text.Substring(start, end - start));
        output.Append(encoded
            .Replace("\r\n", "<br />", StringComparison.Ordinal)
            .Replace("\r", "<br />", StringComparison.Ordinal)
            .Replace("\n", "<br />", StringComparison.Ordinal));
    }

    private static bool TryGetSafeLink(string value, out string safeUrl)
    {
        safeUrl = string.Empty;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp &&
             uri.Scheme != Uri.UriSchemeHttps &&
             uri.Scheme != Uri.UriSchemeMailto))
        {
            return false;
        }

        safeUrl = uri.AbsoluteUri;
        return true;
    }
}
