using System;
using System.Collections.Generic;
using MarkdownRenderer.Diagnostics;
using MarkdownRenderer.Hosting;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;

namespace MarkdownRenderer.Controls.Embeds;

/// <summary>
/// Owns the hosted elements and code block copy buttons laid over the
/// document: their plans, the viewport index over those plans, which plans
/// are realized, the hit-test rectangles of realized elements, and the
/// hosted elements that fell back to native Markdown (through
/// <see cref="EmbedPlanServices"/>).
/// </summary>
/// <remarks>
/// Kept whole, over 500 lines, because plan collection, adoption of realized
/// elements, visible-realization passes and teardown all mutate the same plan
/// lists and active ordinals, and each step's reentrancy fence reads the
/// others' state.
/// </remarks>
internal sealed class EmbedRealization
{
    private readonly IEmbedRealizationHost _host;
    private readonly MarkdownViewLifetime _lifetime;
    private readonly PublishedLayout _published;
    private readonly MarkdownViewport _viewport;
    private readonly DispatcherQueue _dispatcher;
    private readonly EmbedPlanServices _services;

    private readonly List<EmbedPlan> _embedPlans = new();
    private ViewportBandIndex? _embedPlanIndex;
    // Document ordinals stay sorted so hit-test rectangles retain their source
    // order while scroll work visits only the small active realization band.
    private readonly List<int> _activeEmbedOrdinals = new();
    private readonly CodeBlockActionRealization _codeBlockActions;
    private bool _derealizingAllEmbeds;
    private bool _realizingVisibleEmbeds;
    private bool _realizeVisibleEmbedsRequested;
    private bool _realizeVisibleEmbedsContinuationQueued;
    private long _realizeVisibleEmbedsContinuationTicket;
    private long _realizationPassEpoch;

    // Previous realised embed count — used to suppress redundant
    // EmbedsRealizationChanged fires on every scroll tick (UIA-friendly).
    private int _lastFiredRealizedCount = -1;

    internal EmbedRealization(
        IEmbedRealizationHost host,
        ICodeBlockCopyButtonHost copyButtonHost,
        MarkdownViewSurface surface,
        MarkdownViewLifetime lifetime,
        PublishedLayout published,
        MarkdownViewport viewport,
        DispatcherQueue dispatcher,
        KeyEventHandler hostedKeyDown)
    {
        _host = host;
        _lifetime = lifetime;
        _published = published;
        _viewport = viewport;
        _dispatcher = dispatcher;
        _services = new EmbedPlanServices(this, host, copyButtonHost, surface, lifetime, viewport);
        Context = new EmbedPlanContext(_services, surface, lifetime, published, dispatcher, hostedKeyDown);
        _codeBlockActions = new CodeBlockActionRealization(
            this, host, Context, HitTargets, _services.CopyButtons, viewport);
    }

    /// <summary>The context every plan realizes, places and releases its element with.</summary>
    internal EmbedPlanContext Context { get; }

    /// <summary>The rectangles of realized hosted elements and copy buttons.</summary>
    internal EmbedHitTargets HitTargets { get; } = new();

    /// <summary>True while <see cref="DerealizeAll"/> is releasing every element.</summary>
    internal bool IsDerealizingAll => _derealizingAllEmbeds;

    /// <summary>Hosted element plans of the published layout, in document order.</summary>
    internal IReadOnlyList<EmbedPlan> EmbedPlans => _embedPlans;

    /// <summary>Code block copy button plans of the published layout, in document order.</summary>
    internal IReadOnlyList<CodeBlockActionPlan> CodeBlockActionPlans => _codeBlockActions.Plans;

    /// <summary>True when plan collection measured at least one code block.</summary>
    internal bool HasMeasuredCodeBlocks => _codeBlockActions.HasMeasuredCodeBlocks;

    /// <summary>The number of hosted elements currently realized.</summary>
    internal int RealizedEmbedCount
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

    internal bool Contains(CodeBlockActionPlan plan) => _codeBlockActions.Contains(plan);

    /// <summary>Copies the hosted elements that fell back, for a layout build.</summary>
    internal HashSet<HostedElementFallbackKey> CopyHostedElementFallbacks() =>
        _services.CopyHostedElementFallbacks();

    /// <summary>Lets every hosted element try its factory again.</summary>
    internal void ClearHostedElementFallbacks() => _services.ClearHostedElementFallbacks();

