namespace MarkdownRenderer.Controls.Selection;

/// <summary>
/// The handle drag state <see cref="SelectionHandlePlacement"/> reads so a
/// captured handle is never moved or hidden mid-drag.
/// </summary>
internal interface ISelectionHandleDragView
{
    /// <summary>The logical endpoint the drag is moving, or none.</summary>
    SelectionHandleEndpoint ActiveEndpoint { get; }

    /// <summary>True while a confirmed pointer drag owns a handle.</summary>
    bool IsPointerDragActive { get; }

    /// <summary>True when the drag holds the capture of <paramref name="endpoint"/>'s handle.</summary>
    bool IsCaptured(SelectionHandleEndpoint endpoint);
}
