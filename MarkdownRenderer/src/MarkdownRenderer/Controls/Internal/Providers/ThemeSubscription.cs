using System;
using MarkdownRenderer.Controls.Internal.Pipeline.Rebuild;
using MarkdownRenderer.Controls.Internal.View;
using MarkdownRenderer.Theming;
using Microsoft.UI.Xaml;
using RebuildReason = MarkdownRenderer.Controls.Internal.Pipeline.Rebuild.MarkdownRebuildCoordinatorReason;

namespace MarkdownRenderer.Controls.Internal.Providers;

/// <summary>
/// Restyles the document when its theme changes: a new Theme object, a
/// change of the control's actual theme, or a revision of the theme itself.
/// </summary>
internal sealed class ThemeSubscription
{
    private readonly MarkdownRendererControl _control;
    private readonly MarkdownViewLifetime _lifetime;
    private readonly RebuildScheduler _rebuild;

    internal ThemeSubscription(MarkdownRendererControl control, MarkdownViewLifetime lifetime, RebuildScheduler rebuild)
    {
        _control = control;
        _lifetime = lifetime;
        _rebuild = rebuild;
    }

    /// <summary>The Theme property changed.</summary>
    internal void OnThemePropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        // Unsubscribe from the previous theme's Changed event so we don't leak
        // a reference to it (DependencyObjects don't auto-unsubscribe).
        if (e.OldValue is MarkdownTheme old)
        {
            old.Changed -= OnThemeRevisionChanged;
        }
        if (e.NewValue is MarkdownTheme @new)
        {
            @new.Changed += OnThemeRevisionChanged;
        }
        OnThemeChanged();
    }

    /// <summary>The control's actual theme changed, or a new Theme was assigned.</summary>
    internal void OnThemeChanged()
    {
        if (_lifetime.IsDisposed || _lifetime.IsUnloaded)
            return;

        // NotifyEnvironmentChanged fires Theme.Changed → OnThemeRevisionChanged → a restyle.
        // Do NOT call RequestRebuild() here again — that would start two simultaneous
        // builds and immediately cancel the first one on every theme change.
        if (_control.Theme is { } t) t.NotifyEnvironmentChanged();
        else _rebuild.Request(RebuildReason.Restyle); // no Theme object: must trigger rebuild directly
    }

    /// <summary>
    /// Re-subscribes to Theme.Changed when the view loads: unload unhooks the
    /// handler, and a Load→Unload→Load cycle (TabView reuse, navigation
    /// hide/show) does not necessarily reassign the Theme property, so without
    /// this the control would silently stop reacting to theme.Invalidate()
    /// after re-attach. Unsubscribes first to avoid a duplicate handler if
    /// Loaded fires before any Unload has occurred.
    /// </summary>
    internal void Attach()
    {
        if (_control.Theme is { } t)
        {
            t.Changed -= OnThemeRevisionChanged;
            t.Changed += OnThemeRevisionChanged;
        }
    }

    internal void Detach()
    {
        if (_control.Theme is { } t)
        {
            t.Changed -= OnThemeRevisionChanged;
        }
    }

    private void OnThemeRevisionChanged(object? sender, EventArgs e)
    {
        // A theme property, override, environment notification, or explicit
        // MarkdownTheme.Invalidate() raised Changed. Rebuild without notifying
        // the theme again, which would loop.
        // Guard against cross-thread calls: MarkdownTheme.Changed is a plain
        // .NET event and a consumer may call Invalidate() from a background thread.
        // Accessing DependencyObject members off the UI thread throws RPC_E_WRONG_THREAD.
        var dq = _control.DispatcherQueue;
        if (dq is null) return;
        if (dq.HasThreadAccess) { if (!_lifetime.IsUnloaded) _rebuild.Request(RebuildReason.Restyle); return; }
        dq.TryEnqueue(() => { if (!_lifetime.IsUnloaded) _rebuild.Request(RebuildReason.Restyle); });
    }
}