    /// <summary>
    /// Releases every realized element and copy button. Elements leave the
    /// overlay and go back to their factories or the pool; plans remain.
    /// </summary>
    internal void DerealizeAll()
    {
        if (_derealizingAllEmbeds)
            return;

        _host.InvalidateHostedFocusPlanPublication();
        _derealizingAllEmbeds = true;
        _realizationPassEpoch++;
        _realizeVisibleEmbedsRequested = false;
        _realizeVisibleEmbedsContinuationQueued = false;
        _realizeVisibleEmbedsContinuationTicket++;
        // Native callbacks can synchronously unload or replace the document.
        // Keep this teardown's ownership detached from the mutable plan lists;
        // nested cleanup is rejected above and each plan retires its element
        // before invoking callback-capable recycling code.
        EmbedPlan[] embedPlans = Array.Empty<EmbedPlan>();
        CodeBlockActionPlan[] actionPlans = Array.Empty<CodeBlockActionPlan>();
        try
        {
            embedPlans = _embedPlans.ToArray();
            actionPlans = _codeBlockActions.CapturePlans();
            // Previously pooled buttons may still be collapsed children of the
            // current overlay. The caller clears that overlay after this pass,
            // so discard those attached-state entries before pooling plans.
            _services.CopyButtons.Clear();
            foreach (EmbedPlan plan in embedPlans)
                plan.Derealize(Context);
            _codeBlockActions.DerealizeDetached(actionPlans);
        }
        finally
        {
            _derealizingAllEmbeds = false;
            _activeEmbedOrdinals.Clear();
            _codeBlockActions.ClearActiveOrdinals();
        }
    }

    /// <summary>Forgets every plan and its index. Realized elements must already be released.</summary>
    internal void ClearPlans()
    {
        _embedPlans.Clear();
        _embedPlanIndex = null;
        _activeEmbedOrdinals.Clear();
        _codeBlockActions.Clear();
    }

    /// <summary>Forgets pooled copy buttons before the overlay is discarded.</summary>
    internal void ClearCopyButtonPool() => _services.CopyButtons.Clear();

    /// <summary>
    /// Makes the next realization pass raise EmbedsRealizationChanged even
    /// when the count is unchanged, because plan identities changed.
    /// </summary>
    internal void ResetRealizedCountNotification() => _lastFiredRealizedCount = -1;

    /// <summary>
    /// Captures the current plans so a plan rebuild can adopt their realized
    /// elements: all of them when <paramref name="preserveRealized"/>, else none.
    /// </summary>
    internal void CapturePlansForAdoption(
        bool preserveRealized,
        out EmbedPlan[] oldPlans,
        out CodeBlockActionPlan[] oldActionPlans)
    {
        oldPlans = preserveRealized ? _embedPlans.ToArray() : Array.Empty<EmbedPlan>();
        oldActionPlans = preserveRealized ? _codeBlockActions.CapturePlans() : Array.Empty<CodeBlockActionPlan>();
    }

