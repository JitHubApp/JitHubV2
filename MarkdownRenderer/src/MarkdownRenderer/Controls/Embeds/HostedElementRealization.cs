using System;
using System.Collections.Generic;
using MarkdownRenderer.Layout;
using Windows.Foundation;

namespace MarkdownRenderer.Controls.Embeds;

/// <summary>
/// Owns the hosted element plans of the published layout (block embeds,
/// inline embeds and declarative hosted elements): the plans, their viewport
/// index, which of them hold active work, their share of the hit-test
/// rectangles, and the last realized count raised to subscribers.
/// <see cref="EmbedRealization"/> decides when each step runs.
/// </summary>
internal sealed class HostedElementRealization
{
    private readonly EmbedRealization _realization;
    private readonly IEmbedRealizationHost _host;
    private readonly EmbedPlanContext _context;
    private readonly EmbedHitTargets _hitTargets;

    private readonly List<EmbedPlan> _embedPlans = new();
    private ViewportBandIndex? _embedPlanIndex;
    // Document ordinals stay sorted so hit-test rectangles retain their source
    // order while scroll work visits only the small active realization band.
    private readonly List<int> _activeEmbedOrdinals = new();

    // Previous realised embed count — used to suppress redundant
    // EmbedsRealizationChanged fires on every scroll tick (UIA-friendly).
    private int _lastFiredRealizedCount = -1;

    internal HostedElementRealization(
        EmbedRealization realization,
        IEmbedRealizationHost host,
        EmbedPlanContext context,
        EmbedHitTargets hitTargets)
    {
        _realization = realization;
        _host = host;
        _context = context;
        _hitTargets = hitTargets;
    }

    /// <summary>Hosted element plans of the published layout, in document order.</summary>
    internal IReadOnlyList<EmbedPlan> Plans => _embedPlans;

    /// <summary>The viewport index over <see cref="Plans"/>, or null when there are none.</summary>
    internal ViewportBandIndex? Index => _embedPlanIndex;

    /// <summary>The number of hosted elements currently realized.</summary>
    internal int RealizedCount
    {
        get
        {
            int n = 0;
            foreach (int ordinal in _activeEmbedOrdinals)
            {
                if (_embedPlans[ordinal].Realized is not null)
                    n++;
            }
            return n;
        }
    }

    internal bool Contains(EmbedPlan plan) => _embedPlans.Contains(plan);

    /// <summary>Appends a plan found by plan collection, in document order.</summary>
    internal void Add(EmbedPlan plan) => _embedPlans.Add(plan);

    /// <summary>Indexes the collected plans by vertical extent.</summary>
    internal void CommitIndex() => _embedPlanIndex = BuildPlanIndex(_embedPlans);

    /// <summary>Forgets every plan and its index. Realized elements must already be released.</summary>
    internal void Clear()
    {
        _embedPlans.Clear();
        _embedPlanIndex = null;
        _activeEmbedOrdinals.Clear();
    }

    /// <summary>Forgets which plans hold active work, after every element was released.</summary>
    internal void ClearActiveOrdinals() => _activeEmbedOrdinals.Clear();

    /// <summary>
    /// Makes the next realization pass raise EmbedsRealizationChanged even
    /// when the count is unchanged, because plan identities changed.
    /// </summary>
    internal void ResetRealizedCountNotification() => _lastFiredRealizedCount = -1;

    /// <summary>Copies the current plans so a teardown or rebuild can work on a detached array.</summary>
    internal EmbedPlan[] CapturePlans() => _embedPlans.ToArray();

    /// <summary>
    /// Releases the elements held by <paramref name="plans"/>. The array is
    /// detached, so native callbacks cannot invalidate it.
    /// </summary>
    internal void DerealizeDetached(EmbedPlan[] plans)
    {
        foreach (EmbedPlan plan in plans)
            plan.Derealize(_context);
    }

