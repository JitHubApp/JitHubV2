using System;
using System.Collections.Generic;
using MarkdownRenderer.Accessibility;
using MarkdownRenderer.Controls.Input;
using MarkdownRenderer.Diagnostics;
using MarkdownRenderer.Hosting;
using MarkdownRenderer.Selection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.UI;

namespace MarkdownRenderer.Controls.Selection;

/// <summary>
/// The two touch selection handle Buttons in the renderer's overlay: their
/// creation, look, placement, accessible names and automation peers.
/// </summary>
/// <remarks>
/// Each Button's Tag is its <see cref="SelectionHandleEndpoint"/>. Pointer
/// events go to the renderer's input path; key presses go to the owner's
/// keyboard handler.
/// </remarks>
internal sealed class SelectionHandleButtons
{
    private readonly ISelectionHandlesHost _host;
    private readonly IPointerInputSink _input;
    private readonly FrameworkElement _renderer;
    private readonly MarkdownViewSurface _surface;
    private readonly KeyEventHandler _keyDownHandler;
    private readonly PointerEventHandler _pressedHandler;
    private readonly PointerEventHandler _movedHandler;
    private readonly PointerEventHandler _releasedHandler;
    private readonly PointerEventHandler _canceledHandler;

    internal SelectionHandleButtons(
        ISelectionHandlesHost host,
        IPointerInputSink input,
        FrameworkElement renderer,
        MarkdownViewSurface surface,
        KeyEventHandler keyDownHandler)
    {
        _host = host;
        _input = input;
        _renderer = renderer;
        _surface = surface;
        _keyDownHandler = keyDownHandler;
        _pressedHandler = (sender, e) => input.DispatchPointerInput(PointerInputKind.HandlePress, sender, e);
        _movedHandler = (sender, e) => input.DispatchPointerInput(PointerInputKind.HandleMove, sender, e);
        _releasedHandler = (sender, e) => input.DispatchPointerInput(PointerInputKind.HandleRelease, sender, e);
        _canceledHandler = (sender, e) => input.DispatchPointerInput(PointerInputKind.HandleCancel, sender, e);
    }

    /// <summary>The handle at the selection's start, once created.</summary>
    internal Button? Start { get; private set; }

    /// <summary>The handle at the selection's end, once created.</summary>
    internal Button? End { get; private set; }

    /// <summary>The handle for <paramref name="endpoint"/>, or null.</summary>
    internal Button? Get(SelectionHandleEndpoint endpoint) => endpoint switch
    {
        SelectionHandleEndpoint.Start => Start,
        SelectionHandleEndpoint.End => End,
        _ => null,
    };

    /// <summary>
    /// Creates both handles on first use and attaches any that a rebuild
    /// detached from the overlay. Returns false when the overlay is missing or
    /// rejects them.
    /// </summary>
    internal bool EnsureAttached()
    {
        if (_surface.Overlay is null)
            return false;
        if (Start is null || End is null)
            Create();

        bool attached = false;
        try
        {
            if (VisualTreeHelper.GetParent(Start) is null)
            {
                _surface.Overlay.Children.Add(Start);
                attached = true;
            }
            if (VisualTreeHelper.GetParent(End) is null)
            {
                _surface.Overlay.Children.Add(End);
                attached = true;
            }
        }
        catch (System.Runtime.InteropServices.COMException ex)
        {
            MarkdownDiagnostics.WriteLine(
                $"[MarkdownRendererControl] selection handle attach failed: {ex.Message}");
            return false;
        }

        if (attached)
        {
            UpdateLocalization(Start!, SelectionHandleEndpoint.Start);
            UpdateLocalization(End!, SelectionHandleEndpoint.End);
        }
        return true;
    }

    /// <summary>Refreshes the existing handles' accessible names, help and tooltips.</summary>
    internal void UpdateLocalization()
    {
        if (Start is not null)
            UpdateLocalization(Start, SelectionHandleEndpoint.Start);
        if (End is not null)
            UpdateLocalization(End, SelectionHandleEndpoint.End);
    }

    /// <summary>Sizes a handle's hit target and colors its dot.</summary>
    internal static void UpdateAppearance(
        Button handle,
        double targetSize,
        double dotSize,
        Color color)
    {
        handle.Width = targetSize;
        handle.Height = targetSize;
        handle.MinWidth = targetSize;
        handle.MinHeight = targetSize;
        handle.CornerRadius = new CornerRadius(targetSize / 2.0);
        if (handle.Content is not Ellipse dot)
            return;

        dot.Width = dotSize;
        dot.Height = dotSize;
        if (dot.Fill is SolidColorBrush brush)
            brush.Color = color;
        else
            dot.Fill = new SolidColorBrush(color);
    }

