using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;

namespace MarkdownRenderer.Controls.Input;

/// <summary>
/// Classifies pointer devices and cancellations, and captures pointers for
/// input sessions.
/// </summary>
internal static class PointerInputClassification
{
    internal static bool IsPrecisePointer(PointerInput e)
        => MarkdownPointerGesturePolicy.BeginsImmediateSelection(
            GetPointerModality(e.Pointer.PointerDeviceType));

    internal static MarkdownPointerModality GetPointerModality(
        Microsoft.UI.Input.PointerDeviceType deviceType)
        => deviceType switch
        {
            Microsoft.UI.Input.PointerDeviceType.Mouse => MarkdownPointerModality.Mouse,
            Microsoft.UI.Input.PointerDeviceType.Touch => MarkdownPointerModality.Touch,
            Microsoft.UI.Input.PointerDeviceType.Pen => MarkdownPointerModality.Pen,
            _ => MarkdownPointerModality.Unknown,
        };

    internal static PointerInputKind NormalizeCanceledPointerInputKind(PointerInputKind kind, bool isCanceled)
    {
        if (!isCanceled)
            return kind;

        // Device cancellation can accompany a routed move or release packet.
        // Classify it once before either live dispatch or deferred queuing so
        // it cannot commit a tap, link activation, or final handle movement.
        return kind switch
        {
            PointerInputKind.Press or PointerInputKind.Move or PointerInputKind.Release or
                PointerInputKind.Exit or PointerInputKind.Wheel => PointerInputKind.Cancel,
            PointerInputKind.HandlePress or PointerInputKind.HandleMove or
                PointerInputKind.HandleRelease => PointerInputKind.HandleCancel,
            _ => kind,
        };
    }

    internal static bool CapturePointerForInput(UIElement target, PointerInput input)
    {
        // Pointer input can be replayed after a layout/image realization pass.
        // Do not ask WinUI for capture state once that target has left its tree.
        if (target.XamlRoot is null)
            return false;

        if (input.CapturedBeforeDeferral)
            return true;

        // ButtonBase can capture a touch contact in its class handler before our
        // handled-events-too selection-handle handler runs. Re-capturing the same
        // pointer may report false even though the intended target already owns it;
        // treat that existing capture as success so native Button behavior and the
        // renderer's drag state can cooperate.
        // Although the projected API is non-nullable, WinUI can surface a null
        // capture view while an element's input site is being initialized or
        // torn down. Treat it as the normal empty-capture state.
        IReadOnlyList<Pointer>? captures = target.PointerCaptures;
        if (ContainsPointerCapture(captures, input.Pointer.PointerId))
            return true;

        return target.CapturePointer(input.Pointer);
    }

    internal static bool ContainsPointerCapture(IReadOnlyList<Pointer>? captures, uint pointerId)
    {
        if (captures is null)
            return false;

        foreach (Pointer captured in captures)
        {
            if (captured.PointerId == pointerId)
                return true;
        }

        return false;
    }
}
