namespace MarkdownRenderer.Controls.Selection;

internal sealed class MarkdownTableClipboardSnapshot
{
    private readonly string[][] _rows;
    private readonly int _headerRowCount;

    internal MarkdownTableClipboardSnapshot(
        string[][] rows,
        int headerRowCount)
    {
        _rows = rows;
        _headerRowCount = headerRowCount;
    }

    internal MarkdownTableClipboardPayload Format() =>
        MarkdownTableClipboardFormatter.Format(_rows, _headerRowCount);

    internal bool HasText
    {
        get
        {
            if (_rows.Length == 0)
                return false;
            if (_rows.Length > 1)
                return true;

            string[] row = _rows[0];
            if (row.Length > 1)
                return true;

            return row.Length > 0 && row[0].Length > 0;
        }
    }
}
