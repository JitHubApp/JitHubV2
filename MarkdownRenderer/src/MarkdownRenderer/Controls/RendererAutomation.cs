using System.Collections.Generic;
using System.Runtime.CompilerServices;
using MarkdownRenderer.Accessibility;
using MarkdownRenderer.Controls.Focus;
using MarkdownRenderer.Controls.Input;
using MarkdownRenderer.Controls.Selection;
using MarkdownRenderer.Document;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Selection;
using MarkdownRenderer.Selection.Internal;
using MarkdownRenderer.Theming;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Windows.Foundation;

namespace MarkdownRenderer.Controls;

/// <summary>
/// The control's UI Automation side: stable child peer identity, peer
/// notifications, keyboard-focus queries for virtual children, and focus,
/// selection and activation requested by automation clients.
/// </summary>
internal sealed class RendererAutomation : IMarkdownAutomationHost
{
    private readonly MarkdownRendererControl _control;
    private readonly MarkdownViewLifetime _lifetime;
    private readonly MarkdownViewSurface _surface;
    private readonly MarkdownViewport _viewport;
    private readonly PublishedLayout _published;
    private readonly MarkdownAutomationDocumentGeneration _documentGenerations;
    private readonly RendererLocalization _localization;
    private readonly SelectionController _selection;
    private readonly KeyboardFocusModel _focus;
    private readonly FocusRingPresenter _focusRing;
    private readonly FocusedItemCommit _focusCommit;
    private readonly RendererFocusEntry _focusEntry;
    private readonly LinkActivation _links;
    private readonly SelectionHandlesPresenter _handles;
    private readonly HorizontalOverflowScrollVisuals _overflowScroll;

    // Stable per-LinkRun automation peer cache. UIA traversals re-request the
    // children of a block frequently; returning fresh peers each time loses
    // identity tracking (Narrator focus, "where am I"). Tied to the LinkRun
    // weakly so peers are released alongside the layout snapshot.
    private readonly ConditionalWeakTable<LinkRun, MarkdownLinkPeer> _linkPeerCache = new();
    private readonly ConditionalWeakTable<InlineImageRun, MarkdownLinkedImagePeer> _linkedImagePeerCache = new();
    private bool _selectionEventQueued;

    internal RendererAutomation(
        MarkdownRendererControl control,
        MarkdownViewLifetime lifetime,
        MarkdownViewSurface surface,
        MarkdownViewport viewport,
        PublishedLayout published,
        MarkdownAutomationDocumentGeneration documentGenerations,
        RendererLocalization localization,
        SelectionController selection,
        KeyboardFocusModel focus,
        FocusRingPresenter focusRing,
        FocusedItemCommit focusCommit,
        RendererFocusEntry focusEntry,
        LinkActivation links,
        SelectionHandlesPresenter handles,
        HorizontalOverflowScrollVisuals overflowScroll)
    {
        _control = control;
        _lifetime = lifetime;
        _surface = surface;
        _viewport = viewport;
        _published = published;
        _documentGenerations = documentGenerations;
        _localization = localization;
        _selection = selection;
        _focus = focus;
        _focusRing = focusRing;
        _focusCommit = focusCommit;
        _focusEntry = focusEntry;
        _links = links;
        _handles = handles;
        _overflowScroll = overflowScroll;
    }

    /// <summary>The control's existing document peer, without creating one.</summary>
    internal MarkdownAutomationPeer? FindPeer() =>
        FrameworkElementAutomationPeer.FromElement(_control) as MarkdownAutomationPeer;

    int IMarkdownAutomationHost.CultureLcid => _localization.Culture.LCID;

    long IMarkdownAutomationHost.DocumentGeneration => _documentGenerations.CurrentGeneration;

    ThemeSnapshot? IMarkdownAutomationHost.Theme => _published.Theme;

    DocumentRange IMarkdownAutomationHost.SelectionRange => _selection.Range;

    double IMarkdownAutomationHost.ScrollOffsetY => _surface.Scroll?.VerticalOffset ?? 0;

    bool IMarkdownAutomationHost.TryGetVisibleDocumentRect(out Rect rect) => _viewport.TryGetRect(out rect);

