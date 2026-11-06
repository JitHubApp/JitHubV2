using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;

namespace MarkdownRenderer.Controls.Input;

/// <summary>
/// Subscribes the document canvas's pointer and gesture events to
/// <see cref="PointerInputRouter"/>. Move, release, cancel and capture-lost
/// are received even when a child already handled them, so a drag that
/// leaves its start element still ends.
/// </summary>
internal sealed class CanvasPointerEvents
{
    private readonly MarkdownViewSurface _surface;
    private readonly PointerInputRouter _router;
    private readonly PointerEventHandler _pressed;
    private readonly PointerEventHandler _wheelChanged;
    private readonly PointerEventHandler _exited;
    private readonly PointerEventHandler _moved;
    private readonly PointerEventHandler _released;
    private readonly PointerEventHandler _canceled;
    private readonly PointerEventHandler _captureLost;
    private readonly TappedEventHandler _tapped;
    private readonly DoubleTappedEventHandler _doubleTapped;
    private readonly HoldingEventHandler _holding;
    private readonly RightTappedEventHandler _rightTapped;

    internal CanvasPointerEvents(MarkdownViewSurface surface, PointerInputRouter router)
    {
        _surface = surface;
        _router = router;
        _pressed = (sender, e) => router.DispatchPointerInput(PointerInputKind.Press, sender, e);
        _wheelChanged = (sender, e) => router.DispatchPointerInput(PointerInputKind.Wheel, sender, e);
        _exited = (sender, e) => router.DispatchPointerInput(PointerInputKind.Exit, sender, e);
        _moved = (sender, e) => router.DispatchPointerInput(PointerInputKind.Move, sender, e);
        _released = (sender, e) => router.DispatchPointerInput(PointerInputKind.Release, sender, e);
        _canceled = (sender, e) => router.DispatchPointerInput(PointerInputKind.Cancel, sender, e);
        _captureLost = (sender, e) => router.DispatchPointerInput(PointerInputKind.Cancel, sender, e);
        _tapped = OnTapped;
        _doubleTapped = OnDoubleTapped;
        _holding = OnHolding;
        _rightTapped = OnRightTapped;
    }

    /// <summary>Subscribes to <paramref name="canvas"/>'s pointer and gesture events.</summary>
    internal void Attach(UIElement canvas)
    {
        canvas.PointerPressed += _pressed;
        canvas.PointerWheelChanged += _wheelChanged;
        canvas.PointerExited += _exited;
        canvas.Tapped += _tapped;
        canvas.DoubleTapped += _doubleTapped;
        canvas.Holding += _holding;
        canvas.RightTapped += _rightTapped;
        canvas.AddHandler(UIElement.PointerMovedEvent, _moved, handledEventsToo: true);
        canvas.AddHandler(UIElement.PointerReleasedEvent, _released, handledEventsToo: true);
        canvas.AddHandler(UIElement.PointerCanceledEvent, _canceled, handledEventsToo: true);
        canvas.AddHandler(UIElement.PointerCaptureLostEvent, _captureLost, handledEventsToo: true);
    }

    /// <summary>Removes every subscription <see cref="Attach"/> made.</summary>
    internal void Detach(UIElement canvas)
    {
        canvas.PointerPressed -= _pressed;
        canvas.PointerWheelChanged -= _wheelChanged;
        canvas.PointerExited -= _exited;
        canvas.Tapped -= _tapped;
        canvas.DoubleTapped -= _doubleTapped;
        canvas.Holding -= _holding;
        canvas.RightTapped -= _rightTapped;
        canvas.RemoveHandler(UIElement.PointerMovedEvent, _moved);
        canvas.RemoveHandler(UIElement.PointerReleasedEvent, _released);
        canvas.RemoveHandler(UIElement.PointerCanceledEvent, _canceled);
        canvas.RemoveHandler(UIElement.PointerCaptureLostEvent, _captureLost);
    }

    private void OnTapped(object sender, TappedRoutedEventArgs e)
    {
        if (_surface.Canvas is not null && e.PointerDeviceType == PointerDeviceType.Touch)
            e.Handled |= _router.DispatchRecognizedGesture(PointerInputKind.Tap, e.GetPosition(_surface.Canvas), e.PointerDeviceType);
    }

    private void OnDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (_surface.Canvas is not null && e.PointerDeviceType == PointerDeviceType.Touch)
            e.Handled |= _router.DispatchRecognizedGesture(PointerInputKind.DoubleTap, e.GetPosition(_surface.Canvas), e.PointerDeviceType);
    }

    private void OnHolding(object sender, HoldingRoutedEventArgs e)
    {
        if (_surface.Canvas is not null && e.HoldingState == HoldingState.Started &&
            e.PointerDeviceType is PointerDeviceType.Touch or PointerDeviceType.Pen)
            e.Handled |= _router.DispatchRecognizedGesture(PointerInputKind.Hold, e.GetPosition(_surface.Canvas), e.PointerDeviceType);
    }

    private void OnRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (_surface.Canvas is not null)
            e.Handled |= _router.DispatchRecognizedGesture(PointerInputKind.RightTap, e.GetPosition(_surface.Canvas), e.PointerDeviceType);
    }
}
