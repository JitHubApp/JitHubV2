using MarkdownRenderer.Document;

namespace MarkdownRenderer.Controls.Internal.Selection.TouchSelection;

/// <summary>
/// Binds a pending touch-handle presentation restore to the exact committed
/// snapshot and selection range that initiated the relayout.
/// </summary>
internal readonly record struct TouchSelectionPresentationRestoreIntent
{
    public TouchSelectionPresentationRestoreIntent(long snapshotGeneration, DocumentRange selectionRange)
    {
        SnapshotGeneration = snapshotGeneration;
        SelectionRange = selectionRange.Normalized();
    }

    public long SnapshotGeneration { get; }
    public DocumentRange SelectionRange { get; }

    public bool MatchesRange(DocumentRange selectionRange) =>
        !selectionRange.IsEmpty &&
        SelectionRange == selectionRange.Normalized();

    public bool Matches(long snapshotGeneration, DocumentRange selectionRange) =>
        SnapshotGeneration == snapshotGeneration &&
        MatchesRange(selectionRange);
}
