using MarkdownRenderer.Controls.Internal.Input;
using MarkdownRenderer.Controls.Internal.View;
using MarkdownRenderer.Diagnostics.Internal;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace MarkdownRenderer.Controls.Internal.Selection;

/// <summary>
/// A transparent, viewport-sized element above hosted embeds while a text
/// selection drag is captured, so the drag's moves and release reach the
/// renderer instead of the embed under the pointer.
/// </summary>
internal sealed class SelectionDragShield
{
    private readonly MarkdownViewSurface _surface;
    private readonly MarkdownViewport _viewport;
    private readonly PointerEventHandler _movedHandler;
    private readonly PointerEventHandler _releasedHandler;
    private readonly PointerEventHandler _canceledHandler;
    private Border? _shield;

    internal SelectionDragShield(
        MarkdownViewSurface surface,
        MarkdownViewport viewport,
        IPointerInputSink input)
    {
        _surface = surface;
        _viewport = viewport;
        _movedHandler = (sender, e) => input.DispatchPointerInput(PointerInputKind.Move, sender, e);
        _releasedHandler = (sender, e) => input.DispatchPointerInput(PointerInputKind.Release, sender, e);
        _canceledHandler = (sender, e) => input.DispatchPointerInput(PointerInputKind.Cancel, sender, e);
    }

    /// <summary>
    /// Shows the shield over the visible band, creating and attaching it on
    /// first use, or collapses it.
    /// </summary>
    internal void SetActive(bool active)
    {
        if (_surface.Overlay is null)
            return;

        if (!active)
        {
            if (_shield is not null)
                _shield.Visibility = Visibility.Collapsed;
            return;
        }

        if (!EnsureAttached())
            return;

        _shield!.Width = GetOverlayWidth();
        _shield.Height = GetOverlayHeight();
        _shield.Visibility = Visibility.Visible;
    }

    /// <summary>Detaches the shield's input handlers and drops it.</summary>
    internal void Release()
    {
        if (_shield is null)
            return;

        _shield.PointerMoved -= _movedHandler;
        _shield.PointerReleased -= _releasedHandler;
        _shield.PointerCanceled -= _canceledHandler;
        _shield.PointerCaptureLost -= _canceledHandler;
        _shield = null;
    }

    private Border Create()
    {
        var shield = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0)),
            IsHitTestVisible = true,
            Visibility = Visibility.Collapsed,
        };
        shield.PointerMoved += _movedHandler;
        shield.PointerReleased += _releasedHandler;
        shield.PointerCanceled += _canceledHandler;
        shield.PointerCaptureLost += _canceledHandler;
        Canvas.SetLeft(shield, 0);
        Canvas.SetTop(shield, 0);
        Canvas.SetZIndex(shield, 4);
        _shield = shield;
        return shield;
    }

    private bool EnsureAttached()
    {
        if (_surface.Overlay is null)
            return false;

        if (_shield is null)
            Create();

        try
        {
            if (VisualTreeHelper.GetParent(_shield) is null)
                _surface.Overlay.Children.Add(_shield);
        }
        catch (System.Runtime.InteropServices.COMException ex)
        {
            MarkdownDiagnostics.WriteLine($"[MarkdownRendererControl] selection drag shield attach failed: {ex.Message}");
            return false;
        }

        return true;
    }

    private double GetOverlayWidth()
    {
        _viewport.TryGet(out _, out _, out double width);
        return double.IsFinite(width) && width > 0 ? width : 1.0;
    }

    private double GetOverlayHeight()
    {
        _viewport.TryGet(out _, out double height, out _);
        return double.IsFinite(height) && height > 0 ? height : 1.0;
    }
}
