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
    private readonly MarkdownLifecycleAutomationBridge _bridge;
    private readonly RuntimeEventSubscription _runtimeEvents;

    /// <param name="document">The fixture document hosts render.</param>
    /// <param name="bridge">The app's lifecycle automation bridge.</param>
    /// <param name="runtimeEvents">The app's best-effort Windows runtime event helper.</param>
    public MarkdownLifecycleViewerHooks(
        MarkdownLifecycleFixtureDocument document,
        MarkdownLifecycleAutomationBridge bridge,
        RuntimeEventSubscription runtimeEvents)
    {
        _document = document ?? throw new ArgumentNullException(nameof(document));
        _bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));
        _runtimeEvents = runtimeEvents ?? throw new ArgumentNullException(nameof(runtimeEvents));
    }

    public bool RequiresRemoteImageConsent => true;

    public IMarkdownImageResolver WrapImageResolver(IMarkdownImageResolver resolver) =>
        MarkdownLifecycleImageResolver.Create(resolver);

    public string ComposeMarkdown(string markdown, string automationId) =>
        _document.Compose(markdown, automationId);

    public void ApplyRendererResources(ResourceDictionary resources)
    {
        MarkdownLifecycleTextScaleResources.Apply(resources, _bridge, _runtimeEvents);

        if (_bridge.IsHighContrastEnabled)
        {
            MarkdownLifecycleHighContrastResources.Apply(resources);
        }
    }

    public IDisposable? WatchRuntimeSettings(DispatcherQueue dispatcherQueue, Action onChanged) =>
        new MarkdownLifecycleRuntimeSettingsWatch(dispatcherQueue, _bridge, onChanged);
}