    /// <summary>
    /// Collects the plans of <paramref name="box"/> and its descendants.
    /// Images found along the way go to the host. Runs under the snapshot's
    /// paint lease, inside <paramref name="topLevelRoot"/>'s placement scope.
    /// </summary>
    /// <remarks>
    /// One walk with one branch per box kind. The branches share the walk's
    /// order and placement scope, which decide plan ordinals, so they stay
    /// together.
    /// </remarks>
    internal void CollectPlans(
        LayoutSnapshot snapshot,
        BlockBox topLevelRoot,
        BlockBox box)
    {
        switch (box)
        {
            case EmbedBox eb:
            {
                double left = eb.Bounds.X + eb.Margin.Left;
                double top  = eb.Bounds.Y + eb.Margin.Top;
                double w = eb.Bounds.Width  - eb.Margin.Left - eb.Margin.Right;
                double h = eb.Bounds.Height - eb.Margin.Top  - eb.Margin.Bottom;
                Rect physicalBounds = new(left, top, w, h);
                _embedPlans.Add(new BlockEmbedPlan
                {
                    Box = eb,
                    FocusIdentity = new FocusableItem(
                        eb.BlockIndex, 0, FocusableItemKind.BlockEmbed),
                    Rect = snapshot.ToLogicalRectForTopLevelBlockUnderPaint(topLevelRoot, physicalBounds),
                });
                break;
            }
            case DeclarativeHostedElementBox hosted when _host.HostedElementFactory is { } factory:
            {
                double left = hosted.Bounds.X + hosted.Margin.Left;
                double top = hosted.Bounds.Y + hosted.Margin.Top;
                double width = hosted.Bounds.Width - hosted.Margin.Left - hosted.Margin.Right;
                double height = hosted.Bounds.Height - hosted.Margin.Top - hosted.Margin.Bottom;
                _embedPlans.Add(new DeclarativeHostedElementPlan
                {
                    Box = hosted,
                    Factory = factory,
                    FocusIdentity = new FocusableItem(
                        hosted.BlockIndex, 0, FocusableItemKind.DeclarativeHostedElement),
                    Rect = snapshot.ToLogicalRectForTopLevelBlockUnderPaint(
                        topLevelRoot, new Rect(left, top, Math.Max(0, width), Math.Max(0, height))),
                });
                break;
            }
            case ImageBox ib:
            {
                _host.AddImagePlan(ib);
                break;
            }
            case InlineContainerBox icb:
            {
                // Most paragraphs contain only text. Avoid walking their runs
                // and querying DirectWrite geometry when no overlay/image
                // plan can possibly be produced.
                if (!icb.HasInlineImages && !icb.HasInlineEmbeds)
                    break;

                // Register completion before asking DirectWrite for rectangles. During the
                // first commit an inline image can be measurable but not yet have a stable
                // character region; paint will start it and completion must still reflow.
                if (icb.HasInlineImages)
                {
                    foreach (var (imageRun, _) in icb.InlineImageRuns)
                    {
                        // Image registration must not depend on DirectWrite
                        // returning a character rectangle for the object
                        // replacement slot. In particular, a linked image on
                        // an otherwise empty line can temporarily have no
                        // region during the first layout pass. Keeping it out
                        // of the image plans in that state leaves it permanently
                        // "Loading" because no later viewport pass can start
                        // it. A missing first-pass rectangle has the default
                        // origin, which deliberately starts the bounded load;
                        // EnumerateInlineImageRects synchronizes the real
                        // bounds as soon as DirectWrite publishes them.
                        _host.AddImagePlan(imageRun.Image);
                    }
                }
                if (icb.HasInlineEmbeds)
                {
                    foreach (var (run, rect) in icb.EnumerateEmbedRects())
                        _embedPlans.Add(new InlineEmbedPlan
                        {
                            Icb = icb,
                            Run = run,
                            FocusIdentity = new FocusableItem(
                                icb.BlockIndex, run.InlineIndex, FocusableItemKind.InlineEmbed),
                            Rect = snapshot.ToLogicalRectForTopLevelBlockUnderPaint(topLevelRoot, rect),
                        });
                }
                // Force enumeration so each ImageBox receives its document
                // bounds. Images were already added above, independently of
                // whether this geometry query succeeds.
                if (icb.HasInlineImages)
                {
                    foreach (var _ in icb.EnumerateInlineImageRects()) { }
                }
                break;
            }
            case CodeBlockBox codeBlock:
            {
                _codeBlockActions.Collect(snapshot, topLevelRoot, codeBlock);
                break;
            }
            case ListItemBox lib:
                CollectPlans(snapshot, topLevelRoot, lib.Marker);
                CollectPlans(snapshot, topLevelRoot, lib.Content);
                break;
            case TableBox tb:
                foreach (var cell in tb.GetCellBoxes()) CollectPlans(snapshot, topLevelRoot, cell);
                break;
            case StackBox sb:
                foreach (var c in sb.Children) CollectPlans(snapshot, topLevelRoot, c);
                break;
        }
    }

    /// <summary>Indexes the collected plans by vertical extent.</summary>
    internal void CommitPlanIndexes()
    {
        _embedPlanIndex = BuildPlanIndex(_embedPlans);
        _codeBlockActions.CommitIndex();
    }

    /// <summary>Forgets every hit rectangle.</summary>
    internal void ClearHitTargets() => HitTargets.Clear();

    /// <summary>Forgets hosted element hit rectangles, keeping copy buttons'.</summary>
    internal void ClearHostedElementHitTargets() => HitTargets.ClearHostedElements();

    /// <summary>
    /// Moves realized elements from <paramref name="oldPlans"/> and
    /// <paramref name="oldActionPlans"/> to the new plans for the same
    /// logical element, then marks the plans with active work. Returns false
    /// as soon as the publication of <paramref name="snapshot"/> stops being
    /// current; the caller retires what was not adopted.
    /// </summary>
    /// <remarks>
    /// Every transfer can call back into the host, so each is followed by
    /// the same publication check. Hosted elements are adopted before code
    /// block actions, and no plan is marked active until both passes finish.
    /// </remarks>
    internal bool AdoptRealizedElements(
        EmbedPlan[] oldPlans,
        CodeBlockActionPlan[] oldActionPlans,
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

        if (!_codeBlockActions.AdoptRealizedElements(
                oldActionPlans, snapshot, planGeneration, hostedFocusPlanEpoch, hostedFocusPlanRevision))
            return false;

        for (int index = 0; index < _embedPlans.Count; index++)
        {
            if (_embedPlans[index].HasActiveWork)
                _activeEmbedOrdinals.Add(index);
        }
        _codeBlockActions.MarkActiveOrdinals();

        _lastFiredRealizedCount = -1;
        return true;
    }