    /// <summary>
    /// Centers a handle on <paramref name="endpoint"/>, snapped to the window's
    /// physical pixel grid, or collapses it when its target lies outside
    /// <paramref name="viewport"/>. Returns true when the handle is shown.
    /// </summary>
    internal bool Place(Button handle, Point endpoint, double targetSize, Rect viewport)
    {
        double halfTarget = targetSize / 2.0;
        bool visible = endpoint.X + halfTarget >= viewport.Left &&
                       endpoint.X - halfTarget <= viewport.Right &&
                       endpoint.Y + halfTarget >= viewport.Top &&
                       endpoint.Y - halfTarget <= viewport.Bottom;
        if (!visible)
        {
            handle.Visibility = Visibility.Collapsed;
            return false;
        }

        // Snap to the physical pixel grid of the window root so a 44-DIP target
        // measures exactly 44 DIP when the renderer starts on a fractional
        // physical pixel.
        (double left, double top) = SelectionController.ComputeSelectionHandleOrigin(
            endpoint,
            targetSize,
            viewport,
            GetOverlayOriginInRoot(),
            _renderer.XamlRoot?.RasterizationScale ?? 1.0);
        Canvas.SetLeft(handle, left);
        Canvas.SetTop(handle, top);
        handle.Visibility = Visibility.Visible;
        return true;
    }

    /// <summary>Sets both handles' visibility; the end handle is set even if the start throws.</summary>
    internal void SetVisibility(Visibility visibility)
    {
        try
        {
            if (Start is not null)
                Start.Visibility = visibility;
        }
        finally
        {
            if (End is not null)
                End.Visibility = visibility;
        }
    }

    /// <summary>
    /// Shows or hides both handles' dots. A dragged handle hides its dot and
    /// keeps its hit target, since the adorner paints the moving endpoint.
    /// </summary>
    internal void SetGlyphVisibility(bool visible)
    {
        double opacity = visible ? 1 : 0;
        if (Start?.Content is Ellipse startGlyph)
            startGlyph.Opacity = opacity;
        if (End?.Content is Ellipse endGlyph)
            endGlyph.Opacity = opacity;
    }

    /// <summary>Releases any pointer capture either handle holds, ignoring a closed input site.</summary>
    internal void ReleasePointerCaptures()
    {
        try { Start?.ReleasePointerCaptures(); } catch { }
        try { End?.ReleasePointerCaptures(); } catch { }
    }

    /// <summary>True when <paramref name="element"/> is inside either handle.</summary>
    internal bool Contains(DependencyObject element)
    {
        return (Start is not null && VisualTreeAncestry.IsWithin(element, Start)) ||
               (End is not null && VisualTreeAncestry.IsWithin(element, End));
    }

    /// <summary>Adds the automation peers of the visible handles to <paramref name="peers"/>.</summary>
    internal void AppendVisibleAutomationPeers(IList<AutomationPeer> peers)
    {
        AppendVisibleAutomationPeer(Start, peers);
        AppendVisibleAutomationPeer(End, peers);
    }

    /// <summary>Detaches every input handler from both handles.</summary>
    internal void DetachHandlers()
    {
        DetachHandlers(Start);
        DetachHandlers(End);
    }

    /// <summary>
    /// Detaches the handles and forgets them. The overlay's children were
    /// already cleared; they are re-created on the next selection.
    /// </summary>
    internal void Release()
    {
        DetachHandlers();
        Start = null;
        End = null;
    }

    private void Create()
    {
        if (_surface.Overlay is null)
            return;

        Start = Create(SelectionHandleEndpoint.Start);
        End = Create(SelectionHandleEndpoint.End);
        _surface.Overlay.Children.Add(Start);
        _surface.Overlay.Children.Add(End);
    }

    private Button Create(SelectionHandleEndpoint endpoint)
    {
        var dot = new Ellipse
        {
            IsHitTestVisible = false,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var handle = new Button
        {
            Tag = endpoint,
            Content = dot,
            Padding = new Thickness(0),
            BorderThickness = new Thickness(0),
            Background = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0)),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            // Handles own their drag. Without opting out of the inherited
            // ScrollViewer manipulation contract, DirectManipulation can cancel
            // the Button's pointer stream as soon as the contact starts moving.
            ManipulationMode = ManipulationModes.None,
            IsTabStop = true,
            UseSystemFocusVisuals = true,
            Visibility = Visibility.Collapsed,
        };

