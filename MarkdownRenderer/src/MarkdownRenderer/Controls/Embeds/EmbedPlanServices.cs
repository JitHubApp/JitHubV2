using System;
using System.Collections.Generic;
using System.Threading;
using MarkdownRenderer.Diagnostics.Internal;
using MarkdownRenderer.Hosting;
using MarkdownRenderer.Hosting.Internal;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace MarkdownRenderer.Controls.Embeds;

/// <summary>
/// Serves embed and code block action plans: the copy button pool,
/// hosted-element requests and attachment checks, and the set of hosted
/// elements that fell back to native Markdown. Focus restoration and
/// clipboard work go to the control.
/// </summary>
internal sealed class EmbedPlanServices : IEmbedPlanHost
{
    private readonly EmbedRealization _realization;
    private readonly IEmbedRealizationHost _host;
    private readonly MarkdownViewSurface _surface;
    private readonly MarkdownViewLifetime _lifetime;
    private readonly MarkdownViewport _viewport;
    private readonly HashSet<HostedElementFallbackKey> _hostedElementFallbacks = new();

    internal EmbedPlanServices(
        EmbedRealization realization,
        IEmbedRealizationHost host,
        ICodeBlockCopyButtonHost copyButtonHost,
        MarkdownViewSurface surface,
        MarkdownViewLifetime lifetime,
        MarkdownViewport viewport)
    {
        _realization = realization;
        _host = host;
        _surface = surface;
        _lifetime = lifetime;
        _viewport = viewport;
        CopyButtons = new CodeBlockCopyButtons(copyButtonHost, surface, lifetime, this);
    }

    internal CodeBlockCopyButtons CopyButtons { get; }

    /// <summary>Copies the hosted elements that fell back, for a layout build.</summary>
    internal HashSet<HostedElementFallbackKey> CopyHostedElementFallbacks() =>
        new HashSet<HostedElementFallbackKey>(_hostedElementFallbacks);

    /// <summary>Lets every hosted element try its factory again.</summary>
    internal void ClearHostedElementFallbacks() => _hostedElementFallbacks.Clear();

    CancellationToken IEmbedPlanHost.PipelineToken => _host.PipelineToken;

    long IEmbedPlanHost.PipelineGeneration => _host.PipelineGeneration;

    (Button Button, bool Attached) IEmbedPlanHost.RentCodeBlockCopyButton() => CopyButtons.Rent();

    void IEmbedPlanHost.ReturnCodeBlockCopyButton(Button button) =>
        CopyButtons.Return(button, keepAttached: !_realization.IsDerealizingAll);

    void IEmbedPlanHost.SetCodeBlockCopyButtonState(Button button, bool copied) =>
        CopyButtons.SetState(button, copied);

    bool IEmbedPlanHost.SetCodeBlockCopyButtonStateForRasterCandidate(
        CodeBlockActionPlan plan,
        Button button,
        FrameworkElement element,
        LayoutSnapshot candidate,
        long generation,
        long layoutRevision,
        long hostedFocusPlanEpoch)
        => CopyButtons.SetStateForRasterCandidate(
            plan, button, element, candidate, generation, layoutRevision, hostedFocusPlanEpoch);

    bool IEmbedPlanHost.IsCurrentRasterCodeActionPublication(
        LayoutSnapshot candidate,
        long generation,
        long layoutRevision,
        long hostedFocusPlanEpoch)
        => _host.IsCurrentRasterCodeActionPublication(candidate, generation, layoutRevision, hostedFocusPlanEpoch);

    void IEmbedPlanHost.CopyCodeBlockToClipboard(CodeBlockActionPlan plan) => _host.CopyCodeBlockToClipboard(plan);

    void IEmbedPlanHost.RestoreFocusedCodeBlockActionIfNeeded(CodeBlockActionPlan plan)
        => _host.RestoreFocusedCodeBlockActionIfNeeded(plan);

    void IEmbedPlanHost.PreserveLogicalFocusBeforeDerealizingCodeBlockAction(FrameworkElement element)
        => _host.PreserveLogicalFocusBeforeDerealizingCodeBlockAction(element);

    MarkdownHostedElementRequest IEmbedPlanHost.CreateHostedElementRequest(
        DeclarativeHostedElementBox box,
        Rect layoutBounds)
    {
        _viewport.TryGet(out double viewportTop, out double viewportHeight, out double viewportWidth);
        return new MarkdownHostedElementRequest(
            box.FactoryKey,
            box.SourceRange,
            layoutBounds,
            new Rect(0, viewportTop, Math.Max(0, viewportWidth), Math.Max(0, viewportHeight)),
            box.Attributes,
            box.AccessibilityRole,
            box.AccessibilityName,
            box.AccessibilityDescription,
            box.SemanticText,
            box.AutomationId);
    }

    bool IEmbedPlanHost.CanAttachHostedElement(
        DeclarativeHostedElementPlan plan,
        long pipelineGeneration)
    {
        if (_lifetime.IsUnloaded ||
            pipelineGeneration != _host.PipelineGeneration ||
            _surface.Overlay is null ||
            !_realization.Contains(plan))
            return false;

        _viewport.TryGet(out double top, out double viewportHeight, out _);
        double bottom = top + viewportHeight;
        return EmbedVisibility.IsInRealizeBand(
            plan.Rect.Top,
            plan.Rect.Bottom,
            top,
            bottom,
            MarkdownRendererControl.EmbedVirtualizationOverscanPx);
    }

    bool IEmbedPlanHost.CanActivateHostedElementFallback(
        DeclarativeHostedElementPlan plan,
        long pipelineGeneration)
        => !_lifetime.IsUnloaded &&
           pipelineGeneration == _host.PipelineGeneration &&
           _realization.Contains(plan);

    void IEmbedPlanHost.ActivateHostedElementFallback(DeclarativeHostedElementPlan plan)
    {
        if (!_hostedElementFallbacks.Add(plan.Box.FallbackKey))
            return;

        MarkdownDiagnostics.WriteLine(
            $"[MarkdownRendererControl] using native markdown fallback for hosted element '{plan.Box.FactoryKey}'.");
        _host.RequestFallbackRebuild();
    }

    void IEmbedPlanHost.RealizeVisibleEmbeds() => _realization.RealizeVisible();

    void IEmbedPlanHost.RestoreFocusedHostedElementIfNeeded(DeclarativeHostedElementPlan plan)
        => _host.RestoreFocusedHostedElementIfNeeded(plan);
}
