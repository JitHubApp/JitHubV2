using MarkdownRenderer.Controls.Pipeline;
using MarkdownRenderer.Theming;
using Microsoft.UI.Xaml;
using RebuildReason = MarkdownRenderer.Controls.MarkdownRebuildCoordinatorReason;

namespace MarkdownRenderer.Controls.Providers;

/// <summary>
/// The loaded view's subscription to its XamlRoot's Markdown environment
/// (text scale, language, contrast and accent), and the last snapshot it
/// delivered. Relayout and palette changes restyle the document.
/// </summary>
internal sealed class EnvironmentSubscription : IMarkdownEnvironmentListener
{
    private readonly FrameworkElement _host;
    private readonly MarkdownViewLifetime _lifetime;
    private readonly RebuildScheduler _rebuild;
    private MarkdownEnvironmentMonitor.Subscription? _subscription;
    private MarkdownEnvironmentSnapshot _snapshot;

    internal EnvironmentSubscription(FrameworkElement host, MarkdownViewLifetime lifetime, RebuildScheduler rebuild)
    {
        _host = host;
        _lifetime = lifetime;
        _rebuild = rebuild;
    }

    /// <summary>The environment the next layout reads; default until the view first loads.</summary>
    internal MarkdownEnvironmentSnapshot Snapshot => _snapshot;

    /// <summary>Subscribes to the environment of the view's XamlRoot, when the view loads.</summary>
    internal void Acquire()
    {
        _subscription?.Dispose();
        _subscription = MarkdownEnvironmentMonitor.Acquire(_host, this);
        _snapshot = _subscription?.Snapshot ?? default;
    }

    internal void Release()
    {
        _subscription?.Dispose();
        _subscription = null;
    }

    void IMarkdownEnvironmentListener.OnMarkdownEnvironmentChanged(
        in MarkdownEnvironmentSnapshot snapshot,
        MarkdownEnvironmentChange changes)
    {
        _snapshot = snapshot;
        if (_lifetime.IsDisposed || _lifetime.IsUnloaded)
            return;

        if ((changes & MarkdownEnvironmentChange.Relayout) != 0)
        {
            _rebuild.Request(RebuildReason.Restyle);
            return;
        }

        // Layout boxes hold an immutable ThemeSnapshot. Until paint commands
        // carry semantic brush roles independently, a palette-only notification
        // must compile and commit a fresh snapshot; repainting the existing
        // boxes would retain stale accent or High Contrast colors.
        if (MarkdownEnvironmentSnapshot.IsBrushOnly(changes))
            _rebuild.Request(RebuildReason.Restyle);
    }
}
