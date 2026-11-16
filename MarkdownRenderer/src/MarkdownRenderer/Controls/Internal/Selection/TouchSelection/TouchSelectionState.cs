namespace MarkdownRenderer.Controls.Internal.Selection.TouchSelection;

internal enum TouchSelectionState
{
    Idle,
    Contact,
    Selected,
    DraggingStartHandle,
    DraggingEndHandle,
    ContextMenu,
    Canceled,
    Disposed,
}
