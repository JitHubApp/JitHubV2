using System;
using JitHub.Services.Markdown.Hosting;
using MarkdownRenderer.Images;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace JitHub.Services.Markdown.Automation;

/// <summary>
/// The lifecycle automation host's viewer hooks: consent-gated remote images,
/// the lifecycle fixture document, harness-driven text scale and the test
/// High Contrast palette. Registered only when lifecycle automation is enabled
/// for the process.
/// </summary>
internal sealed class MarkdownLifecycleViewerHooks : IMarkdownViewerAutomationHooks
{
    private readonly MarkdownLifecycleFixtureDocument _document;

    /// <param name="document">The fixture document hosts render.</param>
    public MarkdownLifecycleViewerHooks(MarkdownLifecycleFixtureDocument document)
    {
        _document = document ?? throw new ArgumentNullException(nameof(document));
    }

    public bool RequiresRemoteImageConsent => true;

    public IMarkdownImageResolver WrapImageResolver(IMarkdownImageResolver resolver) =>
        MarkdownLifecycleImageResolver.Create(resolver);

    public string ComposeMarkdown(string markdown, string automationId) =>
        _document.Compose(markdown, automationId);

    public void ApplyRendererResources(ResourceDictionary resources)
    {
        MarkdownLifecycleTextScaleResources.Apply(resources);

        if (MarkdownLifecycleAutomationBridge.IsHighContrastEnabled)
        {
            MarkdownLifecycleHighContrastResources.Apply(resources);
        }
    }

    public IDisposable? WatchRuntimeSettings(DispatcherQueue dispatcherQueue, Action onChanged) =>
        new MarkdownLifecycleRuntimeSettingsWatch(dispatcherQueue, onChanged);
}
