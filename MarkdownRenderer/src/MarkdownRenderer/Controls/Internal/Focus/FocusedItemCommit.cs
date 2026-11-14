using System;
using MarkdownRenderer.Controls.Internal.Embeds;
using MarkdownRenderer.Controls.Internal.Focus.Hosted;
using MarkdownRenderer.Controls.Internal.Input.Keyboard;
using MarkdownRenderer.Controls.Internal.Input.Links;
using MarkdownRenderer.Controls.Internal.Pipeline;
using MarkdownRenderer.Controls.Internal.View;
using MarkdownRenderer.Document;
using MarkdownRenderer.Extensions.Vectors;
using MarkdownRenderer.Layout.Internal.Focus;
using MarkdownRenderer.Layout.Internal.Inlines.Runs;
using MarkdownRenderer.Layout.Internal.LazyLayout;
using MarkdownRenderer.Layout.Internal.Snapshots;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Automation.Peers;

namespace MarkdownRenderer.Controls.Internal.Focus;

/// <summary>
/// Makes the item keyboard navigation chose the focused one: scrolls it into
/// view, moves native focus to its hosted element or to the renderer, draws
/// the ring and tells automation clients. A target whose block lazy layout
/// has not measured yet keeps focus on the renderer until the layout
/// publishes, then resumes. Also activates the focused item on Enter or Space.
/// </summary>
internal sealed class FocusedItemCommit
{
    private readonly IFocusedItemCommitHost _host;
    private readonly MarkdownViewLifetime _lifetime;
    private readonly PublishedLayout _published;
    private readonly KeyboardFocusModel _focus;
    private readonly FocusRingPresenter _focusRing;
    private readonly FocusedItemReveal _reveal;
    private readonly HostedElementFocus _hostedFocus;
    private readonly EmbedRealization _embeds;
    private readonly DispatcherQueue _dispatcher;

    internal FocusedItemCommit(
        IFocusedItemCommitHost host,
        MarkdownViewLifetime lifetime,
        PublishedLayout published,
        KeyboardFocusModel focus,
        FocusRingPresenter focusRing,
        FocusedItemReveal reveal,
        HostedElementFocus hostedFocus,
        EmbedRealization embeds,
        DispatcherQueue dispatcher)
    {
        _host = host;
        _lifetime = lifetime;
        _published = published;
        _focus = focus;
        _focusRing = focusRing;
        _reveal = reveal;
        _hostedFocus = hostedFocus;
        _embeds = embeds;
        _dispatcher = dispatcher;
    }

    /// <summary>
    /// Focuses the item at the focused index. Returns true when the key that
    /// moved focus is consumed, including while the target waits for layout.
    /// </summary>
    internal bool Commit(bool reverse)
    {
        var items = _focus.Items;
        LayoutSnapshot? snapshot = _published.Snapshot;
        if (snapshot is null ||
            items is null ||
            _focus.FocusedIndex < 0 ||
            _focus.FocusedIndex >= items.Count)
        {
            _focus.ClearLazyFocus();
            return false;
        }

        long snapshotGeneration = _published.Generation;
        if (!ReferenceEquals(snapshot, _published.Snapshot))
            return true;

        FocusableItem item = items[_focus.FocusedIndex];
        FocusTargetLayoutState layoutState = snapshot.TryGetFocusTargetLayoutState(
            item.BlockIndex,
            out LazyLayoutBand estimatedBand);
        if (!ReferenceEquals(snapshot, _published.Snapshot) ||
            snapshotGeneration != _published.Generation)
        {
            return true;
        }

        if (layoutState is FocusTargetLayoutState.RequiresRealization or FocusTargetLayoutState.Busy)
        {
            WaitForLazyTarget(
                snapshot,
                snapshotGeneration,
                item,
                reverse,
                layoutState == FocusTargetLayoutState.RequiresRealization,
                estimatedBand);
            return true;
        }

        if (layoutState == FocusTargetLayoutState.Unavailable)
        {
            HoldFocusOnRenderer(snapshot, snapshotGeneration);
            return true;
        }

        _focus.ClearLazyFocus();
        bool scrolled = _reveal.Begin();
        FocusCurrent(reverse);
        if (scrolled)
        {
            _dispatcher.TryEnqueue(() =>
            {
                if (_lifetime.IsUnloaded) return;
                _embeds.RealizeVisible();
                FocusCurrent(reverse);
            });
        }
        return true;
    }

