namespace MarkdownRenderer.Controls.Internal.Input.Gestures;

internal enum MarkdownPanGestureDecision
{
    Pending,
    YieldToAncestor,
    CaptureHorizontal,
}
