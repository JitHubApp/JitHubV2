using MarkdownRenderer.Hosting;
using MarkdownRenderer.Selection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;
using Windows.System;

namespace MarkdownRenderer.Controls.Selection;

/// <summary>
/// Watches the window's root content while the renderer is loaded. A press
/// outside the document text clears the selection, and Ctrl+C anywhere in
/// the window copies it.
/// </summary>
/// <remarks>
/// Both handlers see handled events too, so a control that consumes the
/// press or key still dismisses or copies the selection.
/// </remarks>
internal sealed class SelectionDismissalHook
{
    private readonly ISelectionDismissalHost _host;
    private readonly MarkdownRendererControl _renderer;
    private readonly MarkdownViewSurface _surface;
    private readonly MarkdownViewLifetime _lifetime;
    private readonly PublishedLayout _published;
    private readonly SelectionController _selection;
    private readonly SelectionCommands _commands;

    private UIElement? _root;
    private PointerEventHandler? _pointerPressedHandler;
    private KeyEventHandler? _keyDownHandler;

    internal SelectionDismissalHook(
        ISelectionDismissalHost host,
        MarkdownRendererControl renderer,
        MarkdownViewSurface surface,
        MarkdownViewLifetime lifetime,
        PublishedLayout published,
        SelectionController selection,
        SelectionCommands commands)
    {
        _host = host;
        _renderer = renderer;
        _surface = surface;
        _lifetime = lifetime;
        _published = published;
        _selection = selection;
        _commands = commands;
    }

    /// <summary>Attaches to the current window's root content, once.</summary>
    internal void Register()
    {
        if (_root is not null)
            return;

        var root = _renderer.XamlRoot?.Content as UIElement;
        if (root is null)
            return;

        _pointerPressedHandler = OnRootPointerPressed;
        root.AddHandler(UIElement.PointerPressedEvent, _pointerPressedHandler, handledEventsToo: true);
        _keyDownHandler = OnRootKeyDown;
        root.AddHandler(UIElement.KeyDownEvent, _keyDownHandler, handledEventsToo: true);
        _root = root;
    }

    /// <summary>Detaches from the root content. A detached root may reject removal.</summary>
    internal void Unregister()
    {
        if (_root is not null && _pointerPressedHandler is not null)
        {
            try { _root.RemoveHandler(UIElement.PointerPressedEvent, _pointerPressedHandler); }
            catch { }
        }

        if (_root is not null && _keyDownHandler is not null)
        {
            try { _root.RemoveHandler(UIElement.KeyDownEvent, _keyDownHandler); }
            catch { }
        }

        _root = null;
        _pointerPressedHandler = null;
        _keyDownHandler = null;
    }

    private void OnRootPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_lifetime.IsUnloaded)
            return;

        var point = e.GetCurrentPoint(sender as UIElement ?? _renderer);
        var properties = point.Properties;
        if (properties.IsRightButtonPressed || properties.IsMiddleButtonPressed || properties.IsXButton1Pressed || properties.IsXButton2Pressed)
            return;

        if (e.OriginalSource is DependencyObject source && VisualTreeAncestry.IsWithin(source, _renderer))
        {
            MarkdownSelectionCoordinator.ClearSelectionsExcept(_renderer);
            if (_host.IsElementWithinCodeBlockAction(source))
                return;
            if (_host.IsElementWithinSelectionHandle(source))
                return;

            bool overCanvas = IsPointerOverCanvasTextSurface(e, out var canvasPoint);
            bool overEmbed = overCanvas && _host.IsPointOverEmbed(canvasPoint);
            if (!overCanvas || overEmbed)
                _host.ClearSelectionForExternalInteraction();
            return;
        }

        _host.ClearSelectionForExternalInteraction();
    }

    private void OnRootKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_lifetime.IsUnloaded || _published.Snapshot is null || !_selection.IsActive)
            return;

        bool ctrl = (Microsoft.UI.Input.InputKeyboardSource
            .GetKeyStateForCurrentThread(VirtualKey.Control) & Windows.UI.Core.CoreVirtualKeyStates.Down)
            == Windows.UI.Core.CoreVirtualKeyStates.Down;
        if (!ctrl || e.Key != VirtualKey.C)
            return;

        if (_commands.Execute(MarkdownCommandKind.CopyRendered))
            e.Handled = true;
    }

    private bool IsPointerOverCanvasTextSurface(PointerRoutedEventArgs e, out Point canvasPoint)
    {
        canvasPoint = default;
        if (_surface.Canvas is null)
            return false;

        try
        {
            canvasPoint = e.GetCurrentPoint(_surface.Canvas).Position;
            return canvasPoint.X >= 0 &&
                   canvasPoint.Y >= 0 &&
                   canvasPoint.X <= _surface.Canvas.ActualWidth &&
                   canvasPoint.Y <= _surface.Canvas.ActualHeight;
        }
        catch
        {
            canvasPoint = default;
            return false;
        }
    }
}
