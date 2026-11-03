namespace MarkdownRenderer.Layout.Boxes;

/// <summary>
/// One source cell, its prepared clone and their inline transfer at a fixed
/// table position, captured for a table measured-placement transfer.
/// </summary>
internal readonly struct TableMeasuredCellFence(
    bool IsHeader,
    int RowIndex,
    int ColumnIndex,
    InlineContainerBox Source,
    InlineContainerBox Prepared,
    InlineMeasuredPlacementTransfer Transfer)
{
    public bool IsHeader { get; init; } = IsHeader;
    public int RowIndex { get; init; } = RowIndex;
    public int ColumnIndex { get; init; } = ColumnIndex;
    public InlineContainerBox Source { get; init; } = Source;
    public InlineContainerBox Prepared { get; init; } = Prepared;
    public InlineMeasuredPlacementTransfer Transfer { get; init; } = Transfer;
}