    /// <summary>The control's document peer, created when none exists yet.</summary>
    internal MarkdownAutomationPeer? GetOrCreatePeer() =>
        FrameworkElementAutomationPeer.FromElement(_control) as MarkdownAutomationPeer
        ?? FrameworkElementAutomationPeer.CreatePeerForElement(_control) as MarkdownAutomationPeer;

    MarkdownLinkPeer IMarkdownAutomationHost.GetOrCreateLinkPeer(MarkdownBlockPeer parent, LinkRun run)
    {
        // GetValue is atomic for concurrent UIA callers and avoids the
        // TryGetValue+Add race. These caches are cleared when the root peer
        // observes a new semantic snapshot; externally retained old peers are
        // fenced by their captured projection lifetime.
        return _linkPeerCache.GetValue(run, r => new MarkdownLinkPeer(_control, parent, r));
    }

    void IMarkdownAutomationHost.InvalidateChildPeerCaches()
    {
        _linkPeerCache.Clear();
        _linkedImagePeerCache.Clear();
    }

    internal void NotifySnapshotChanged()
    {
        // Do not create an automation peer just to release its previous
        // semantic projection; if a peer exists, reconcile it with the
        // already-published snapshot immediately.
        FindPeer()?.NotifySnapshotChanged();
    }

    internal void InvalidateLayout()
    {
        // Custom-drawn descendants do not receive XAML layout notifications of
        // their own. Refresh an existing document peer after image reflow so UIA
        // clients see the same bounds as the newly painted snapshot immediately.
        FrameworkElementAutomationPeer.FromElement(_control)?.InvalidatePeer();
    }

    MarkdownLinkedImagePeer IMarkdownAutomationHost.GetOrCreateLinkedImagePeer(
        MarkdownBlockPeer parent,
        InlineImageRun run) =>
        _linkedImagePeerCache.GetValue(run, r => new MarkdownLinkedImagePeer(_control, parent, r));

    bool IMarkdownAutomationHost.TryGetLinkedImagePeer(
        InlineImageRun run,
        out MarkdownLinkedImagePeer peer)
    {
        if (_linkedImagePeerCache.TryGetValue(run, out MarkdownLinkedImagePeer? cached) &&
            cached is not null)
        {
            peer = cached;
            return true;
        }

        peer = null!;
        return false;
    }

    bool IMarkdownAutomationHost.IsKeyboardFocusOnLink(LinkRun run)
    {
        return _focus.TryGetFocusedLinkedAtomic(out _, out InlineRun linkedRun) &&
               linkedRun is LinkRun focusedRun &&
               LinkTargets.AreEquivalentLinks(focusedRun, run);
    }

    bool IMarkdownAutomationHost.IsKeyboardFocusOnLinkedImage(InlineImageRun run) =>
        _focus.TryGetFocusedLinkedAtomic(out _, out InlineRun linkedRun) &&
        linkedRun is InlineImageRun focusedRun &&
        ReferenceEquals(focusedRun, run);

    bool IMarkdownAutomationHost.IsKeyboardFocusOnLinkedInlineVectorScene(InlineVectorSceneRun run) =>
        _focus.TryGetFocusedLinkedAtomic(out _, out InlineRun linkedRun) &&
        linkedRun is InlineVectorSceneRun focusedRun &&
        ReferenceEquals(focusedRun, run);

    bool IMarkdownAutomationHost.IsKeyboardFocusOnVectorSemantic(
        Layout.Boxes.VectorSceneBox box,
        int semanticIndex) =>
        _control.FocusState != FocusState.Unfocused &&
        _focus.TryGetFocusedVectorSemantic(out var focusedBox, out var focusedSemanticIndex) &&
        ReferenceEquals(focusedBox, box) &&
        focusedSemanticIndex == semanticIndex;

    bool IMarkdownAutomationHost.IsKeyboardFocusOnHorizontalOverflow(IHorizontalOverflowBox overflow) =>
        _control.FocusState != FocusState.Unfocused &&
        _focus.TryGetFocusedHorizontalOverflow(out var focusedOverflow) &&
        ReferenceEquals(focusedOverflow, overflow);

    bool IMarkdownAutomationHost.IsHorizontalOverflowKeyboardFocusable(IHorizontalOverflowBox overflow) =>
        _focus.TryGetIndexForHorizontalOverflow(overflow, out _);