    /// <summary>
    /// Moves realized elements from <paramref name="oldPlans"/> to the new
    /// plans for the same logical element. Returns false as soon as the
    /// publication of <paramref name="snapshot"/> stops being current.
    /// </summary>
    internal bool AdoptRealizedElements(
        EmbedPlan[] oldPlans,
        LayoutSnapshot snapshot,
        long planGeneration,
        long hostedFocusPlanEpoch,
        long hostedFocusPlanRevision)
    {
        if (oldPlans.Length > 0)
        {
            // Only plans owned by the same logical box can be the same
            // logical embed. Index realized plans once: a long document can
            // otherwise spend O(n²) UI-thread work after every lazy-layout or
            // image-size publication.
            var oldByOwner = new Dictionary<object, List<EmbedPlan>>(
                ReferenceEqualityComparer.Instance);
            foreach (EmbedPlan oldPlan in oldPlans)
            {
                if (oldPlan.Realized is null)
                    continue;
                if (!oldByOwner.TryGetValue(oldPlan.LogicalOwner, out var candidates))
                    oldByOwner.Add(oldPlan.LogicalOwner, candidates = []);
                candidates.Add(oldPlan);
            }

            for (int ordinal = 0; ordinal < _embedPlans.Count; ordinal++)
            {
                if (!_host.IsCurrentPlanPublication(
                        snapshot, planGeneration, hostedFocusPlanEpoch, hostedFocusPlanRevision))
                    return false;
                EmbedPlan newPlan = _embedPlans[ordinal];
                if (!oldByOwner.TryGetValue(newPlan.LogicalOwner, out var candidates))
                    continue;
                for (int index = 0; index < candidates.Count; index++)
                {
                    EmbedPlan oldPlan = candidates[index];
                    if (!newPlan.IsSameLogicalEmbed(oldPlan))
                        continue;

                    if (newPlan is DeclarativeHostedElementPlan newDeclarative &&
                        oldPlan is DeclarativeHostedElementPlan oldDeclarative)
                    {
                        newDeclarative.AdoptRealizedFrom(oldDeclarative);
                    }
                    else
                    {
                        newPlan.Realized = oldPlan.Realized;
                        oldPlan.Realized = null;
                        AttachRealizedElement(newPlan);
                    }
                    newPlan.UpdatePlacement();
                    if (!_host.IsCurrentPlanPublication(
                            snapshot, planGeneration, hostedFocusPlanEpoch, hostedFocusPlanRevision))
                        return false;
                    candidates.RemoveAt(index);
                    break;
                }
            }
        }

        return true;
    }

    /// <summary>Marks every plan with active work as active.</summary>
    internal void MarkActiveOrdinals()
    {
        for (int index = 0; index < _embedPlans.Count; index++)
        {
            if (_embedPlans[index].HasActiveWork)
                _activeEmbedOrdinals.Add(index);
        }
    }

