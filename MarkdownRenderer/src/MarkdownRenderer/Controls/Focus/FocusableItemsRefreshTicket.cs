namespace MarkdownRenderer.Controls.Focus;

/// <summary>
/// One request to collect the focusable items again: the snapshot
/// generation, automation document generation and layout revision it was
/// made for, and its version.
/// </summary>
internal readonly record struct FocusableItemsRefreshTicket(
    long SnapshotGeneration,
    long AutomationDocumentGeneration,
    long LayoutRevision,
    long RequestVersion,
    bool IsPending);