    bool IMarkdownAutomationHost.HasKeyboardFocusOnVirtualChild =>
        _control.FocusState != FocusState.Unfocused &&
        (_focus.TryGetFocusedLinkedAtomic(out _, out _) ||
         _focus.TryGetFocusedVectorSemantic(out _, out _) ||
         _focus.TryGetFocusedHorizontalOverflow(out _));

    bool IMarkdownAutomationHost.FocusLinkedAtomic(InlineRun run)
    {
        if (!_focus.TryGetIndexForLinkedAtomic(run, out var index))
            return _control.Focus(FocusState.Programmatic);

        return _focusCommit.FocusFromAutomation(index);
    }

    bool IMarkdownAutomationHost.FocusVectorSemantic(
        Layout.Boxes.VectorSceneBox box,
        int semanticIndex)
    {
        if (!_focus.TryGetIndexForVectorSemantic(box, semanticIndex, out int index))
            return false;

        return _focusCommit.FocusFromAutomation(index);
    }

    bool IMarkdownAutomationHost.FocusHorizontalOverflow(IHorizontalOverflowBox overflow)
    {
        if (!_focus.TryGetIndexForHorizontalOverflow(overflow, out int index))
            return false;

        return _focusCommit.FocusFromAutomation(index);
    }

    bool IMarkdownAutomationHost.FocusDocument()
    {
        _focusEntry.SuppressNextPromotion();
        _focus.ClearFocus();
        _focusRing.Update();
        return _focusEntry.FocusWithoutPromotion();
    }

    void IMarkdownAutomationHost.ActivateLinkedAtomic(InlineRun run)
    {
        if (LinkTargets.TryCreateLinkedAtomicTarget(run, out LinkTarget target))
            _links.ActivateFromAutomation(target);
    }

    /// <summary>Activates a host-approved vector-scene link/action from UI Automation.</summary>
    void IMarkdownAutomationHost.ActivateVectorSceneLink(
        Layout.Boxes.VectorSceneBox box,
        int semanticIndex)
    {
        if (box is null || !box.TryGetLink(semanticIndex, out var link) || link is null)
            return;

        var semantic = box.GetSemanticItem(semanticIndex);
        _links.ActivateFromAutomation(new LinkTarget(
            link.Target ?? string.Empty,
            semantic?.Description,
            semantic?.SourceSpan ?? box.SourceRange,
            null,
            link.Action,
            link.External));
    }

    void IMarkdownAutomationHost.Select(DocumentRange range)
    {
        if (!_control.IsSelectionEnabled) return;
        var normalized = range.Normalized();
        _selection.SetAnchor(normalized.Start);
        _selection.ExtendTo(normalized.End);
    }

    void IMarkdownAutomationHost.ClearSelection() => _selection.Clear();

    void IMarkdownAutomationHost.AppendVisibleSelectionHandlePeers(IList<AutomationPeer> peers) =>
        _handles.AppendVisibleAutomationPeers(peers);

    void IMarkdownAutomationHost.ScrollHorizontalOverflow(IHorizontalOverflowBox overflow, ScrollAmount amount) =>
        _overflowScroll.ScrollForAutomation(overflow, amount);

    void IMarkdownAutomationHost.SetHorizontalOverflowPercent(IHorizontalOverflowBox overflow, double percent) =>
        _overflowScroll.SetPercentForAutomation(overflow, percent);

    /// <summary>Raises one selection-changed automation event per dispatcher turn.</summary>
    internal void QueueSelectionChanged()
    {
        if (_selectionEventQueued || _lifetime.IsDisposed || _lifetime.IsUnloaded)
            return;

        _selectionEventQueued = true;
        var dispatcher = _control.DispatcherQueue;
        if (dispatcher is null || !dispatcher.TryEnqueue(() =>
            {
                _selectionEventQueued = false;
                if (_lifetime.IsDisposed || _lifetime.IsUnloaded)
                    return;

                FindPeer()?.NotifySelectionChanged();
            }))
        {
            _selectionEventQueued = false;
        }
    }

    /// <summary>Forgets a queued selection event; its callback is fenced by the unload.</summary>
    internal void ForgetQueuedSelectionChanged() => _selectionEventQueued = false;
}
