using MarkdownRenderer.Layout.Internal.Inlines;

namespace MarkdownRenderer.Layout.Internal.Tables;

/// <summary>
/// The source and prepared row arrays one table measured-placement transfer
/// expects at a header or body row index.
/// </summary>
internal readonly struct TableMeasuredRowFence(
    bool IsHeader,
    int RowIndex,
    InlineContainerBox[] SourceRow,
    InlineContainerBox[] PreparedRow)
{
    public bool IsHeader { get; init; } = IsHeader;
    public int RowIndex { get; init; } = RowIndex;
    public InlineContainerBox[] SourceRow { get; init; } = SourceRow;
    public InlineContainerBox[] PreparedRow { get; init; } = PreparedRow;
}
