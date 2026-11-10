using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Input;
using MarkdownRenderer.Controls.Internal.Pipeline;
using MarkdownRenderer.Controls.Internal.View;

namespace MarkdownRenderer.Controls.Internal.Embeds;

/// <summary>
/// The collaborators an <see cref="EmbedPlan"/> or
/// <see cref="CodeBlockActionPlan"/> needs to realize, place and release its
/// element. Plans are short-lived data objects, so they receive this context
/// on each call instead of holding the control.
/// </summary>
internal sealed class EmbedPlanContext
{
    internal EmbedPlanContext(
        IEmbedPlanHost host,
        MarkdownViewSurface surface,
        MarkdownViewLifetime lifetime,
        PublishedLayout published,
        DispatcherQueue dispatcher,
        KeyEventHandler hostedKeyDown)
    {
        Host = host;
        Surface = surface;
        Lifetime = lifetime;
        Published = published;
        Dispatcher = dispatcher;
        HostedKeyDown = hostedKeyDown;
    }

    internal IEmbedPlanHost Host { get; }

    internal MarkdownViewSurface Surface { get; }

    internal MarkdownViewLifetime Lifetime { get; }

    internal PublishedLayout Published { get; }

    internal DispatcherQueue Dispatcher { get; }

    /// <summary>
    /// The single key handler every realized element subscribes to, so each
    /// unsubscribe removes the same delegate it added.
    /// </summary>
    internal KeyEventHandler HostedKeyDown { get; }
}