    /// <summary>
    /// Keeps native focus on the renderer while the target's top-level block
    /// is measured. This consumes the Tab instead of allowing XAML to advance
    /// beyond the renderer at a lazy-layout boundary.
    /// </summary>
    private void WaitForLazyTarget(
        LayoutSnapshot snapshot,
        long snapshotGeneration,
        FocusableItem item,
        bool reverse,
        bool requiresRealization,
        LazyLayoutBand estimatedBand)
    {
        _focus.BeginLazyFocus(new PendingLazyFocus(
            snapshot,
            snapshotGeneration,
            item,
            reverse));
        var pending = _focus.PendingLazy!.Value;
        _ = _host.FocusRenderer();
        if (!_focus.IsCurrentLazyFocus(pending))
            return;
        _focusRing.Hide();
        if (!_focus.IsCurrentLazyFocus(pending))
            return;

        if (requiresRealization)
        {
            _reveal.ScrollLazyTargetIntoView(estimatedBand, reverse);
            if (!_focus.IsCurrentLazyFocus(pending))
                return;
            _host.RealizeLazyFocusTarget(snapshot, estimatedBand);
        }
    }

    /// <summary>
    /// Keeps focus on the renderer when the target is unavailable. A retiring
    /// snapshot or malformed custom block index must never turn an
    /// already-consumed Tab into focus escape.
    /// </summary>
    private void HoldFocusOnRenderer(LayoutSnapshot snapshot, long snapshotGeneration)
    {
        _focus.ClearLazyFocus();
        _ = _host.FocusRenderer();
        if (!ReferenceEquals(snapshot, _published.Snapshot) ||
            snapshotGeneration != _published.Generation)
        {
            return;
        }
        _focusRing.Hide();
    }

    /// <summary>
    /// Commits the focus target that waited for lazy layout once
    /// <paramref name="snapshot"/> has measured it. Returns true when focus
    /// was committed.
    /// </summary>
    internal bool ResumePendingLazy(LayoutSnapshot snapshot)
    {
        if (_focus.PendingLazy is not { } pending)
            return false;

        if (!ReferenceEquals(snapshot, pending.Snapshot) ||
            pending.SnapshotGeneration != _published.Generation)
        {
            _focus.ClearLazyFocus();
            return false;
        }

        int index = FocusableItemQueries.FindFocusableItemIndex(_focus.Items, pending.Item);
        if (index < 0)
        {
            _focus.ClearFocus();
            return false;
        }

        _focus.SetFocusedIndex(index);
        return Commit(pending.Reverse);
    }

    /// <summary>
    /// Activates the focused item: follows a painted or vector link, or moves
    /// focus into a hosted element. Returns true when the key is consumed.
    /// </summary>
    internal bool Activate()
    {
        if (_focus.PendingLazy is not null)
            return true;

        var items = _focus.Items;
        if (items is null || _focus.FocusedIndex < 0 || _focus.FocusedIndex >= items.Count)
            return false;

        var item = items[_focus.FocusedIndex];
        MarkdownInputModifiers modifiers = InputModifierState.GetCurrentInputModifiers();
        if (item.IsVectorSemantic &&
            _focus.TryGetVectorSemantic(item, out var vectorBox, out int semanticIndex) &&
            vectorBox.TryGetLink(semanticIndex, out var vectorLink) &&
            vectorLink is not null)
        {
            MarkdownVectorSemanticItem? semantic = vectorBox.GetSemanticItem(semanticIndex);
            return _host.ActivateLink(
                new LinkTarget(
                    vectorLink.Target ?? string.Empty,
                    semantic?.Description,
                    semantic?.SourceSpan ?? vectorBox.SourceRange,
                    null,
                    vectorLink.Action,
                    vectorLink.External),
                MarkdownLinkInputKind.Keyboard,
                modifiers);
        }

        if (!item.IsLink)
        {
            return _hostedFocus.TryFocus(item, reverse: false) switch
            {
                HostedFocusResult.Focused => true,
                HostedFocusResult.Deferred => true,
                HostedFocusResult.Stale => true,
                _ => false,
            };
        }

        // Resolve a text link or linked atomic inline (image/math) and use the
        // same activation path as pointer input.
        if (_published.Snapshot is null) return false;
        var pos = new DocumentPosition(item.BlockIndex, item.InlineIndex, 0);
        if (_host.FindLinkTargetAt(pos) is { } link)
        {
            return _host.ActivateLink(link, MarkdownLinkInputKind.Keyboard, modifiers);
        }
        return false;
    }