    /// <summary>
    /// Realizes the hosted elements in the realization band, releases the
    /// ones that left the wider derealization band, adds the realized
    /// elements' hit rectangles and raises EmbedsRealizationChanged when the
    /// realized count changed. Stops as soon as <paramref name="pass"/> stops
    /// being current.
    /// </summary>
    /// <remarks>
    /// Kept as one method, over 80 lines, because realization, release and
    /// the count notification run in a fixed order against one captured
    /// <see cref="VisibleRealizationPass"/>; every step that can reenter is
    /// followed by the same currency check.
    /// </remarks>
    internal void RealizeVisible(in VisibleRealizationPass pass, double top, double bottom)
    {
        if (_embedPlans.Count == 0)
        {
            // No embeds: still emit a transition-to-zero event so subscribers
            // that mirror the count (e.g. UI-automation) don't go stale after
            // a rebuild that dropped all embeds.
            if (_lastFiredRealizedCount != 0)
            {
                _lastFiredRealizedCount = 0;
                _host.RaiseEmbedsRealizationChanged();
            }
            return;
        }

        ViewportBandIndex? embedIndex = pass.EmbedIndex;
        ViewportRange embedRange = embedIndex?.Find(
            top - MarkdownRendererControl.EmbedVirtualizationOverscanPx,
            bottom + MarkdownRendererControl.EmbedVirtualizationOverscanPx) ?? default;
        for (int index = embedRange.Start; index < embedRange.End; index++)
        {
            int ordinal = embedIndex!.GetBlockOrdinal(index);
            EmbedPlan plan = _embedPlans[ordinal];
            double pTop = plan.Rect.Top;
            double pBottom = plan.Rect.Bottom;
            bool inRealize = EmbedVisibility.IsInRealizeBand(
                pTop, pBottom, top, bottom, MarkdownRendererControl.EmbedVirtualizationOverscanPx);
            if (inRealize)
            {
                plan.Realize(_context);
                if (!_realization.IsCurrent(pass))
                    return;
                if (plan.HasActiveWork)
                    EmbedRealization.AddActiveOrdinal(_activeEmbedOrdinals, ordinal);
            }
        }

        for (int index = _activeEmbedOrdinals.Count - 1; index >= 0; index--)
        {
            int ordinal = _activeEmbedOrdinals[index];
            EmbedPlan plan = _embedPlans[ordinal];
            if (EmbedVisibility.IsInDerealizeBand(
                plan.Rect.Top, plan.Rect.Bottom, top, bottom,
                MarkdownRendererControl.EmbedVirtualizationDerealizeOverscanPx))
                continue;
            plan.Derealize(_context);
            if (!_realization.IsCurrent(pass))
                return;
            _activeEmbedOrdinals.RemoveAt(index);
        }

        foreach (int ordinal in _activeEmbedOrdinals)
        {
            EmbedPlan plan = _embedPlans[ordinal];
            // Pending declarative factories occupy the active band but do not
            // contribute a hit-test rectangle or a realized-element count.
            if (plan.Realized is null)
                continue;

            // Rebuild hit-rect caches from realised plans in document order.
            if (plan is BlockEmbedPlan bp)
            {
                Rect rect = bp.Rect;
                double left = Math.Round(rect.X);
                double t = Math.Round(rect.Y);
                double w = Math.Round(rect.Width);
                double h = Math.Round(rect.Height);
                _hitTargets.AddBlock(bp.Box, new Rect(left, t, w, h));
            }
            else if (plan is InlineEmbedPlan ip)
            {
                double iLeft = Math.Round(ip.Rect.X);
                double iTop  = Math.Round(ip.Rect.Y);
                double iW    = Math.Round(ip.Rect.X + ip.Rect.Width)  - iLeft;
                double iH    = Math.Round(ip.Rect.Y + ip.Rect.Height) - iTop;
                _hitTargets.AddInline(ip.Icb, ip.Run, new Rect(iLeft, iTop, iW, iH));
            }
            else if (plan is DeclarativeHostedElementPlan declarative)
            {
                _hitTargets.AddBlock(declarative.Box, declarative.Rect);
            }
        }

        // Surface the realised count to subscribers (e.g. sample app exposing
        // it via a hidden TextBlock for UI-automation tests) — but only when
        // the count actually changed. Firing on every scroll tick causes a
        // property-change event storm for any UIA listener bound to a Name
        // property mirror in the subscriber's UI.
        int realised = 0;
        foreach (int ordinal in _activeEmbedOrdinals)
        {
            if (_embedPlans[ordinal].Realized is not null)
                realised++;
        }
        if (realised != _lastFiredRealizedCount)
        {
            _lastFiredRealizedCount = realised;
            _host.InvalidateAutomationLayout();
            if (!_realization.IsCurrent(pass))
                return;
            _host.RaiseEmbedsRealizationChanged();
        }
    }

    private static ViewportBandIndex? BuildPlanIndex(List<EmbedPlan> plans)
    {
        if (plans.Count == 0)
            return null;
        var index = new ViewportBandIndex(plans.Count);
        for (int ordinal = 0; ordinal < plans.Count; ordinal++)
        {
            Rect rect = plans[ordinal].Rect;
            index.SetEntry(ordinal, ordinal, rect.Top, rect.Bottom);
        }
        index.Commit();
        return index;
    }

    private static void AttachRealizedElement(EmbedPlan plan)
    {
        switch (plan)
        {
            case BlockEmbedPlan block:
                block.Box.RealizedElement = block.Realized;
                break;
            case InlineEmbedPlan inline:
                inline.Run.RealizedElement = inline.Realized;
                break;
            case DeclarativeHostedElementPlan declarative:
                declarative.Box.RealizedElement = declarative.Realized;
                break;
        }
    }
}
