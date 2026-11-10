using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using RebuildReason = MarkdownRenderer.Controls.Internal.Pipeline.Rebuild.MarkdownRebuildCoordinatorReason;
using MarkdownRenderer.Document;
using MarkdownRenderer.Hosting;
using MarkdownRenderer.Images;
using MarkdownRenderer.Performance;
using MarkdownRenderer.Accessibility.Internal.Peers;
using MarkdownRenderer.Layout.Internal.Inlines.Runs;
using MarkdownRenderer.Performance.Internal;
using MarkdownRenderer.Controls.Internal.Embeds;
using MarkdownRenderer.Controls.Internal.Highlighting;
using MarkdownRenderer.Controls.Internal.Images;
using MarkdownRenderer.Controls.Internal.Input;
using MarkdownRenderer.Controls.Internal.Input.Links;
using MarkdownRenderer.Controls.Internal.LazyLayout;
using MarkdownRenderer.Controls.Internal.Pipeline;
using MarkdownRenderer.Controls.Internal.Pipeline.Rebuild;
using MarkdownRenderer.Controls.Internal.Providers;
using MarkdownRenderer.Controls.Internal.Readiness;
using MarkdownRenderer.Controls.Internal.Selection.TouchSelection;

namespace MarkdownRenderer.Controls.Internal.Composition;

/// <summary>
/// The pipeline owners’ hosts: rebuild scheduling and layout setup, deferred scene readiness, link activation and localization.
/// </summary>
/// <remarks>
/// The owners these hosts forward to are constructed after the owners that
/// need the hosts, so the control connects them once its owners exist.
/// </remarks>
internal sealed class PipelineHosts :
    IRebuildSchedulerHost,
    IRebuildLayoutSetupHost,
    IDeferredSceneReadinessHost,
    ILinkActivationHost,
    IRendererLocalizationHost,
    IDocumentInputsHost
{
    private readonly MarkdownRendererControl _control;
    private Parts? _owners;

    internal PipelineHosts(MarkdownRendererControl control)
    {
        _control = control;
    }

    /// <summary>The owners the hosts forward to.</summary>
    internal sealed record Parts(
        RendererEvents Events,
        DocumentInputs DocumentInputs,
        RendererAutomation Automation,
        ImageLifetimeScope ImageLifetime,
        ImageRelayout ImageRelayout,
        LazyLayoutRealization LazyLayout,
        RebuildPipeline Pipeline,
        TouchSelectionRestore TouchRestore,
        InteractionReset InteractionReset,
        PerformanceSessionSubscription PerformanceSession,
        EnvironmentSubscription Environment,
        RebuildScheduler Rebuild,
        EmbedRealization Embeds,
        CodeBlockHighlighting Highlighting);

    internal void Connect(Parts owners) => _owners = owners;

    private Parts Owners => _owners ??
        throw new InvalidOperationException("The renderer used its PipelineHosts before connecting them.");

    Microsoft.UI.Dispatching.DispatcherQueue? IRebuildSchedulerHost.DispatcherQueue => _control.DispatcherQueue;

    bool IRebuildSchedulerHost.IsLoaded => _control.IsLoaded;

    string IRebuildSchedulerHost.EffectiveSource => Owners.DocumentInputs.EffectiveSource;

    void IRebuildSchedulerHost.AdvanceDeferredSceneGeneration() => Owners.PerformanceSession.AdvanceDeferredSceneGeneration();

    void IRebuildSchedulerHost.CancelSnapshotWork()
    {
        Owners.LazyLayout.Cancel();
        Owners.ImageRelayout.CancelActive();
    }

    void IRebuildSchedulerHost.ResetInteractionForRebuild()
    {
        Owners.TouchRestore.Capture();
        Owners.InteractionReset.ResetTransientState(clearSelection: false);
    }

    void IRebuildSchedulerHost.DiscardTouchSelectionRestore() => Owners.TouchRestore.Cancel();

    Task IRebuildSchedulerHost.RunRebuildAsync(
        CancellationToken ct,
        RebuildReason reason,
        long generation,
        long pipelineStartTimestamp,
        long sourceUtf16Bytes) =>
        Owners.Pipeline.RunAsync(ct, reason, generation, pipelineStartTimestamp, sourceUtf16Bytes);

    Microsoft.UI.Dispatching.DispatcherQueue? IRebuildLayoutSetupHost.DispatcherQueue => _control.DispatcherQueue;

    FrameworkElement IRebuildLayoutSetupHost.ThemeHost => _control;

    object IRebuildLayoutSetupHost.DocumentOwner => _control;

    CancellationTokenSource? IRebuildLayoutSetupHost.ImageLifetimeCts => Owners.ImageLifetime.Source;

    void IRebuildLayoutSetupHost.RaiseImageUnavailable(
        string source,
        MarkdownImageUnavailableReason reason,
        MarkdownSvgFailureReason? svgFailureReason) =>
        Owners.Events.RaiseImageUnavailable(source, reason, svgFailureReason);

    MarkdownRendererControl IDeferredSceneReadinessHost.DocumentOwner => _control;

    IMarkdownPerformanceSessionInternal? IDeferredSceneReadinessHost.PerformanceSession =>
        _control.PerformanceSession as IMarkdownPerformanceSessionInternal;

    MarkdownAutomationPeer? IDeferredSceneReadinessHost.FindAutomationPeer() => Owners.Automation.FindPeer();

    Microsoft.UI.Dispatching.DispatcherQueue? ILinkActivationHost.DispatcherQueue => _control.DispatcherQueue;

    IMarkdownCommandProvider? ILinkActivationHost.CommandProvider => _control.CommandProvider;

    void ILinkActivationHost.SetDisclosureExpanded(LinkRun run, bool expanded) =>
        Owners.DocumentInputs.SetDisclosureExpanded(run, expanded);

    void ILinkActivationHost.SetDisclosureExpanded(string disclosureId, bool expanded) =>
        Owners.DocumentInputs.SetDisclosureExpanded(disclosureId, expanded);

    void ILinkActivationHost.RaiseLinkClick(MarkdownLinkClickEventArgs args) => Owners.Events.RaiseLinkClick(args);

    string IRendererLocalizationHost.Language => _control.Language;

    IMarkdownStringProvider? IRendererLocalizationHost.StringProvider => _control.StringProvider;

    string? IRendererLocalizationHost.EnvironmentLanguage => Owners.Environment.Snapshot.Language;

    string? IDocumentInputsHost.Markdown => _control.Markdown;

    MarkdownDocument? IDocumentInputsHost.Document => _control.Document;

    Task? IDocumentInputsHost.ActivePipeline => Owners.Rebuild.ActiveTask;

    void IDocumentInputsHost.AssignEngine(MarkdownEngine engine) => _control.Engine = engine;

    void IDocumentInputsHost.ClearDocument() => _control.ClearValue(MarkdownRendererControl.DocumentProperty);

    void IDocumentInputsHost.SetDocument(MarkdownDocument document) =>
        _control.SetValue(MarkdownRendererControl.DocumentProperty, document);

    void IDocumentInputsHost.ResetInputForDocumentReplacement() =>
        Owners.InteractionReset.ResetInputForDocumentReplacement();

    void IDocumentInputsHost.ClearDocumentCaches()
    {
        Owners.Embeds.ClearHostedElementFallbacks();
        Owners.Highlighting.ClearCache();
    }

    void IDocumentInputsHost.RequestRebuild(RebuildReason reason) => Owners.Rebuild.Request(reason);

    void IDocumentInputsHost.RaiseDisclosureToggled(bool expanded) => Owners.Events.RaiseDisclosureToggled(expanded);
}
