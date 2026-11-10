using System;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using MarkdownRenderer.Diagnostics.Internal;
using MarkdownRenderer.Layout.Internal.Focus;

namespace MarkdownRenderer.Controls.Focus;

/// <summary>
/// Draws the focus ring around a painted link, vector semantic or horizontal
/// overflow region. Each placement is published on the dispatcher behind a
/// ticket, so only the latest request moves the ring, and only while its
/// item is still the focused one.
/// </summary>
internal sealed class FocusRingPresenter
{
    private const double Padding = 2.0;

    private readonly IFocusRingHost _host;
    private readonly KeyboardFocusModel _focus;
    private readonly FocusGeometryReader _geometry;
    private readonly MarkdownViewSurface _surface;
    private readonly MarkdownViewLifetime _lifetime;
    private readonly PublishedLayout _published;
    private readonly DispatcherQueue? _dispatcher;
    private Border? _ring;

    // Cached so Tab traversal does not allocate a brush per keystroke.
    private Windows.UI.Color _brushColor;
    private long _publicationTicket;

    internal FocusRingPresenter(
        IFocusRingHost host,
        KeyboardFocusModel focus,
        FocusGeometryReader geometry,
        MarkdownViewSurface surface,
        MarkdownViewLifetime lifetime,
        PublishedLayout published,
        DispatcherQueue? dispatcher)
    {
        _host = host;
        _focus = focus;
        _geometry = geometry;
        _surface = surface;
        _lifetime = lifetime;
        _published = published;
        _dispatcher = dispatcher;
    }

    /// <summary>
    /// Queues the ring to surround <paramref name="rect"/> for
    /// <paramref name="item"/>, or to hide when either is null.
    /// </summary>
    internal void Publish(long snapshotGeneration, FocusableItem? item, Rect? rect)
    {
        long ticket = ++_publicationTicket;
        bool queued = false;
        try
        {
            queued = _dispatcher is { } dispatcher &&
                dispatcher.TryEnqueue(() => Apply(ticket, snapshotGeneration, item, rect));
        }
        catch (Exception)
        {
        }
        if (!queued)
        {
            _host.RequestFocusGeometryRetry();
        }
    }

    /// <summary>Queues the ring to hide.</summary>
    internal void Hide() => Publish(_published.Generation, item: null, rect: null);

    /// <summary>Hides the ring now, without a publication.</summary>
    internal void Collapse()
    {
        if (_ring is not null)
            _ring.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// Moves the ring to surround the focused painted item, or hides it when
    /// nothing painted is focused. Asks for a geometry retry when the item's
    /// rectangle cannot be read yet.
    /// </summary>
    internal void Update()
    {
        if (_focus.PendingLazy is not null)
        {
            Hide();
            return;
        }

        var items = _focus.Items;
        if (items is null || _focus.FocusedIndex < 0 || _focus.FocusedIndex >= items.Count)
        {
            Hide();
            return;
        }

        var item = items[_focus.FocusedIndex];
        if (!item.IsLink && !item.IsVectorSemantic && !item.IsHorizontalOverflow)
        {
            Hide();
            return;
        }

        if (_published.Snapshot is not { } snapshot)
        {
            Hide();
            return;
        }

        long generation = _published.Generation;
        FocusGeometryReadStatus status = _geometry.TryCaptureItemRect(
            snapshot, generation, snapshot.LayoutRevision, item, out Rect? rect);
        if (status != FocusGeometryReadStatus.Ready)
        {
            _host.RequestFocusGeometryRetry();
            return;
        }

        if (rect is not { Width: > 0, Height: > 0 })
        {
            Hide();
            return;
        }

        Publish(generation, item, rect);
    }

    /// <summary>Makes queued publications stale.</summary>
    internal void InvalidatePublication() => ++_publicationTicket;

    /// <summary>
    /// Forgets the ring after the overlay was cleared. The next publication
    /// creates a new one.
    /// </summary>
    internal void ForgetEvictedRing() => _ring = null;

    private void Apply(
        long ticket,
        long snapshotGeneration,
        FocusableItem? item,
        Rect? rect)
    {
        if (ticket != _publicationTicket ||
            _lifetime.IsDisposed || _lifetime.IsUnloaded ||
            snapshotGeneration != _published.Generation)
        {
            return;
        }

        if (item is not { } expected ||
            rect is not { Width: > 0, Height: > 0 } r ||
            _focus.Current is not { } current ||
            !FocusableItemQueries.SameFocusableIdentity(current, expected))
        {
            if (_ring is not null)
                _ring.Visibility = Visibility.Collapsed;
            _host.InvalidateInteractiveTextAdorner();
            return;
        }

        if (!EnsureRing())
            return;
        var ring = _ring!;

        var focusVisual = _host.FocusVisualColor
                          ?? Windows.UI.Color.FromArgb(0xFF, 0x00, 0x78, 0xD4);
        if (ring.BorderBrush is null || focusVisual != _brushColor)
        {
            _brushColor = focusVisual;
            ring.BorderBrush = new SolidColorBrush(focusVisual);
        }

        Canvas.SetLeft(ring, r.X - Padding);
        Canvas.SetTop(ring, r.Y - Padding);
        ring.Width = r.Width + Padding * 2;
        ring.Height = r.Height + Padding * 2;
        ring.Visibility = Visibility.Visible;
        _host.InvalidateInteractiveTextAdorner();
    }

    private bool EnsureRing()
    {
        if (_ring is not null)
            return true;

        if (_surface.Overlay is null || _lifetime.IsUnloaded || _host.XamlRoot is null)
            return false;

        var ring = new Border
        {
            BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(3),
            IsHitTestVisible = false,
        };
        // Above the selection layer (-1) on the overlay.
        Canvas.SetZIndex(ring, 1);

        try
        {
            _surface.Overlay.Children.Add(ring);
        }
        catch (System.Runtime.InteropServices.COMException ex)
        {
            MarkdownDiagnostics.WriteLine($"[MarkdownRendererControl] focus ring attach failed: {ex.Message}");
            return false;
        }

        _ring = ring;
        return true;
    }
}
