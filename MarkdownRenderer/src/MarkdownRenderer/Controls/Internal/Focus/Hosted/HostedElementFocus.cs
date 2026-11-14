using MarkdownRenderer.Controls.Internal.Embeds;
using MarkdownRenderer.Controls.Internal.Pipeline;
using MarkdownRenderer.Controls.Internal.View;
using MarkdownRenderer.Layout.Internal.Focus;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;

namespace MarkdownRenderer.Controls.Internal.Focus.Hosted;

/// <summary>
/// Moves keyboard focus between the renderer's virtual focus and the
/// realized hosted elements and copy buttons. Every step that can call back
/// into native code is followed by a check that the snapshot, focus plan
/// and focused item it started from are still current; a stale step stops
/// without touching focus again.
/// </summary>
internal sealed class HostedElementFocus
{
    private readonly IHostedElementFocusHost _host;
    private readonly MarkdownViewLifetime _lifetime;
    private readonly PublishedLayout _published;
    private readonly KeyboardFocusModel _focus;
    private readonly HostedFocusPlanPublication _plan;
    private readonly EmbedRealization _embeds;
    private readonly FocusRingPresenter _focusRing;

    internal HostedElementFocus(
        IHostedElementFocusHost host,
        MarkdownViewLifetime lifetime,
        PublishedLayout published,
        KeyboardFocusModel focus,
        HostedFocusPlanPublication plan,
        EmbedRealization embeds,
        FocusRingPresenter focusRing)
    {
        _host = host;
        _lifetime = lifetime;
        _published = published;
        _focus = focus;
        _plan = plan;
        _embeds = embeds;
        _focusRing = focusRing;
    }

    /// <summary>
    /// Captures the snapshot, generation, revision, plan epoch and items that
    /// a hosted focus move starts from, and whether the plan for them is
    /// published yet.
    /// </summary>
    internal HostedFocusPlanStatus TryCaptureTuple(out HostedFocusTuple tuple)
    {
        tuple = default;
        if (_lifetime.IsDisposed || _lifetime.IsUnloaded || _published.Snapshot is not { } snapshot ||
            _focus.Items is not { } items)
        {
            return HostedFocusPlanStatus.Stale;
        }

        long generation = _published.Generation;
        long revision = snapshot.LayoutRevision;
        long epoch = _plan.Epoch;
        tuple = new HostedFocusTuple(snapshot, generation, revision, epoch, items);
        if (!ReferenceEquals(snapshot, _published.Snapshot) ||
            generation != _published.Generation)
        {
            return HostedFocusPlanStatus.Stale;
        }

        long publishedEpoch = _plan.PublishedEpoch;
        if (publishedEpoch != epoch ||
            _plan.PublishedGeneration != generation ||
            _plan.PublishedRevision != revision ||
            _host.AppliedLazyLayoutRevision != revision ||
            snapshot.LayoutRevision != revision)
        {
            return HostedFocusPlanStatus.LayoutPending;
        }

        return HostedFocusPlanStatus.Current;
    }

    /// <summary>
    /// True while <paramref name="tuple"/> still matches the published
    /// layout and focus items and <paramref name="item"/> is still focused.
    /// Does not require the plan to be published.
    /// </summary>
    internal bool IsCurrentRequest(in HostedFocusTuple tuple, FocusableItem item) =>
        !_lifetime.IsDisposed && !_lifetime.IsUnloaded &&
        ReferenceEquals(tuple.Snapshot, _published.Snapshot) &&
        tuple.SnapshotGeneration == _published.Generation &&
        tuple.LayoutRevision == tuple.Snapshot.LayoutRevision &&
        tuple.PlanEpoch == _plan.Epoch &&
        ReferenceEquals(tuple.Items, _focus.Items) &&
        FocusableItemQueries.IsFocusedIdentity(tuple.Items, _focus.FocusedIndex, item);

    /// <summary>
    /// Focuses the hosted element or copy button for <paramref name="item"/>.
    /// Defers the move until the plan is published when it is not yet.
    /// </summary>
    internal HostedFocusResult TryFocus(FocusableItem item, bool reverse)
    {
        HostedFocusPlanStatus status = TryCaptureTuple(out HostedFocusTuple tuple);
        if (status == HostedFocusPlanStatus.Stale ||
            !IsCurrentRequest(tuple, item))
        {
            return HostedFocusResult.Stale;
        }

        if (status == HostedFocusPlanStatus.LayoutPending)
            return Defer(tuple, item, reverse);

        FrameworkElement? element = FindRealizedElement(item);
        if (!IsCurrentOperation(tuple, item))
            return HostedFocusResult.Stale;
        if (element is null)
            return HostedFocusResult.NotRealized;

        if (HostedFocusNavigation.TryFocusHostedDescendant(element, reverse))
            return IsCurrentOperation(tuple, item)
                ? HostedFocusResult.Focused
                : HostedFocusResult.Stale;

        if (!IsCurrentOperation(tuple, item))
            return HostedFocusResult.Stale;

        bool focused = element.Focus(FocusState.Keyboard);
        if (!IsCurrentOperation(tuple, item))
            return HostedFocusResult.Stale;
        return focused ? HostedFocusResult.Focused : HostedFocusResult.NotRealized;
    }

    /// <summary>Gives focus back to a copy button that came back while it was the focused item.</summary>
    internal void RestoreCodeBlockActionIfNeeded(CodeBlockActionPlan plan)
    {
        if (_host.FocusState == FocusState.Unfocused ||
            plan.Realized is null ||
            plan.FocusIdentity is not { } planIdentity ||
            !_embeds.Contains(plan))
        {
            return;
        }

        RefocusIfFocused(planIdentity);
    }

