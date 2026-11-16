using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using RebuildReason = MarkdownRenderer.Controls.Internal.Pipeline.Rebuild.MarkdownRebuildCoordinatorReason;
using MarkdownRenderer.Document;
using MarkdownRenderer.Images;
using MarkdownRenderer.Performance;
using MarkdownRenderer.Selection;
using MarkdownRenderer.Theming;
using MarkdownRenderer.Accessibility.Internal.Peers;
using MarkdownRenderer.Layout.Internal.LazyLayout;
using MarkdownRenderer.Layout.Internal.Snapshots;
using MarkdownRenderer.Controls.Internal.Focus;
using MarkdownRenderer.Controls.Internal.Focus.Hosted;
using MarkdownRenderer.Controls.Internal.Input.HorizontalOverflow;
using MarkdownRenderer.Controls.Internal.Input.Links;
using MarkdownRenderer.Controls.Internal.LazyLayout;
using MarkdownRenderer.Controls.Internal.Pipeline;
using MarkdownRenderer.Controls.Internal.Pipeline.Commit;
using MarkdownRenderer.Controls.Internal.Pipeline.Rebuild;
using MarkdownRenderer.Controls.Internal.Recovery;
using MarkdownRenderer.Controls.Internal.Rendering;
using MarkdownRenderer.Controls.Internal.Selection.Adorner;
using MarkdownRenderer.Controls.Internal.View;

namespace MarkdownRenderer.Controls.Internal.Composition;

/// <summary>
/// The keyboard focus owners’ hosts: focus ring, hosted focus, overflow scrolling, reveal, commit, geometry recovery and focus entry.
/// </summary>
/// <remarks>
/// The owners these hosts forward to are constructed after the owners that
/// need the hosts, so the control connects them once its owners exist.
/// </remarks>
internal sealed class FocusHosts :
    IFocusRingHost,
    IHostedElementFocusHost,
    IHorizontalOverflowScrollHost,
    IFocusedItemRevealHost,
    IFocusedItemCommitHost,
    IFocusGeometryRecoveryHost,
    IRendererFocusEntryHost
{
    private readonly MarkdownRendererControl _control;
    private Parts? _owners;

    internal FocusHosts(MarkdownRendererControl control)
    {
        _control = control;
    }

    /// <summary>The owners the hosts forward to.</summary>
    internal sealed record Parts(
        MarkdownViewSurface Surface,
        PublishedLayout Published,
        MarkdownViewport Viewport,
        RecoveryClock RecoveryClock,
        RebuildScheduler Rebuild,
        GeometryRecovery GeometryRecovery,
        InteractiveTextAdorner TextAdorner,
        LinkActivation Links,
        RendererAutomation Automation,
        LayoutCommitState LayoutCommit,
        LazyLayoutRealization LazyLayout,
        PendingFocusGeometryWork FocusGeometryWork,
        FocusRingPresenter FocusRing);

    internal void Connect(Parts owners) => _owners = owners;

    private Parts Owners => _owners ??
        throw new InvalidOperationException("The renderer used its FocusHosts before connecting them.");

    XamlRoot? IFocusRingHost.XamlRoot => _control.XamlRoot;

    Windows.UI.Color? IFocusRingHost.FocusVisualColor => Owners.Published.Theme?.FocusVisualColor;

    void IFocusRingHost.InvalidateInteractiveTextAdorner() => Owners.TextAdorner.Invalidate();

    void IFocusRingHost.RequestFocusGeometryRetry() => Owners.FocusGeometryWork.RequestRetry();

    FocusState IHostedElementFocusHost.FocusState => _control.FocusState;

    XamlRoot? IHostedElementFocusHost.XamlRoot => _control.XamlRoot;

    long IHostedElementFocusHost.AppliedLazyLayoutRevision => Owners.LayoutCommit.AppliedRevision;

    bool IHostedElementFocusHost.FocusRenderer() => _control.Focus(FocusState.Keyboard);

    void IHorizontalOverflowScrollHost.UpdateSelectionOverlay() => Owners.TextAdorner.UpdateSelectionOverlay();

    void IHorizontalOverflowScrollHost.UpdateFocusRing() => Owners.FocusRing.Update();

    void IHorizontalOverflowScrollHost.StartRecoveryClock() => Owners.RecoveryClock.Start();

    bool IHorizontalOverflowScrollHost.HasRecoveryClock => Owners.RecoveryClock.IsRunning;

    void IHorizontalOverflowScrollHost.RequestFullRebuild() => Owners.Rebuild.Request(RebuildReason.Full);

    bool IHorizontalOverflowScrollHost.RequestGeometryRecoveryIfRequired(LayoutSnapshot? snapshot)
        => Owners.GeometryRecovery.RequestIfRequired(snapshot);

    MarkdownAutomationPeer? IHorizontalOverflowScrollHost.FindAutomationPeer()
        => Owners.Automation.FindPeer();

    void IFocusedItemRevealHost.RequestFocusGeometryRetry() => Owners.FocusGeometryWork.RequestRetry();

    bool IFocusedItemCommitHost.FocusRenderer() => _control.Focus(FocusState.Keyboard);

    bool IFocusedItemCommitHost.FocusRendererProgrammatically() => _control.Focus(FocusState.Programmatic);

    void IFocusedItemCommitHost.RealizeLazyFocusTarget(LayoutSnapshot snapshot, LazyLayoutBand estimatedBand)
    {
        var realizationBand = LazyLayoutBand.FromViewport(
            estimatedBand.Top,
            Math.Max(1, estimatedBand.Bottom - estimatedBand.Top),
            MarkdownLazyLayoutPolicy.OverscanPx);
        LazyLayoutBand visibleVectorBand = Owners.Viewport.GetVisibleBand();
        Owners.LazyLayout.QueueForBand(
            snapshot,
            LazyLayoutBand.Union(realizationBand, visibleVectorBand),
            visibleVectorBand,
            preserveScrollAnchor: Owners.Surface.Scroll is not null);
    }

    LinkTarget? IFocusedItemCommitHost.FindLinkTargetAt(DocumentPosition position) => FindLinkTargetAt(position);

    bool IFocusedItemCommitHost.ActivateLink(
        LinkTarget link,
        MarkdownLinkInputKind inputKind,
        MarkdownInputModifiers modifiers)
        => Owners.Links.Activate(link, inputKind, modifiers);

    MarkdownAutomationPeer? IFocusedItemCommitHost.GetOrCreateAutomationPeer()
        => Owners.Automation.GetOrCreatePeer();

    bool IFocusGeometryRecoveryHost.RequestGeometryRecoveryIfRequired(LayoutSnapshot? snapshot)
        => Owners.GeometryRecovery.RequestIfRequired(snapshot);

    void IFocusGeometryRecoveryHost.StartRecoveryClock() => Owners.RecoveryClock.Start();

    bool IFocusGeometryRecoveryHost.HasRecoveryClock => Owners.RecoveryClock.IsRunning;

    void IFocusGeometryRecoveryHost.RequestFullRebuild() => Owners.Rebuild.Request(RebuildReason.Full);

    bool IRendererFocusEntryHost.FocusRendererProgrammatically() => _control.Focus(FocusState.Programmatic);

    private LinkTarget? FindLinkTargetAt(DocumentPosition pos, Point? point = null)
    {
        return Owners.Published.Snapshot is { } snapshot
            ? LinkTargets.FindLinkTargetAt(snapshot, pos, point)
            : null;
    }
}
