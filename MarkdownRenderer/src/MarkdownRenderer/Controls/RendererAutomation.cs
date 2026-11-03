using System.Runtime.CompilerServices;
using MarkdownRenderer.Accessibility;
using MarkdownRenderer.Controls.Focus;
using MarkdownRenderer.Controls.Input;
using MarkdownRenderer.Document;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Selection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;

namespace MarkdownRenderer.Controls;

/// <summary>
/// The control's UI Automation side: stable child peer identity, peer
/// notifications, keyboard-focus queries for virtual children, and focus,
/// selection and activation requested by automation clients.
/// </summary>
internal sealed class RendererAutomation
{
    private readonly MarkdownRendererControl _control;
    private readonly MarkdownViewLifetime _lifetime;
    private readonly SelectionController _selection;
    private readonly KeyboardFocusModel _focus;
    private readonly FocusRingPresenter _focusRing;
    private readonly FocusedItemCommit _focusCommit;
    private readonly RendererFocusEntry _focusEntry;
    private readonly LinkActivation _links;

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
        SelectionController selection,
        KeyboardFocusModel focus,
        FocusRingPresenter focusRing,
        FocusedItemCommit focusCommit,
        RendererFocusEntry focusEntry,
        LinkActivation links)
    {
        _control = control;
        _lifetime = lifetime;
        _selection = selection;
        _focus = focus;
        _focusRing = focusRing;
        _focusCommit = focusCommit;
        _focusEntry = focusEntry;
        _links = links;
    }

    /// <summary>The control's existing document peer, without creating one.</summary>
    internal MarkdownAutomationPeer? FindPeer() =>
        FrameworkElementAutomationPeer.FromElement(_control) as MarkdownAutomationPeer;

    /// <summary>The control's document peer, created when none exists yet.</summary>
    internal MarkdownAutomationPeer? GetOrCreatePeer() =>
        FrameworkElementAutomationPeer.FromElement(_control) as MarkdownAutomationPeer
        ?? FrameworkElementAutomationPeer.CreatePeerForElement(_control) as MarkdownAutomationPeer;

    internal MarkdownLinkPeer GetOrCreateLinkPeer(MarkdownBlockPeer parent, LinkRun run)
    {
        // GetValue is atomic for concurrent UIA callers and avoids the
        // TryGetValue+Add race. These caches are cleared when the root peer
        // observes a new semantic snapshot; externally retained old peers are
        // fenced by their captured projection lifetime.
        return _linkPeerCache.GetValue(run, r => new MarkdownLinkPeer(_control, parent, r));
    }

    internal void InvalidateChildPeerCaches()
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

    internal MarkdownLinkedImagePeer GetOrCreateLinkedImagePeer(
        MarkdownBlockPeer parent,
        InlineImageRun run) =>
        _linkedImagePeerCache.GetValue(run, r => new MarkdownLinkedImagePeer(_control, parent, r));

    internal bool TryGetLinkedImagePeer(
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

    internal bool IsKeyboardFocusOnLink(LinkRun run)
    {
        return _focus.TryGetFocusedLinkedAtomic(out _, out InlineRun linkedRun) &&
               linkedRun is LinkRun focusedRun &&
               LinkTargets.AreEquivalentLinks(focusedRun, run);
    }

    internal bool IsKeyboardFocusOnLinkedImage(InlineImageRun run) =>
        _focus.TryGetFocusedLinkedAtomic(out _, out InlineRun linkedRun) &&
        linkedRun is InlineImageRun focusedRun &&
        ReferenceEquals(focusedRun, run);

    internal bool IsKeyboardFocusOnLinkedInlineVectorScene(InlineVectorSceneRun run) =>
        _focus.TryGetFocusedLinkedAtomic(out _, out InlineRun linkedRun) &&
        linkedRun is InlineVectorSceneRun focusedRun &&
        ReferenceEquals(focusedRun, run);

    internal bool IsKeyboardFocusOnVectorSemantic(
        Layout.Boxes.VectorSceneBox box,
        int semanticIndex) =>
        _control.FocusState != FocusState.Unfocused &&
        _focus.TryGetFocusedVectorSemantic(out var focusedBox, out var focusedSemanticIndex) &&
        ReferenceEquals(focusedBox, box) &&
        focusedSemanticIndex == semanticIndex;

    internal bool IsKeyboardFocusOnHorizontalOverflow(IHorizontalOverflowBox overflow) =>
        _control.FocusState != FocusState.Unfocused &&
        _focus.TryGetFocusedHorizontalOverflow(out var focusedOverflow) &&
        ReferenceEquals(focusedOverflow, overflow);

    internal bool IsHorizontalOverflowKeyboardFocusable(IHorizontalOverflowBox overflow) =>
        _focus.TryGetIndexForHorizontalOverflow(overflow, out _);

    internal bool HasKeyboardFocusOnVirtualChild =>
        _control.FocusState != FocusState.Unfocused &&
        (_focus.TryGetFocusedLinkedAtomic(out _, out _) ||
         _focus.TryGetFocusedVectorSemantic(out _, out _) ||
         _focus.TryGetFocusedHorizontalOverflow(out _));

    internal bool FocusLinkedAtomic(InlineRun run)
    {
        if (!_focus.TryGetIndexForLinkedAtomic(run, out var index))
            return _control.Focus(FocusState.Programmatic);

        return _focusCommit.FocusFromAutomation(index);
    }

    internal bool FocusVectorSemantic(
        Layout.Boxes.VectorSceneBox box,
        int semanticIndex)
    {
        if (!_focus.TryGetIndexForVectorSemantic(box, semanticIndex, out int index))
            return false;

        return _focusCommit.FocusFromAutomation(index);
    }

    internal bool FocusHorizontalOverflow(IHorizontalOverflowBox overflow)
    {
        if (!_focus.TryGetIndexForHorizontalOverflow(overflow, out int index))
            return false;

        return _focusCommit.FocusFromAutomation(index);
    }

    internal bool FocusDocument()
    {
        _focusEntry.SuppressNextPromotion();
        _focus.ClearFocus();
        _focusRing.Update();
        return _focusEntry.FocusWithoutPromotion();
    }

    internal void ActivateLinkedAtomic(InlineRun run)
    {
        if (LinkTargets.TryCreateLinkedAtomicTarget(run, out LinkTarget target))
            _links.ActivateFromAutomation(target);
    }

    /// <summary>Activates a host-approved vector-scene link/action from UI Automation.</summary>
    internal void ActivateVectorSceneLink(
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

    internal void Select(DocumentRange range)
    {
        if (!_control.IsSelectionEnabled) return;
        var normalized = range.Normalized();
        _selection.SetAnchor(normalized.Start);
        _selection.ExtendTo(normalized.End);
    }

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