    /// <summary>
    /// Releases the elements still held by plans that a rebuild did not
    /// adopt. The arrays are detached, so native callbacks cannot
    /// invalidate them.
    /// </summary>
    internal void RetireDetachedPlans(EmbedPlan[] oldPlans, CodeBlockActionPlan[] oldActionPlans)
    {
        foreach (EmbedPlan oldPlan in oldPlans)
            oldPlan.Derealize(Context);
        _codeBlockActions.DerealizeDetached(oldActionPlans);
    }

    /// <summary>
    /// Takes over the code block action plans that a raster-candidate
    /// publication prepared off the published state.
    /// </summary>
    internal void AdoptCodeBlockActionPublication(
        List<CodeBlockActionPlan> plans,
        ViewportBandIndex? index,
        List<int> activeOrdinals,
        bool hasMeasuredCodeBlocks,
        List<(CodeBlockBox Box, Rect Rect, CodeBlockHostedElementKind Kind)> rects) =>
        _codeBlockActions.AdoptPublication(plans, index, activeOrdinals, hasMeasuredCodeBlocks, rects);

    /// <summary>
    /// Forgets the copy button at <paramref name="ordinal"/> after a raster
    /// candidate failed to adopt its button.
    /// </summary>
    internal void ForgetCodeBlockAction(int ordinal, CodeBlockBox box) =>
        _codeBlockActions.Forget(ordinal, box);

    /// <summary>Applies the current copy button labels to realized buttons.</summary>
    internal void UpdateCodeBlockCopyButtonLabels() => _codeBlockActions.UpdateCopyButtonLabels();

    /// <summary>Applies the current copy button style to realized buttons.</summary>
    internal void UpdateCodeBlockCopyButtonStyles(Style? style) =>
        _codeBlockActions.UpdateCopyButtonStyles(style);

    /// <summary>
    /// Realise embeds whose rect intersects the realisation band (viewport +
    /// overscan) and derealise embeds that have left the wider derealisation
    /// band. Called from snapshot commit, scroll, and resize.
    /// </summary>
    internal void RealizeVisible()
    {
        if (_lifetime.IsDisposed || _lifetime.IsUnloaded || _derealizingAllEmbeds)
            return;

        // Plan factories and native child teardown can synchronously raise
        // layout/scroll events. Coalesce those nested requests instead of
        // allowing them to mutate the active ordinal lists being traversed.
        if (_realizingVisibleEmbeds)
        {
            _realizeVisibleEmbedsRequested = true;
            return;
        }

        _realizingVisibleEmbeds = true;
        bool queueContinuation = false;
        try
        {
            for (int pass = 0; pass < 2; pass++)
            {
                _realizeVisibleEmbedsRequested = false;
                if (_lifetime.IsDisposed || _lifetime.IsUnloaded || _derealizingAllEmbeds)
                    break;

                RealizeVisibleCore();
                if (!_realizeVisibleEmbedsRequested)
                    break;
            }
            queueContinuation = _realizeVisibleEmbedsRequested &&
                !_lifetime.IsDisposed && !_lifetime.IsUnloaded && !_derealizingAllEmbeds;
        }
        finally
        {
            _realizingVisibleEmbeds = false;
        }

        if (queueContinuation)
            QueueVisibleRealizationContinuation();
    }

    /// <summary>
    /// True while nothing <paramref name="pass"/> started from has changed:
    /// the control is live, no teardown is running, and the snapshot, its
    /// generation, the pass epoch and every plan index are the same.
    /// </summary>
    internal bool IsCurrent(in VisibleRealizationPass pass) =>
        !_lifetime.IsDisposed && !_lifetime.IsUnloaded && !_derealizingAllEmbeds &&
        pass.SnapshotGeneration == _published.Generation &&
        pass.RealizationPassEpoch == _realizationPassEpoch &&
        ReferenceEquals(pass.Snapshot, _published.Snapshot) &&
        ReferenceEquals(pass.ImageIndex, _host.ImagePlanIndex) &&
        ReferenceEquals(pass.EmbedIndex, _embedPlanIndex) &&
        ReferenceEquals(pass.ActionIndex, _codeBlockActions.Index);

