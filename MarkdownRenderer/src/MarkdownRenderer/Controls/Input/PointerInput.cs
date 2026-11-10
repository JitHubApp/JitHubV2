using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;
using VirtualKeyModifiers = Windows.System.VirtualKeyModifiers;
using MarkdownRenderer.Layout.Internal.Snapshots;

namespace MarkdownRenderer.Controls.Input;

// Capture point/modifiers/time at event delivery. Never retain routed event
// arguments or re-query a moved pointer when deferred work is replayed.
internal struct PointerInput
{
    internal PointerInputKind Kind;
    internal object Sender;
    internal Pointer Pointer;
    internal PointerPoint Point;
    internal VirtualKeyModifiers Modifiers;
    internal long TickCount;
    internal long TouchTapGestureEpoch;
    internal uint DeferredTouchGesturePointerId;
    internal bool IgnoreUnattributableTouchGesture;
    internal bool CapturedBeforeDeferral;
    internal bool Handled;
    internal Windows.Foundation.Point GesturePosition;
    internal PointerDeviceType GestureDevice;
    internal long GestureLayoutRevision;
    internal bool ShowContextMenuAfterInteraction;
    internal bool TouchSelectionContextMenu;
    internal LayoutSnapshot? InteractionSnapshot;
    internal UIElement? InteractionCanvas;
    internal MarkdownInputModifiers LinkModifiers;
    internal bool ClearSelectionAfterInteraction;
    internal bool ReleaseCanvasCaptureAfterInteraction;
    internal bool ResumeRebuildAfterInteraction;
    internal bool RefreshSelectionAdornerAfterInteraction;
    internal bool WaitForMeasuredHitTest;
    internal bool WaitForLayoutPublication;
    internal bool IsDeferredGeometryWait;
    internal bool InvalidDeferredProjection;
    internal bool ContextCaptureEnded;
    internal Point ViewportGesturePosition;
    internal bool HasViewportGesturePosition;
    internal TouchContactGeometry ViewportGestureContact;
    internal bool HasViewportGestureContact;
    internal TouchContactGeometry GestureContact;
    internal bool HasGestureContact;
    internal long GestureSnapshotGeneration;
    internal uint ReleasePointerIdAfterInteraction;
    private LinkActivationIntent? _linkActivation;

    internal bool HasPostInteractionWork =>
        ClearSelectionAfterInteraction ||
        ReleaseCanvasCaptureAfterInteraction ||
        ResumeRebuildAfterInteraction ||
        RefreshSelectionAdornerAfterInteraction ||
        _linkActivation is not null;

    internal void SetLinkActivation(LinkActivationIntent intent) => _linkActivation = intent;

    internal bool TryGetLinkActivation(out LinkActivationIntent intent)
    {
        if (_linkActivation is { } activation)
        {
            intent = activation;
            return true;
        }

        intent = default;
        return false;
    }
}
