using System;
using MarkdownRenderer.Selection;
using MarkdownRenderer.Selection.Internal;
using MarkdownRenderer.Theming;
using MarkdownRenderer.Theming.Internal.Environment;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace MarkdownRenderer.Controls.Internal.View;

/// <summary>
/// The renderer state that every control on one <see cref="Microsoft.UI.Xaml.XamlRoot"/>
/// shares: the native environment observer and the selection coordinator.
/// </summary>
/// <remarks>
/// <see cref="Microsoft.UI.Xaml.XamlRoot"/> is not a dependency object, so the
/// services live in a private attached property on the root's content, which
/// lives exactly as long as the window or island content it serves. When the
/// root's content is replaced, controls loaded under the new content get new
/// services; controls still registered with the old ones keep working until
/// they unload.
/// </remarks>
internal sealed class MarkdownXamlRootServices
{
    private static readonly DependencyProperty ServicesProperty =
        DependencyProperty.RegisterAttached(
            "MarkdownXamlRootServices",
            typeof(object),
            typeof(MarkdownXamlRootServices),
            new PropertyMetadata(null));

    private MarkdownXamlRootServices(XamlRoot xamlRoot, DispatcherQueue dispatcher)
    {
        XamlRoot = xamlRoot;
        Environment = new MarkdownEnvironmentMonitor(xamlRoot, dispatcher);
    }

    /// <summary>Gets the root these services observe.</summary>
    internal XamlRoot XamlRoot { get; }

    /// <summary>Gets the root's single native environment observer.</summary>
    internal MarkdownEnvironmentMonitor Environment { get; }

    /// <summary>Gets the coordinator that keeps one selection on this root.</summary>
    internal MarkdownSelectionCoordinator Selection { get; } = new();

    /// <summary>
    /// Gets the services of a loaded element's XamlRoot, creating them on first
    /// use; null while the element has no XamlRoot or dispatcher.
    /// </summary>
    internal static MarkdownXamlRootServices? GetOrCreate(FrameworkElement host)
    {
        ArgumentNullException.ThrowIfNull(host);

        var xamlRoot = host.XamlRoot;
        var dispatcher = host.DispatcherQueue;
        if (xamlRoot is null || dispatcher is null)
            return null;

        if (!dispatcher.HasThreadAccess)
            throw new InvalidOperationException("Markdown XamlRoot services must be acquired on the XAML thread.");

        DependencyObject anchor = (DependencyObject?)xamlRoot.Content ?? host;
        if (anchor.GetValue(ServicesProperty) is MarkdownXamlRootServices services &&
            ReferenceEquals(services.XamlRoot, xamlRoot))
        {
            return services;
        }

        services = new MarkdownXamlRootServices(xamlRoot, dispatcher);
        anchor.SetValue(ServicesProperty, services);
        return services;
    }
}