    /// <summary>
    /// Counts realized copy buttons, and how many of them lie outside the
    /// viewport.
    /// </summary>
    internal void GetCodeBlockActionRealizationCounts(out int realizedCount, out int offscreenCount) =>
        _codeBlockActions.GetRealizationCounts(out realizedCount, out offscreenCount);

    /// <summary>Indexes <paramref name="plans"/> by vertical extent, or returns null when empty.</summary>
    internal static ViewportBandIndex? BuildPlanIndex(List<CodeBlockActionPlan> plans)
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

    /// <summary>Inserts <paramref name="ordinal"/> into the sorted <paramref name="ordinals"/> unless present.</summary>
    internal static void AddActiveOrdinal(List<int> ordinals, int ordinal)
    {
        int position = ordinals.BinarySearch(ordinal);
        if (position < 0)
            ordinals.Insert(~position, ordinal);
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

    private void QueueVisibleRealizationContinuation()
    {
        if (_realizeVisibleEmbedsContinuationQueued ||
            _lifetime.IsDisposed || _lifetime.IsUnloaded || _derealizingAllEmbeds)
            return;

        var dispatcher = _dispatcher;
        if (dispatcher is null)
            return;

        long ticket = ++_realizeVisibleEmbedsContinuationTicket;
        if (ticket == 0)
            ticket = ++_realizeVisibleEmbedsContinuationTicket;
        var receiver = new WeakReference<EmbedRealization>(this);
        try
        {
            if (dispatcher.TryEnqueue(
                    DispatcherQueuePriority.Low,
                    () =>
                    {
                        if (receiver.TryGetTarget(out var realization))
                            realization.ProcessQueuedVisibleRealizationContinuation(ticket);
                    }))
            {
                _realizeVisibleEmbedsContinuationQueued = true;
            }
        }
        catch (Exception exception)
        {
            MarkdownDiagnostics.WriteLine(
                $"[MarkdownRendererControl] visible realization continuation dispatch failed: {exception.GetType().Name} " +
                $"(0x{exception.HResult:X8}).");
        }
    }

    private void ProcessQueuedVisibleRealizationContinuation(long ticket)
    {
        if (ticket != _realizeVisibleEmbedsContinuationTicket)
            return;

        _realizeVisibleEmbedsContinuationQueued = false;
        if (_lifetime.IsDisposed || _lifetime.IsUnloaded || _derealizingAllEmbeds)
        {
            _realizeVisibleEmbedsRequested = false;
            return;
        }

        RealizeVisible();
    }

    /// <remarks>
    /// Kept as one method, over 80 lines, because images, copy buttons and
    /// hosted elements are realized in a fixed order against one captured
    /// <see cref="VisibleRealizationPass"/>; every step that can reenter is
    /// followed by the same currency check.
    /// </remarks>
    private void RealizeVisibleCore()
    {
        LayoutSnapshot? snapshot = _published.Snapshot;
        if (snapshot is null)
            return;

        var pass = new VisibleRealizationPass(
            snapshot,
            _published.Generation,
            _realizationPassEpoch,
            _host.ImagePlanIndex,
            _embedPlanIndex,
            _codeBlockActions.Index);
        if (!IsCurrent(pass))
            return;

        _viewport.TryGet(out double top, out double viewportHeight, out _);
        double bottom = top + viewportHeight;

        if (!_host.RealizeVisibleImages(pass, top, viewportHeight, bottom))
            return;

        // Drop old realisation-side caches; they'll be repopulated from realised plans.
        HitTargets.Clear();

        if (!_codeBlockActions.RealizeVisible(pass, top, bottom))
            return;

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
                plan.Realize(Context);
                if (!IsCurrent(pass))
                    return;
                if (plan.HasActiveWork)
                    AddActiveOrdinal(_activeEmbedOrdinals, ordinal);
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
            plan.Derealize(Context);
            if (!IsCurrent(pass))
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
                HitTargets.AddBlock(bp.Box, new Rect(left, t, w, h));
            }
            else if (plan is InlineEmbedPlan ip)
            {
                double iLeft = Math.Round(ip.Rect.X);
                double iTop  = Math.Round(ip.Rect.Y);
                double iW    = Math.Round(ip.Rect.X + ip.Rect.Width)  - iLeft;
                double iH    = Math.Round(ip.Rect.Y + ip.Rect.Height) - iTop;
                HitTargets.AddInline(ip.Icb, ip.Run, new Rect(iLeft, iTop, iW, iH));
            }
            else if (plan is DeclarativeHostedElementPlan declarative)
            {
                HitTargets.AddBlock(declarative.Box, declarative.Rect);
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
            if (!IsCurrent(pass))
                return;
            _host.RaiseEmbedsRealizationChanged();
        }
    }
}
