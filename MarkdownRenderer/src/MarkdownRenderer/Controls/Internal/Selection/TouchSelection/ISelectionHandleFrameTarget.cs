namespace MarkdownRenderer.Controls.Internal.Selection.TouchSelection;

/// <summary>
/// The handle drag that <see cref="SelectionHandleFrameClock"/> advances
/// once per compositor frame.
/// </summary>
internal interface ISelectionHandleFrameTarget : ISelectionHandleDragView
{
    /// <summary>
    /// Retires the drag when its pointer capture was lost without a routed
    /// terminal. Returns true when it was retired.
    /// </summary>
    bool RetireSilentlyLostCapture();

    /// <summary>
    /// Applies the latest contact, or keeps edge auto-scroll going while the
    /// contact is stationary.
    /// </summary>
    /// <param name="sustained">True on a frame tick rather than a pointer move.</param>
    /// <param name="elapsedSeconds">Time since the previous frame tick.</param>
    /// <returns>True while movement or edge scrolling continues.</returns>
    bool ApplyPendingMove(bool sustained, double elapsedSeconds);
}