    /// <summary>
    /// Moves native focus to the focused item's hosted element, or to the
    /// renderer with the ring around a painted item.
    /// </summary>
    internal void FocusCurrent(bool reverse = false)
    {
        if (_focus.PendingLazy is not null)
        {
            _focusRing.Hide();
            return;
        }

        var items = _focus.Items;
        if (items is null || _focus.FocusedIndex < 0 || _focus.FocusedIndex >= items.Count)
        {
            _focusRing.Update();
            return;
        }

        var item = items[_focus.FocusedIndex];
        if (!item.IsLink)
        {
            HostedFocusResult result = _hostedFocus.TryFocus(item, reverse);
            if (result == HostedFocusResult.Focused)
            {
                _focusRing.Collapse();
                return;
            }

            // A pending realization is resumed by the normal layout path. A
            // stale tuple means a callback changed the active focus/snapshot;
            // neither case may write the old caller's focus ring or fallback.
            if (result is HostedFocusResult.Deferred or HostedFocusResult.Stale)
                return;
        }

        if (_hostedFocus.TryCaptureTuple(out HostedFocusTuple fallbackTuple) == HostedFocusPlanStatus.Stale ||
            !_hostedFocus.IsCurrentRequest(fallbackTuple, item))
        {
            return;
        }

        _ = _host.FocusRenderer();
        if (!_hostedFocus.IsCurrentRequest(fallbackTuple, item))
            return;
        _focusRing.Update();
        if (!_hostedFocus.IsCurrentRequest(fallbackTuple, item))
            return;
        NotifyAutomation();
    }

    /// <summary>
    /// Focuses the item at <paramref name="index"/> for an automation client:
    /// reveals it, focuses the renderer and raises automation focus. Returns
    /// whether the renderer took focus.
    /// </summary>
    internal bool FocusFromAutomation(int index)
    {
        _focus.FocusItem(index);
        _reveal.Begin();
        bool focused = _host.FocusRendererProgrammatically();
        _focusRing.Update();
        NotifyAutomation();
        return focused;
    }

    /// <summary>Raises automation focus for the focused painted item.</summary>
    internal void NotifyAutomation()
    {
        // Virtual focus is observable through HasKeyboardFocus even when no UIA
        // client subscribes to focus events. Avoid entering UIA from the input
        // path unless an assistive-technology listener actually needs the event;
        // this also prevents a queued focus notification from racing a subsequent
        // key press and re-entering this provider while the client is polling it.
        if (!AutomationPeer.ListenerExists(AutomationEvents.AutomationFocusChanged))
            return;

        void Raise()
        {
            if (_focus.PendingLazy is not null ||
                !AutomationPeer.ListenerExists(AutomationEvents.AutomationFocusChanged))
                return;

            var peer = _host.GetOrCreateAutomationPeer();
            if (_focus.TryGetFocusedLinkedAtomic(out var inline, out InlineRun linkedRun))
            {
                switch (linkedRun)
                {
                    case LinkRun link:
                        peer?.RaiseFocusForLink(inline, link);
                        break;
                    case InlineImageRun image:
                        peer?.RaiseFocusForLinkedImage(inline, image);
                        break;
                    case InlineVectorSceneRun inlineScene:
                        peer?.RaiseFocusForInlineVectorScene(inlineScene);
                        break;
                }
            }
            else if (_focus.TryGetFocusedVectorSemantic(out var vectorBox, out int semanticIndex))
            {
                peer?.RaiseFocusForVectorSemantic(vectorBox, semanticIndex);
            }
            else if (_focus.TryGetFocusedHorizontalOverflow(out var overflow))
            {
                peer?.RaiseFocusForHorizontalOverflow(overflow);
            }
        }

        // Let WinUI finish the real focus transition to the renderer first,
        // then publish the virtual hyperlink focus event. If we raise inside
        // GotFocus, Narrator can observe the root document focus event last and
        // announce the whole renderer instead of the focused painted link.
        if (_dispatcher is { } dispatcher)
            dispatcher.TryEnqueue(Raise);
        else
            Raise();
    }
}
