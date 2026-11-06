using System;
using MarkdownRenderer.Images;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace JitHub.Services.Markdown.Hosting;

/// <summary>
/// The seam an automation host uses to change what a Markdown viewer renders.
/// Product builds register <see cref="NoMarkdownViewerAutomationHooks"/>; only
/// the lifecycle automation host registers an implementation that changes anything.
/// </summary>
internal interface IMarkdownViewerAutomationHooks
{
    /// <summary>
    /// Gets whether third-party remote images need per-document consent. The
    /// product loads trusted HTTPS images by default; automation opts into the
    /// configurable privacy mode so the prompt, consent and retry path stay covered.
    /// </summary>
    bool RequiresRemoteImageConsent { get; }

    /// <summary>Wraps the host's image resolver.</summary>
    /// <param name="resolver">The host's image resolver.</param>
    /// <returns>The resolver the renderer uses.</returns>
    IMarkdownImageResolver WrapImageResolver(IMarkdownImageResolver resolver);

    /// <summary>Composes the Markdown a host renders.</summary>
    /// <param name="markdown">The host's own Markdown.</param>
    /// <param name="automationId">The host's automation id.</param>
    /// <returns>The Markdown to render.</returns>
    string ComposeMarkdown(string markdown, string automationId);

    /// <summary>Adds local overrides after the host has applied its own.</summary>
    /// <param name="resources">The renderer's resource dictionary.</param>
    void ApplyRendererResources(ResourceDictionary resources);

    /// <summary>Starts watching for settings changes that need the renderer's resources reapplied.</summary>
    /// <param name="dispatcherQueue">The host's dispatcher queue.</param>
    /// <param name="onChanged">Called on the dispatcher when the resources must be reapplied.</param>
    /// <returns>The watch, or <see langword="null"/> when there is nothing to watch.</returns>
    IDisposable? WatchRuntimeSettings(DispatcherQueue dispatcherQueue, Action onChanged);
}