    /// <summary>Gives focus back to a hosted element that came back while it was the focused item.</summary>
    internal void RestoreHostedElementIfNeeded(DeclarativeHostedElementPlan plan)
    {
        if (_host.FocusState == FocusState.Unfocused ||
            plan.Realized is null ||
            plan.FocusIdentity is not { } planIdentity ||
            !_embeds.Contains(plan))
        {
            return;
        }

        RefocusIfFocused(planIdentity);
    }

    /// <summary>
    /// Moves focus to the renderer before a focused copy button is recycled,
    /// keeping its item as the logical focus.
    /// </summary>
    internal void PreserveLogicalFocusBeforeDerealizingCodeBlockAction(FrameworkElement element)
    {
        if (_lifetime.IsUnloaded || _lifetime.IsDisposed)
            return;

        DependencyObject? focused = null;
        try
        {
            if (_host.XamlRoot is { } xamlRoot)
                focused = FocusManager.GetFocusedElement(xamlRoot) as DependencyObject;
        }
        catch
        {
        }

        if (focused is null || !VisualTreeAncestry.IsWithin(focused, element))
            return;

        // Keep the renderer's logical focus index while the native copy button
        // is recycled. If the action comes back into the realize band,
        // RestoreCodeBlockActionIfNeeded transfers focus back to it.
        _ = _host.FocusRenderer();
        _focusRing.Update();
    }

    /// <summary>
    /// Makes the item of the realized <paramref name="element"/> the focused
    /// item. Returns false when the element has no item or the plan is not
    /// current.
    /// </summary>
    internal bool TrySetFocusedItemFor(FrameworkElement element)
    {
        if (TryCaptureTuple(out HostedFocusTuple tuple) != HostedFocusPlanStatus.Current)
            return false;

        FocusableItem? focusIdentity = null;
        foreach (EmbedPlan plan in _embeds.EmbedPlans)
        {
            if (ReferenceEquals(plan.Realized, element) && plan.FocusIdentity is { } identity)
            {
                focusIdentity = identity;
                break;
            }
        }

        if (focusIdentity is null)
        {
            foreach (CodeBlockActionPlan plan in _embeds.CodeBlockActionPlans)
            {
                if (ReferenceEquals(plan.Realized, element) && plan.FocusIdentity is { } identity)
                {
                    focusIdentity = identity;
                    break;
                }
            }
        }

        if (focusIdentity is not { } focusedIdentity ||
            !IsCurrentTuple(tuple))
        {
            return false;
        }

        int focusedIndex = FocusableItemQueries.FindFocusableItemIndex(tuple.Items, focusedIdentity);
        if (focusedIndex < 0 || !IsCurrentTuple(tuple))
            return false;

        _focus.SetFocusedIndex(focusedIndex);
        return true;
    }

    private void RefocusIfFocused(FocusableItem planIdentity)
    {
        if (_focus.Items is not { } items ||
            _focus.FocusedIndex < 0 ||
            _focus.FocusedIndex >= items.Count)
        {
            return;
        }

        var focusedItem = items[_focus.FocusedIndex];
        if (FocusableItemQueries.SameFocusableIdentity(focusedItem, planIdentity))
            _ = TryFocus(focusedItem, reverse: false);
    }

    private bool IsCurrentTuple(in HostedFocusTuple tuple) =>
        !_lifetime.IsDisposed && !_lifetime.IsUnloaded &&
        ReferenceEquals(tuple.Snapshot, _published.Snapshot) &&
        tuple.SnapshotGeneration == _published.Generation &&
        tuple.LayoutRevision == tuple.Snapshot.LayoutRevision &&
        tuple.PlanEpoch == _plan.Epoch &&
        tuple.PlanEpoch == _plan.PublishedEpoch &&
        tuple.SnapshotGeneration == _plan.PublishedGeneration &&
        tuple.LayoutRevision == _plan.PublishedRevision &&
        tuple.LayoutRevision == _host.AppliedLazyLayoutRevision &&
        ReferenceEquals(tuple.Items, _focus.Items);

    private bool IsCurrentOperation(in HostedFocusTuple tuple, FocusableItem item) =>
        IsCurrentTuple(tuple) &&
        FocusableItemQueries.IsFocusedIdentity(tuple.Items, _focus.FocusedIndex, item);

    private HostedFocusResult Defer(in HostedFocusTuple tuple, FocusableItem item, bool reverse)
    {
        if (!IsCurrentRequest(tuple, item))
            return HostedFocusResult.Stale;

        var pending = new PendingLazyFocus(
            tuple.Snapshot,
            tuple.SnapshotGeneration,
            item,
            reverse);
        _focus.BeginLazyFocus(pending);
        _ = _host.FocusRenderer();

        // Focus is a native callback boundary. Do not hide the ring or report
        // deferred ownership if reentrancy changed the snapshot, focus item,
        // pending request, or plan attempt while it ran.
        if (!IsCurrentRequest(tuple, item) ||
            !_focus.IsCurrentLazyFocus(pending))
        {
            return HostedFocusResult.Stale;
        }

        _focusRing.Hide();
        return HostedFocusResult.Deferred;
    }

    private FrameworkElement? FindRealizedElement(FocusableItem item)
    {
        foreach (EmbedPlan plan in _embeds.EmbedPlans)
        {
            if (plan.FocusIdentity is { } identity &&
                FocusableItemQueries.SameFocusableIdentity(identity, item) &&
                plan.Realized is { } element)
            {
                return element;
            }
        }

        foreach (CodeBlockActionPlan plan in _embeds.CodeBlockActionPlans)
        {
            if (plan.FocusIdentity is { } identity &&
                FocusableItemQueries.SameFocusableIdentity(identity, item) &&
                plan.Realized is { } element)
            {
                return element;
            }
        }

        return null;
    }
}
