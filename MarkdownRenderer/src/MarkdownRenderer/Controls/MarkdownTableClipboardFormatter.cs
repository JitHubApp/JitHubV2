using System.Collections.Generic;
using System.Net;
using System.Text;
using MarkdownRenderer.Hosting;

namespace MarkdownRenderer.Controls;

/// <summary>Builds a conservative rendered table payload for clipboard fallbacks.</summary>
internal static class MarkdownTableClipboardFormatter
{
    internal static MarkdownTableClipboardPayload Format(
        string[][] rows,
        int headerRowCount)
    {
        var text = new StringBuilder();
        var html = new StringBuilder("<table>");
        if (rows.Length == 0)
            return new MarkdownTableClipboardPayload(string.Empty, "<table></table>");

        int boundedHeaderRows = System.Math.Clamp(headerRowCount, 0, rows.Length);

        for (int rowIndex = 0; rowIndex < rows.Length; rowIndex++)
        {
            if (rowIndex == 0 && boundedHeaderRows > 0)
                html.Append("<thead>");
            if (rowIndex == boundedHeaderRows && boundedHeaderRows > 0)
                html.Append("</thead><tbody>");
            else if (rowIndex == 0 && boundedHeaderRows == 0)
                html.Append("<tbody>");

            if (rowIndex > 0)
                text.AppendLine();
            html.Append("<tr>");

            string[] row = rows[rowIndex];
            for (int columnIndex = 0; columnIndex < row.Length; columnIndex++)
            {
                if (columnIndex > 0)
                    text.Append('\t');

                string value = NormalizeCellText(row[columnIndex]);
                text.Append(value);
                string tag = rowIndex < boundedHeaderRows ? "th" : "td";
                html.Append('<').Append(tag).Append('>')
                    .Append(WebUtility.HtmlEncode(value))
                    .Append("</").Append(tag).Append('>');
            }

            html.Append("</tr>");
        }

        if (boundedHeaderRows > 0 && boundedHeaderRows == rows.Length)
            html.Append("</thead>");
        else
            html.Append("</tbody>");
        html.Append("</table>");
        return new MarkdownTableClipboardPayload(text.ToString(), html.ToString());
    }

    private static string NormalizeCellText(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        return value
            .Replace("\r\n", " ", System.StringComparison.Ordinal)
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Replace('\t', ' ');
    }
}
