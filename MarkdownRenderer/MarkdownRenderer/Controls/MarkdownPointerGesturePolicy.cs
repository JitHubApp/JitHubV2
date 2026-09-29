namespace MarkdownRenderer.Controls;

/// <summary>
/// Renderer-owned pointer policy kept separate from routed-event plumbing so
/// modality and gesture-arbitration behavior can be verified from the built
/// WinUI assembly without synthesizing private routed-event arguments.
/// </summary>
internal static class MarkdownPointerGesturePolicy
{
    internal const double PanThreshold = 8.0;

    internal static bool BeginsImmediateSelection(MarkdownPointerModality modality)
        => modality is MarkdownPointerModality.Mouse or MarkdownPointerModality.Pen;

    internal static bool IsPrimarySelectionPress(
        MarkdownPointerModality modality,
        bool isInContact,
        bool isLeftButtonPressed,
        bool isBarrelButtonPressed)
        => modality switch
        {
            MarkdownPointerModality.Mouse => isLeftButtonPressed,
            MarkdownPointerModality.Pen => isInContact && !isBarrelButtonPressed,
            _ => false,
        };

    internal static MarkdownPanGestureDecision ClassifyHorizontalPan(double deltaX, double deltaY)
    {
        if (System.Math.Abs(deltaX) < PanThreshold &&
            System.Math.Abs(deltaY) < PanThreshold)
        {
            return MarkdownPanGestureDecision.Pending;
        }

        return System.Math.Abs(deltaX) > System.Math.Abs(deltaY) * 1.15
            ? MarkdownPanGestureDecision.CaptureHorizontal
            : MarkdownPanGestureDecision.YieldToAncestor;
    }
}

internal enum MarkdownPointerModality
{
    Unknown,
    Mouse,
    Touch,
    Pen,
}

internal enum MarkdownPanGestureDecision
{
    Pending,
    YieldToAncestor,
    CaptureHorizontal,
}
