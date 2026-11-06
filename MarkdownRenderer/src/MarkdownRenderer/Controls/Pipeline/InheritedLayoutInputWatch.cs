using Microsoft.UI.Xaml;
using RebuildReason = MarkdownRenderer.Controls.MarkdownRebuildCoordinatorReason;

namespace MarkdownRenderer.Controls.Pipeline;

/// <summary>
/// Rebuilds the layout when the control's inherited FlowDirection or Language
/// changes while it is loaded.
/// </summary>
internal sealed class InheritedLayoutInputWatch
{
    private readonly FrameworkElement _control;
    private readonly RebuildScheduler _rebuild;

    internal InheritedLayoutInputWatch(FrameworkElement control, RebuildScheduler rebuild)
    {
        _control = control;
        _rebuild = rebuild;
    }

    internal void Register()
    {
        // FlowDirection has no DP-changed callback we can register at the
        // class level (it's defined by FrameworkElement). Listen for live
        // changes via the dependency-property-changed callback API so RTL
        // toggling at runtime rebuilds the layout instead of leaving stale
        // CanvasTextLayouts in the previous direction. Gate on IsLoaded so
        // the inheritance pass during initial visual-tree attach doesn't
        // race against the Loaded handler's own RequestRebuild.
        _control.RegisterPropertyChangedCallback(FrameworkElement.FlowDirectionProperty, (_, _) =>
        {
            if (_control.IsLoaded) _rebuild.Request(RebuildReason.Full);
        });
        _control.RegisterPropertyChangedCallback(FrameworkElement.LanguageProperty, (_, _) =>
        {
            if (_control.IsLoaded) _rebuild.Request(RebuildReason.Restyle);
        });
    }
}
