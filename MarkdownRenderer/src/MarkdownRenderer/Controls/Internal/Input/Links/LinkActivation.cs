using System;
using MarkdownRenderer.Controls.Internal.Navigation;
using MarkdownRenderer.Diagnostics.Internal;
using MarkdownRenderer.Hosting;
using MarkdownRenderer.Layout.Internal.Inlines.Runs;
using MarkdownRenderer.Layout.Internal.Snapshots;

namespace MarkdownRenderer.Controls.Internal.Input.Links;

/// <summary>
/// Activates a link from pointer, keyboard or UI Automation input: toggles
/// disclosures, scrolls to internal anchors, offers the activation to the
/// host's command provider, and otherwise raises the public LinkClick event.
/// </summary>
internal sealed class LinkActivation
{
    private readonly ILinkActivationHost _host;
    private readonly BlockScrollNavigation _blockScroll;

    internal LinkActivation(ILinkActivationHost host, BlockScrollNavigation blockScroll)
    {
        _host = host;
        _blockScroll = blockScroll;
    }

    /// <summary>
    /// Activates a pointer or touch intent captured from
    /// <paramref name="snapshot"/>, while that layout is still current.
    /// </summary>
    internal bool ActivateIntent(
        LinkActivationIntent intent,
        LayoutSnapshot snapshot,
        long snapshotGeneration,
        long layoutRevision)
    {
        bool IsCurrent() => _blockScroll.IsCurrentTarget(
            snapshot,
            snapshotGeneration,
            layoutRevision);

        if (!IsCurrent())
            return false;

        if (intent.DisclosureId is { Length: > 0 } disclosureId)
        {
            _host.SetDisclosureExpanded(disclosureId, intent.DisclosureExpanded);
            return true;
        }

        return Activate(
            new LinkTarget(
                intent.Url,
                intent.Title,
                intent.SourceSpan,
                run: null,
                action: intent.Action,
                external: intent.External),
            intent.InputKind,
            intent.Modifiers,
            IsCurrent);
    }

    /// <summary>
    /// Activates a UI Automation invoke. UIA callers can arrive on the RPC
    /// thread, so activation is marshalled back to the UI dispatcher before
    /// it raises the public event.
    /// </summary>
    internal void ActivateFromAutomation(LinkTarget target)
    {
        void Run() => Activate(
            target,
            MarkdownLinkInputKind.Automation,
            MarkdownInputModifiers.None);

        var dispatcher = _host.DispatcherQueue;
        if (dispatcher is not null && !dispatcher.HasThreadAccess)
            dispatcher.TryEnqueue(Run);
        else
            Run();
    }

    /// <summary>
    /// Activates <paramref name="link"/>. When <paramref name="isCurrent"/> is
    /// given, activation stops as soon as it reports the captured layout stale.
    /// </summary>
    internal bool Activate(
        LinkTarget link,
        MarkdownLinkInputKind inputKind,
        MarkdownInputModifiers modifiers,
        Func<bool>? isCurrent = null)
    {
        if (isCurrent is not null && !isCurrent())
            return false;

        if (TryActivateDisclosure(link.Run))
            return true;
        if (isCurrent is not null && !isCurrent())
            return false;

        // Intercept internal fragment anchors (e.g. footnote back/forward
        // links) and scroll without surfacing them to external subscribers.
        bool internalTargetHandled =
            link.Url.StartsWith("#", StringComparison.Ordinal) &&
            _blockScroll.TryScrollToInternalAnchor(link.Url);
        if (isCurrent is not null && !isCurrent())
            return false;

        MarkdownLinkActivationDecision decision;
        try
        {
            IMarkdownCommandProvider? commandProvider = _host.CommandProvider;
            decision = isCurrent is null
                ? MarkdownLinkActivationPolicy.Evaluate(
                    inputKind,
                    internalTargetHandled,
                    commandProvider,
                    link.SourceSpan,
                    link.Url,
                    link.Action)
                : MarkdownLinkActivationPolicy.Evaluate(
                    inputKind,
                    internalTargetHandled,
                    commandProvider,
                    link.SourceSpan,
                    link.Url,
                    link.Action,
                    isCurrent);
        }
        catch (Exception ex)
        {
            // Provider failures must not make built-in link activation vanish.
            MarkdownDiagnostics.WriteLine(
                $"[MarkdownRendererControl] Host activation command failed: {ex.Message}");
            if (isCurrent is not null && !isCurrent())
                return false;
            decision = MarkdownLinkActivationPolicy.CreateHostFallback(inputKind);
        }

        if (isCurrent is not null && !isCurrent())
            return false;
        if (decision.IsHandled)
            return true;

        if (isCurrent is not null && !isCurrent())
            return false;

        _host.RaiseLinkClick(new MarkdownLinkClickEventArgs(
            link.Url,
            link.Title,
            link.SourceSpan,
            decision.InputKind,
            modifiers,
            link.External
                ? MarkdownLinkDisposition.External
                : LinkTargets.GetRequestedDisposition(modifiers),
            link.Action));
        return true;
    }

    private bool TryActivateDisclosure(LinkRun? run)
    {
        if (run is not { IsDisclosure: true })
        {
            return false;
        }

        _host.SetDisclosureExpanded(run, !run.IsExpanded);
        return true;
    }
}
