using System;
using System.Threading;
using System.Threading.Tasks;
using MarkdownRenderer.Diagnostics.Internal;
using MarkdownRenderer.Hosting;
using MarkdownRenderer.Hosting.Internal;
using MarkdownRenderer.Layout.Boxes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;

namespace MarkdownRenderer.Controls.Embeds;

/// <summary>
/// A hosted element created asynchronously by an
/// <see cref="IMarkdownHostedElementFactory"/>. Each realization attempt runs
/// through a gate so a stale completion is recycled instead of attached, and a
/// failed factory switches the element to its native Markdown fallback.
/// </summary>
internal sealed class DeclarativeHostedElementPlan : EmbedPlan
{
    private readonly HostedElementRealizationGate<MarkdownHostedElementRequest> _realizationGate = new();
    private MarkdownHostedElementRequest? _realizedRequest;
    private bool _fallbackRequested;

    public DeclarativeHostedElementBox Box = null!;
    public IMarkdownHostedElementFactory Factory = null!;
    public override object LogicalOwner => Box;
    public override bool HasActiveWork => Realized is not null || _realizationGate.HasActive;

    public override void Realize(EmbedPlanContext context)
    {
        if (Realized is not null || _fallbackRequested)
            return;

        var request = context.Host.CreateHostedElementRequest(Box, Rect);
        var pipelineToken = context.Host.PipelineToken;
        long pipelineGeneration = context.Host.PipelineGeneration;
        if (!_realizationGate.TryBegin(request, pipelineToken, out var operation))
            return;

        ValueTask<FrameworkElement?> pending;
        try
        {
            // Visible realization is UI-thread only. Calling the public
            // factory here keeps creation of thread-affine WinUI objects on
            // the owning dispatcher while still permitting async loading.
            pending = Factory.CreateAsync(operation.Request, operation.CancellationToken);
        }
        catch (Exception ex)
        {
            CompleteFailure(context, operation, ex, pipelineGeneration);
            return;
        }

        if (pending.IsCompletedSuccessfully)
        {
            CompleteOnUiThread(context, operation, pending.Result, pipelineGeneration);
            return;
        }

        _ = CompleteAsync(context, operation, pending, pipelineGeneration);
    }

