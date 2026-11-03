using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using RebuildReason = MarkdownRenderer.Controls.MarkdownRebuildCoordinatorReason;
using MarkdownRenderer.Accessibility;
using MarkdownRenderer.Diagnostics;
using MarkdownRenderer.Document;
using MarkdownRenderer.Hosting;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Performance;
using MarkdownRenderer.Theming;
using MarkdownRenderer.Controls.Composition;
using MarkdownRenderer.Controls.Embeds;
using MarkdownRenderer.Controls.Focus;
using MarkdownRenderer.Controls.Highlighting;
using MarkdownRenderer.Controls.Images;
using MarkdownRenderer.Controls.Rendering;
using MarkdownRenderer.Controls.LazyLayout;
using MarkdownRenderer.Controls.Pipeline;
using MarkdownRenderer.Controls.Readiness;
using MarkdownRenderer.Controls.Selection;

namespace MarkdownRenderer.Controls.Composition;

/// <summary>
/// The embed owners’ hosts: realization, code block copy buttons and code block highlighting.
/// </summary>
/// <remarks>
/// The owners these hosts forward to are constructed after the owners that
/// need the hosts, so the control connects them once its owners exist.
/// </remarks>
internal sealed class EmbedHosts :
    IEmbedRealizationHost,
    ICodeBlockCopyButtonHost,
    ICodeBlockHighlightingHost
{
    private readonly MarkdownRendererControl _control;
    private Parts? _owners;

    internal EmbedHosts(MarkdownRendererControl control)
    {
        _control = control;
    }

    /// <summary>The owners the hosts forward to.</summary>
    internal sealed record Parts(
        RendererEvents Events,
        PublishedLayout Published,
        PipelineCancellationRetirement CancellationRetirement,
        RebuildScheduler Rebuild,
        CanvasInvalidation CanvasInvalidation,
        VisibleWorkScheduler VisibleWork,
        RendererAutomation Automation,
        LayoutCommitPublisher LayoutPublisher,
        RasterPlacementPublisher RasterPlacement,
        LazyLayoutRealization LazyLayout,
        HostedFocusPlanPublication HostedFocusPlan,
        RendererLocalization Localization,
        CodeBlockClipboard CodeBlockClipboard,
        VisibleImageLoading VisibleImages,
        HostedElementFocus HostedFocus,
        ImagePlanSet ImagePlans,
        ImageSubscriptions ImageSubscriptions);

    internal void Connect(Parts owners) => _owners = owners;

    private Parts Owners => _owners ??
        throw new InvalidOperationException("The renderer used its EmbedHosts before connecting them.");

    CancellationToken IEmbedRealizationHost.PipelineToken => Owners.Rebuild.Token;

    long IEmbedRealizationHost.PipelineGeneration => Owners.Rebuild.Generation;

    IMarkdownHostedElementFactory? IEmbedRealizationHost.HostedElementFactory => _control.HostedElementFactory;

    ViewportBandIndex? IEmbedRealizationHost.ImagePlanIndex => Owners.ImagePlans.Index;

    void IEmbedRealizationHost.AddImagePlan(ImageBox image) => AddImagePlan(image);

    bool IEmbedRealizationHost.RealizeVisibleImages(
        in VisibleRealizationPass pass,
        double top,
        double viewportHeight,
        double bottom)
        => Owners.VisibleImages.Realize(pass, top, viewportHeight, bottom);

    bool IEmbedRealizationHost.IsCurrentPlanPublication(
        LayoutSnapshot snapshot,
        long generation,
        long hostedFocusPlanEpoch,
        long layoutRevision)
        => Owners.LayoutPublisher.IsCurrentPlanPublication(snapshot, generation, hostedFocusPlanEpoch, layoutRevision);

    bool IEmbedRealizationHost.IsCurrentRasterCodeActionPublication(
        LayoutSnapshot candidate,
        long generation,
        long layoutRevision,
        long hostedFocusPlanEpoch)
        => Owners.RasterPlacement.IsCurrentPublication(candidate, generation, layoutRevision, hostedFocusPlanEpoch);

    void IEmbedRealizationHost.InvalidateHostedFocusPlanPublication() => Owners.HostedFocusPlan.Invalidate();

    void IEmbedRealizationHost.CopyCodeBlockToClipboard(CodeBlockActionPlan plan) => Owners.CodeBlockClipboard.Copy(plan);

    void IEmbedRealizationHost.RestoreFocusedCodeBlockActionIfNeeded(CodeBlockActionPlan plan)
        => Owners.HostedFocus.RestoreCodeBlockActionIfNeeded(plan);

    void IEmbedRealizationHost.PreserveLogicalFocusBeforeDerealizingCodeBlockAction(FrameworkElement element)
        => Owners.HostedFocus.PreserveLogicalFocusBeforeDerealizingCodeBlockAction(element);

    void IEmbedRealizationHost.RestoreFocusedHostedElementIfNeeded(DeclarativeHostedElementPlan plan)
        => Owners.HostedFocus.RestoreHostedElementIfNeeded(plan);

    void IEmbedRealizationHost.RequestFallbackRebuild() => Owners.Rebuild.Request(RebuildReason.Restyle);

    void IEmbedRealizationHost.InvalidateAutomationLayout() => Owners.Automation.InvalidateLayout();

    void IEmbedRealizationHost.RaiseEmbedsRealizationChanged() => Owners.Events.RaiseEmbedsRealizationChanged();

    Style? ICodeBlockCopyButtonHost.CodeBlockCopyButtonStyle => _control.CodeBlockCopyButtonStyle;

    string ICodeBlockCopyButtonHost.ResolveCodeBlockCopyButtonLabel(bool copied) =>
        copied ? ResolvedCodeBlockCopiedButtonLabel : _control.ResolvedCodeBlockCopyButtonLabel;

    object ICodeBlockHighlightingHost.DocumentOwner => _control;

    Microsoft.UI.Dispatching.DispatcherQueue? ICodeBlockHighlightingHost.DispatcherQueue => _control.DispatcherQueue;

    ICodeHighlighter? ICodeBlockHighlightingHost.CodeHighlighter => _control.CodeHighlighter;

    bool ICodeBlockHighlightingHost.IsCodeBlockSyntaxHighlightingEnabled => _control.IsCodeBlockSyntaxHighlightingEnabled;

    IMarkdownPerformanceSession? ICodeBlockHighlightingHost.PerformanceSession => _control.PerformanceSession;

    Theming.ThemeSnapshot? ICodeBlockHighlightingHost.ThemeSnapshot => Owners.Published.Theme;

    double ICodeBlockHighlightingHost.LazyScrollVelocityPixelsPerSecond => Owners.LazyLayout.ScrollVelocity;

    void ICodeBlockHighlightingHost.ScheduleVisibleCodeBlockHighlighting() => Owners.VisibleWork.ScheduleCodeBlockHighlighting();

    void ICodeBlockHighlightingHost.InvalidateCanvas() => Owners.CanvasInvalidation.Invalidate();

    Task ICodeBlockHighlightingHost.RetireCancellationTokenSource(CancellationTokenSource? cts, Task? lifetime) =>
        Owners.CancellationRetirement.Retire(cts, lifetime);

    private void AddImagePlan(Layout.Boxes.ImageBox image)
    {
        if (!Owners.ImageSubscriptions.Subscribe(image))
            return;

        Owners.ImagePlans.Add(image);
    }

    private string ResolvedCodeBlockCopiedButtonLabel =>
        string.IsNullOrWhiteSpace(_control.CodeBlockCopiedButtonLabel)
            ? Owners.Localization.Resolve(MarkdownStringKeys.CodeCopied, MarkdownLocalizedStrings.CodeBlockCopied)
            : _control.CodeBlockCopiedButtonLabel!;
}
