namespace MarkdownRenderer.Controls;

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