    private async Task CompleteAsync(
        EmbedPlanContext context,
        HostedElementRealizationGate<MarkdownHostedElementRequest>.Operation operation,
        ValueTask<FrameworkElement?> pending,
        long pipelineGeneration)
    {
        FrameworkElement? element;
        try
        {
            element = await pending.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (operation.CancellationToken.IsCancellationRequested)
        {
            QueueCompletion(
                context,
                operation,
                element: null,
                () => CompleteOnUiThread(context, operation, null, pipelineGeneration));
            return;
        }
        catch (Exception ex)
        {
            QueueCompletion(
                context,
                operation,
                element: null,
                () => CompleteFailure(context, operation, ex, pipelineGeneration));
            return;
        }

        QueueCompletion(
            context,
            operation,
            element,
            () => CompleteOnUiThread(context, operation, element, pipelineGeneration));
    }

    private void QueueCompletion(
        EmbedPlanContext context,
        HostedElementRealizationGate<MarkdownHostedElementRequest>.Operation operation,
        FrameworkElement? element,
        Action completion)
    {
        var dispatcher = context.Dispatcher;
        bool queued = false;
        try { queued = dispatcher is not null && dispatcher.TryEnqueue(() => completion()); }
        catch (Exception ex)
        {
            MarkdownDiagnostics.WriteLine(
                $"[MarkdownRendererControl] hosted element completion dispatch failed: {ex.Message}");
        }
        if (queued)
            return;

        // Dispatcher shutdown means the control cannot attach the result.
        // Dispose host state as a last resort rather than leaking it.
        _realizationGate.TryClaimCompletion(operation);
        operation.Cancel();
        operation.Dispose();
        RecycleResult(operation.Request, element);
    }

    private void CompleteFailure(
        EmbedPlanContext context,
        HostedElementRealizationGate<MarkdownHostedElementRequest>.Operation operation,
        Exception exception,
        long pipelineGeneration)
    {
        bool wasCurrent = _realizationGate.TryClaimCompletion(operation);
        operation.Dispose();
        if (wasCurrent && context.Host.CanActivateHostedElementFallback(this, pipelineGeneration))
        {
            _fallbackRequested = true;
            MarkdownDiagnostics.WriteLine(
                $"[MarkdownRendererControl] hosted element factory threw: {exception.Message}");
            context.Host.ActivateHostedElementFallback(this);
        }
    }

    private void CompleteOnUiThread(
        EmbedPlanContext context,
        HostedElementRealizationGate<MarkdownHostedElementRequest>.Operation operation,
        FrameworkElement? element,
        long pipelineGeneration)
    {
        bool isCurrent = _realizationGate.TryClaimCompletion(operation);
        bool canAttach = isCurrent &&
            !operation.CancellationToken.IsCancellationRequested &&
            context.Host.CanAttachHostedElement(this, pipelineGeneration);
        operation.Dispose();

        if (!canAttach)
        {
            RecycleResult(operation.Request, element);
            return;
        }

        if (element is null)
        {
            _fallbackRequested = true;
            context.Host.ActivateHostedElementFallback(this);
            return;
        }

        bool ownershipEstablished = false;
        try
        {
            ApplyHostedElementAutomationProperties(Box, element);
            if (context.Lifetime.IsDisposed || !context.Host.CanAttachHostedElement(this, pipelineGeneration))
            {
                RecycleResult(operation.Request, element);
                return;
            }
            _realizedRequest = operation.Request;
            Realized = element;
            ownershipEstablished = true;
            Box.RealizedElement = element;
            UpdatePlacement();
            if (!ReferenceEquals(Realized, element))
                return; // Reentrant teardown already recycled this result.
            if (context.Lifetime.IsDisposed || !context.Host.CanAttachHostedElement(this, pipelineGeneration))
            {
                Derealize(context);
                return;
            }
            element.KeyDown += context.HostedKeyDown;
            context.Surface.Overlay!.Children.Add(element);
        }
        catch (Exception ex)
        {
            if (ownershipEstablished && !ReferenceEquals(Realized, element))
                return; // Never mutate or recycle an already-transferred result.
            element.KeyDown -= context.HostedKeyDown;
            try { context.Surface.Overlay?.Children.Remove(element); } catch { }
            _realizedRequest = null;
            Realized = null;
            Box.RealizedElement = null;
            RecycleResult(operation.Request, element);
            MarkdownDiagnostics.WriteLine(
                $"[MarkdownRendererControl] hosted element attachment failed: {ex.Message}");
            _fallbackRequested = true;
            context.Host.ActivateHostedElementFallback(this);
            return;
        }

        if (!ReferenceEquals(Realized, element) || context.Lifetime.IsDisposed ||
            !context.Host.CanAttachHostedElement(this, pipelineGeneration))
            return;
        context.Host.RealizeVisibleEmbeds();
        context.Host.RestoreFocusedHostedElementIfNeeded(this);
    }

    public override void Derealize(EmbedPlanContext context)
    {
        // Clear the active slot before cancellation so a quick scroll back
        // into view can start a fresh attempt even if the old factory does
        // not observe cancellation promptly.
        _realizationGate.CancelActive();

        if (Realized is null)
            return;

        var element = Realized;
        var request = _realizedRequest;
        _realizedRequest = null;
        Realized = null;
        Box.RealizedElement = null;
        element.KeyDown -= context.HostedKeyDown;
        try { context.Surface.Overlay?.Children.Remove(element); } catch { }
        if (request is not null)
            RecycleResult(request, element);
    }

    public override bool IsSameLogicalEmbed(EmbedPlan other)
        => other is DeclarativeHostedElementPlan declarative &&
           ReferenceEquals(Box, declarative.Box) &&
           ReferenceEquals(Factory, declarative.Factory);

    internal void AdoptRealizedFrom(DeclarativeHostedElementPlan oldPlan)
    {
        _realizedRequest = oldPlan._realizedRequest;
        Realized = oldPlan.Realized;
        oldPlan._realizedRequest = null;
        oldPlan.Realized = null;
        oldPlan.Box.RealizedElement = null;
        Box.RealizedElement = Realized;
    }

    private void RecycleResult(
        MarkdownHostedElementRequest request,
        FrameworkElement? element)
    {
        if (element is null)
            return;

        try { Factory.Recycle(request, element); }
        catch (Exception ex)
        {
            MarkdownDiagnostics.WriteLine(
                $"[MarkdownRendererControl] hosted element Recycle threw: {ex.Message}");
        }
    }

    private static void ApplyHostedElementAutomationProperties(
        DeclarativeHostedElementBox box,
        FrameworkElement element)
    {
        // The semantic wrapper owns the logical identity across virtualization.
        // Give its realized native child a stable, distinct id when the host
        // did not provide one (or copied the logical request id verbatim).
        string nativeAutomationId = AutomationProperties.GetAutomationId(element);
        if (string.IsNullOrWhiteSpace(nativeAutomationId) ||
            string.Equals(nativeAutomationId, box.AutomationId, StringComparison.Ordinal))
        {
            AutomationProperties.SetAutomationId(element, $"{box.AutomationId}-Native");
        }

        // Host-owned names and descriptions remain authoritative when supplied.
        if (!string.IsNullOrWhiteSpace(box.AccessibilityName) &&
            string.IsNullOrWhiteSpace(AutomationProperties.GetName(element)))
        {
            AutomationProperties.SetName(element, box.AccessibilityName);
        }

        if (!string.IsNullOrWhiteSpace(box.AccessibilityDescription) &&
            string.IsNullOrWhiteSpace(AutomationProperties.GetHelpText(element)))
        {
            AutomationProperties.SetHelpText(element, box.AccessibilityDescription);
        }
    }
}