        string automationId = endpoint == SelectionHandleEndpoint.Start
            ? "MarkdownSelectionStartHandle"
            : "MarkdownSelectionEndHandle";
        AutomationProperties.SetAutomationId(handle, automationId);
        UpdateLocalization(handle, endpoint);
        handle.AddHandler(
            UIElement.PointerPressedEvent,
            _pressedHandler,
            handledEventsToo: true);
        handle.AddHandler(
            UIElement.PointerMovedEvent,
            _movedHandler,
            handledEventsToo: true);
        handle.AddHandler(
            UIElement.PointerReleasedEvent,
            _releasedHandler,
            handledEventsToo: true);
        handle.AddHandler(
            UIElement.PointerCanceledEvent,
            _canceledHandler,
            handledEventsToo: true);
        handle.AddHandler(
            UIElement.PointerCaptureLostEvent,
            _canceledHandler,
            handledEventsToo: true);
        handle.RightTapped += OnRightTapped;
        handle.KeyDown += _keyDownHandler;
        Canvas.SetZIndex(handle, 5);
        return handle;
    }

    private void UpdateLocalization(
        Button handle,
        SelectionHandleEndpoint endpoint)
    {
        string name = endpoint == SelectionHandleEndpoint.Start
            ? _host.ResolveLocalizedString(
                MarkdownStringKeys.SelectionStartHandle,
                MarkdownLocalizedStrings.SelectionStartHandle)
            : _host.ResolveLocalizedString(
                MarkdownStringKeys.SelectionEndHandle,
                MarkdownLocalizedStrings.SelectionEndHandle);
        string help = _host.ResolveLocalizedString(
            MarkdownStringKeys.SelectionHandleHelp,
            MarkdownLocalizedStrings.SelectionHandleHelp);
        AutomationProperties.SetName(handle, name);
        AutomationProperties.SetHelpText(handle, help);
        ToolTipService.SetToolTip(handle, name);
    }

    private void OnRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        // Handles live in the overlay, a sibling of the canvas, so their
        // right-taps never reach the canvas's own RightTapped handler.
        if (_surface.Canvas is null ||
            !MarkdownPointerGesturePolicy.ForwardsSelectionHandleRightTap(PointerInputClassification.GetPointerModality(e.PointerDeviceType)))
        {
            return;
        }

        e.Handled |= _input.DispatchRecognizedGesture(PointerInputKind.RightTap, e.GetPosition(_surface.Canvas), e.PointerDeviceType);
    }

    private Point GetOverlayOriginInRoot()
    {
        if (_surface.Overlay is null || _renderer.XamlRoot is null)
            return new Point(double.NaN, double.NaN);
        try
        {
            return _surface.Overlay.TransformToVisual(null).TransformPoint(new Point(0, 0));
        }
        catch (ArgumentException)
        {
            // Not yet in the live tree: keep the unsnapped placement.
            return new Point(double.NaN, double.NaN);
        }
    }

    private static void AppendVisibleAutomationPeer(
        Button? handle,
        IList<AutomationPeer> peers)
    {
        if (handle is null || handle.Visibility != Visibility.Visible)
            return;

        AutomationPeer? peer = FrameworkElementAutomationPeer.FromElement(handle) ??
            FrameworkElementAutomationPeer.CreatePeerForElement(handle);
        if (peer is not null)
            peers.Add(peer);
    }

    private void DetachHandlers(Button? handle)
    {
        if (handle is null)
            return;
        handle.RemoveHandler(
            UIElement.PointerPressedEvent,
            _pressedHandler);
        handle.RemoveHandler(
            UIElement.PointerMovedEvent,
            _movedHandler);
        handle.RemoveHandler(
            UIElement.PointerReleasedEvent,
            _releasedHandler);
        handle.RemoveHandler(
            UIElement.PointerCanceledEvent,
            _canceledHandler);
        handle.RemoveHandler(
            UIElement.PointerCaptureLostEvent,
            _canceledHandler);
        handle.RightTapped -= OnRightTapped;
        handle.KeyDown -= _keyDownHandler;
    }
}
