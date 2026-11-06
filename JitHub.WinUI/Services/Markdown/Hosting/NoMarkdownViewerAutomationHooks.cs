using System;
using MarkdownRenderer.Images;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace JitHub.Services.Markdown.Hosting;

/// <summary>The product's automation hooks, which change nothing.</summary>
internal sealed class NoMarkdownViewerAutomationHooks : IMarkdownViewerAutomationHooks
{
    /// <summary>Gets the shared instance.</summary>
    public static NoMarkdownViewerAutomationHooks Instance { get; } = new();

    private NoMarkdownViewerAutomationHooks()
    {
    }

    public bool RequiresRemoteImageConsent => false;

    public IMarkdownImageResolver WrapImageResolver(IMarkdownImageResolver resolver) => resolver;

    public string ComposeMarkdown(string markdown, string automationId) => markdown;

    public void ApplyRendererResources(ResourceDictionary resources)
    {
    }

    public IDisposable? WatchRuntimeSettings(DispatcherQueue dispatcherQueue, Action onChanged) => null;
}
