using System;
using System.Collections.Generic;
using MarkdownRenderer.Controls.Internal.Embeds;
using MarkdownRenderer.Controls.Internal.Focus;
using MarkdownRenderer.Controls.Internal.Focus.Hosted;
using MarkdownRenderer.Controls.Internal.Images;
using MarkdownRenderer.Controls.Internal.Input;
using MarkdownRenderer.Controls.Internal.LazyLayout;
using MarkdownRenderer.Controls.Internal.Recovery;
using MarkdownRenderer.Controls.Internal.View;
using MarkdownRenderer.Layout.Internal.Boxes;
using MarkdownRenderer.Layout.Internal.Focus;
using MarkdownRenderer.Layout.Internal.Placement;
using MarkdownRenderer.Layout.Internal.Snapshots;
using Windows.Foundation;

namespace MarkdownRenderer.Controls.Internal.Pipeline.Commit;

/// <summary>
/// Publishes layout commits of the published snapshot: snapshot replacement,
/// lazy layout and image relayout all rebuild the embed, image and focus
/// realization plans here, then apply the size, scroll anchor and refreshes
/// that follow. One commit publishes at a time; others defer.
/// </summary>
internal sealed class LayoutCommitPublisher
{
    private readonly ILayoutCommitHost _host;
    private readonly MarkdownViewLifetime _lifetime;
    private readonly PublishedLayout _published;
    private readonly LayoutCommitState _layoutCommit;
    private readonly MarkdownViewSurface _surface;
    private readonly RecoveryClock _recoveryClock;
    private readonly PointerInputRouter _pointerInput;
    private readonly EmbedRealization _embeds;
    private readonly ImageSubscriptions _imageSubscriptions;
    private readonly ImagePlanSet _imagePlans;
    private readonly ImageResidency _imageResidency;
    private readonly KeyboardFocusModel _focus;
    private readonly FocusRingPresenter _focusRing;
    private readonly FocusedItemCommit _focusCommit;
    private readonly FocusableItemsRefreshRequest _focusRefresh;
    private readonly HostedFocusPlanPublication _hostedFocusPlan;

    internal LayoutCommitPublisher(
        ILayoutCommitHost host,
        MarkdownViewLifetime lifetime,
        PublishedLayout published,
        LayoutCommitState layoutCommit,
        MarkdownViewSurface surface,
        RecoveryClock recoveryClock,
        PointerInputRouter pointerInput,
        EmbedRealization embeds,
        ImageSubscriptions imageSubscriptions,
        ImagePlanSet imagePlans,
        ImageResidency imageResidency,
        KeyboardFocusModel focus,
        FocusRingPresenter focusRing,
        FocusedItemCommit focusCommit,
        FocusableItemsRefreshRequest focusRefresh,
        HostedFocusPlanPublication hostedFocusPlan)
    {
        _host = host;
        _lifetime = lifetime;
        _published = published;
        _layoutCommit = layoutCommit;
        _surface = surface;
        _recoveryClock = recoveryClock;
        _pointerInput = pointerInput;
        _embeds = embeds;
        _imageSubscriptions = imageSubscriptions;
        _imagePlans = imagePlans;
        _imageResidency = imageResidency;
        _focus = focus;
        _focusRing = focusRing;
        _focusCommit = focusCommit;
        _focusRefresh = focusRefresh;
        _hostedFocusPlan = hostedFocusPlan;
    }

    /// <summary>
    /// Publishes the realization plans of the current layout revision of
    /// <paramref name="snapshot"/>, restoring <paramref name="scrollAnchor"/>
    /// when given. A commit that arrives while another publishes, or whose
    /// geometry read is not admitted yet, is deferred to the recovery clock.
    /// </summary>
    internal void Apply(
        LayoutSnapshot snapshot,
        (int BlockIndex, double OffsetFromTop)? scrollAnchor)
    {
        if (_lifetime.IsDisposed || _lifetime.IsUnloaded || !ReferenceEquals(snapshot, _published.Snapshot))
            return;
        if (_host.RequestGeometryRecoveryIfRequired(snapshot))
            return;
        if (_layoutCommit.IsPublishing)
        {
            Defer(scrollAnchor);
            return;
        }
        _layoutCommit.BeginPublishing();
        try
        {
            _layoutCommit.ClearPending();
            if (!TryRebuildPlans(snapshot, preserveRealized: true, out Size size,
                    out double? anchorOffset, scrollAnchor))
            {
                Defer(scrollAnchor);
                return;
            }
            if (_lifetime.IsDisposed || _lifetime.IsUnloaded || !ReferenceEquals(snapshot, _published.Snapshot))
                return;
            _host.ApplySnapshotSize(size);
            if (anchorOffset is { } target && _surface.Scroll is { } scroll &&
                Math.Abs(target - scroll.VerticalOffset) >= 0.5)
                scroll.ChangeView(null, target, null, disableAnimation: true);
            _embeds.RealizeVisible();
            _host.ScheduleVisibleCodeBlockHighlighting();
            if (!_focusCommit.ResumePendingLazy(snapshot))
                _focusRing.Update();
            _host.RefreshInteractiveTextAdornerAfterLayoutChange();
            _host.InvalidateAutomationLayout();
            _host.InvalidateCanvas();
        }
        finally
        {
            _layoutCommit.EndPublishing();
        }

        _host.TryResumePendingScrollToBlock(snapshot, _published.Generation);
        if (!_lifetime.IsDisposed && !_lifetime.IsUnloaded &&
            ReferenceEquals(snapshot, _published.Snapshot) &&
            _layoutCommit.Pending is null &&
            _layoutCommit.AppliedRevision >= snapshot.LayoutRevision)
        {
            // Release logical input waiters only after plans/anchor are applied,
            // not when a worker first updates the snapshot's measured mask.
            _pointerInput.OnLazyLayoutCompletion(snapshot, succeeded: true);
        }
    }

    /// <summary>
    /// Defers a commit of the published snapshot, keeping only value
    /// metadata, and starts the recovery clock to retry it.
    /// </summary>
    internal void Defer((int BlockIndex, double OffsetFromTop)? scrollAnchor)
    {
        if (_lifetime.IsDisposed || _lifetime.IsUnloaded || _published.Snapshot is null)
            return;
        // Retain only value metadata, never a retired graph. Keep old plans and
        // native children until one coherent geometry read is admitted. The
        // existing weak recovery timer also covers a withheld UI dispatch.
        _layoutCommit.Defer(new PendingLayoutCommit(
            _published.Generation, scrollAnchor, _surface.Scroll?.VerticalOffset));
        _recoveryClock.Start();
        if (!_recoveryClock.IsRunning)
            _host.RequestFullRebuild();
    }

    /// <summary>Retries the deferred commit, if it still belongs to the published generation.</summary>
    internal void PublishPending()
    {
        if (_layoutCommit.Pending is not { } pending)
            return;
        if (pending.Generation != _published.Generation || _published.Snapshot is not { } snapshot)
        {
            _layoutCommit.ClearPending();
            return;
        }
        bool restoreAnchor = LazyLayoutScrollAnchorPolicy.ShouldRestore(
            pending.ScrollOffset, _surface.Scroll?.VerticalOffset, _host.IsScrollViewIntermediate);
        Apply(snapshot, restoreAnchor ? pending.Anchor : null);
    }

    /// <summary>
    /// Rebuilds the embed, image and focus plans of <paramref name="snapshot"/>
    /// under one geometry lease, adopting realized elements when
    /// <paramref name="preserveRealized"/> is set. Returns false, leaving the
    /// previous publication in place, when the lease is unavailable or any
    /// currency fence fails.
    /// </summary>
    /// <remarks>
    /// This method stays whole because it is one lease protocol: the focus
    /// capture, its currency fence, the plan clear, the candidate collection
    /// and the image index run under one snapshot lease, the capture failure
    /// is rethrown only after that lease ends, and the outer finally that
    /// retires detached plans must cover every step from capture to adoption.
    /// </remarks>
    internal bool TryRebuildPlans(LayoutSnapshot snapshot, bool preserveRealized,
        out Size size, out double? anchorOffset,
        (int BlockIndex, double OffsetFromTop)? scrollAnchor = null)
    {
        size = default;
        anchorOffset = null;
        long planGeneration = _published.Generation;
        if (!snapshot.TryBeginInteraction())
            return false;
        long hostedFocusPlanEpoch = -1;
        long hostedFocusPlanRevision = -1;
        long focusRequestVersion = _focusRefresh.Pending.RequestVersion;
        IReadOnlyList<FocusableItem> collectedFocusableItems =
            Array.Empty<FocusableItem>();
        ImagePlanPublication? imagePlanPublication = null;
        EmbedPlan[] oldPlans = Array.Empty<EmbedPlan>();
        CodeBlockActionPlan[] oldActionPlans = Array.Empty<CodeBlockActionPlan>();
        bool plansBuilt = false;
        Exception? focusPlanCaptureFailure = null;
        try
        {
            try
            {
                // Collect before mutating realization plans. Dynamic overflow
                // eligibility is read only while this exact snapshot lease is
                // held, and a failed capture leaves the previous focus list and
                // plan state intact.
                bool focusPlanCaptured;
                try
                {
                    focusPlanCaptured = snapshot.TryCollectFocusableItemsUnderReadLease(
                        out collectedFocusableItems);
                }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    // A custom overflow getter failed before any realization
                    // plan is touched. Let the lease unwind before reporting
                    // it or clearing the matching pending ticket.
                    focusPlanCaptureFailure = exception;
                    focusPlanCaptured = false;
                }
                if (focusPlanCaptureFailure is null)
                {
                    if (!focusPlanCaptured)
                        return false;

                    hostedFocusPlanRevision = snapshot.LayoutRevision;
                    if (!IsCurrentFocusablePlanAttempt(
                            snapshot,
                            planGeneration,
                            hostedFocusPlanRevision,
                            focusRequestVersion))
                    {
                        return false;
                    }

                    hostedFocusPlanEpoch = _hostedFocusPlan.Invalidate();
                    _embeds.CapturePlansForAdoption(preserveRealized, out oldPlans, out oldActionPlans);
                    _imageSubscriptions.UnsubscribeAll();
                    _imagePlans.Clear();
                    _embeds.ClearHitTargets();
                    _embeds.ClearPlans();

                    LayoutPlanCandidateEnumerator planCandidates =
                        snapshot.GetMeasuredLayoutPlanCandidatesUnderPaint();
                    int candidateRootOrdinal = -1;
                    BlockBox? candidateRoot = null;
                    bool candidateRootIsMeasured = false;
                    while (planCandidates.MoveNext())
                    {
                        LayoutPlanCandidate candidate = planCandidates.Current;
                        if (candidate.TopLevelOrdinal != candidateRootOrdinal)
                        {
                            candidateRootOrdinal = candidate.TopLevelOrdinal;
                            candidateRootIsMeasured = snapshot.TryGetMeasuredBlockByOrdinalUnderPaint(
                                candidateRootOrdinal,
                                out BlockBox measuredRoot);
                            candidateRoot = candidateRootIsMeasured ? measuredRoot : null;
                        }

                        if (candidateRootIsMeasured)
                        {
                            using LayoutPlacementTransformContext.Scope placementScope =
                                snapshot.EnterPlacementTransformScopeForTopLevelBlockUnderPaint(candidateRoot!);
                            _embeds.CollectPlans(snapshot, candidateRoot!, candidate.Box);
                        }
                    }

                    _embeds.CommitPlanIndexes();

                    // Membership and indexed bounds remain immutable until the next
                    // plan publication. The ImageBox itself is mutable and borrowed.
                    imagePlanPublication = _imagePlans.IndexUnderPaint(
                        snapshot, planGeneration, hostedFocusPlanRevision);
                    size = snapshot.Size;
                    anchorOffset = LazyLayoutScrollAnchorPolicy.GetTargetOffset(snapshot, scrollAnchor);
                    _layoutCommit.MarkApplied(hostedFocusPlanRevision);
                    imagePlanPublication ??= ImagePlanSet.Empty(
                        snapshot, planGeneration, _layoutCommit.AppliedRevision);
                }
            }
            finally
            {
                snapshot.EndInteraction();
            }

            if (focusPlanCaptureFailure is not null)
            {
                _focusRefresh.Clear(focusRequestVersion);
                System.Runtime.ExceptionServices.ExceptionDispatchInfo
                    .Capture(focusPlanCaptureFailure).Throw();
            }
            if (!IsCurrentPlanPublication(snapshot, planGeneration, hostedFocusPlanEpoch, hostedFocusPlanRevision))
                return false;
            if (imagePlanPublication is null)
                return false;

            _imagePlans.Publication = imagePlanPublication;

            // Native child transfer, factories, residency policies and XAML
            // notifications can reenter the control. Do not invoke them under the
            // geometry lease; every plan rectangle above is already copied.
            _imageResidency.Prepare(snapshot);

            if (!_embeds.AdoptRealizedElements(
                    oldPlans, oldActionPlans, snapshot, planGeneration,
                    hostedFocusPlanEpoch, hostedFocusPlanRevision))
                return false;

            plansBuilt = true;
        }
        finally
        {
            // Even reentrant unload/replacement or failed geometry collection
            // must retire the old plans not transferred to the current set.
            // These detached arrays are not invalidated by native callbacks.
            _embeds.RetireDetachedPlans(oldPlans, oldActionPlans);
        }

        // Retiring detached plans is callback-capable. Publish the focus index
        // only after that cleanup completes and the complete plan attempt still
        // owns the same snapshot, generation, layout revision, and epoch.
        if (!plansBuilt ||
            !IsCurrentPlanPublication(snapshot, planGeneration, hostedFocusPlanEpoch, hostedFocusPlanRevision) ||
            !IsCurrentFocusablePlanAttempt(
                snapshot,
                planGeneration,
                hostedFocusPlanRevision,
                focusRequestVersion))
        {
            return false;
        }

        // Host, native-resource, and retirement callbacks above may navigate
        // focus while realization plans are being published. Resolve the
        // currently focused value identity only now, then atomically replace
        // the value-only list/index after every snapshot and ticket fence holds.
        _focus.ReplaceItems(collectedFocusableItems);
        _hostedFocusPlan.Publish(planGeneration, hostedFocusPlanRevision, hostedFocusPlanEpoch);
        _focusRefresh.Complete(
            snapshot,
            planGeneration,
            hostedFocusPlanRevision,
            focusRequestVersion);
        return true;
    }

    /// <summary>
    /// True while a plan publication still owns the published snapshot,
    /// generation, hosted focus plan epoch and layout revision.
    /// </summary>
    internal bool IsCurrentPlanPublication(
        LayoutSnapshot snapshot,
        long generation,
        long hostedFocusPlanEpoch,
        long layoutRevision) =>
        IsCurrentRealizationSnapshot(snapshot, generation) &&
        hostedFocusPlanEpoch == _hostedFocusPlan.Epoch &&
        snapshot.LayoutRevision == layoutRevision;

    private bool IsCurrentRealizationSnapshot(LayoutSnapshot snapshot, long generation) =>
        !_lifetime.IsDisposed && !_lifetime.IsUnloaded && generation == _published.Generation &&
        ReferenceEquals(snapshot, _published.Snapshot);

    private bool IsCurrentFocusablePlanAttempt(
        LayoutSnapshot snapshot,
        long generation,
        long revision,
        long requestVersion)
    {
        if (!IsCurrentRealizationSnapshot(snapshot, generation) ||
            snapshot.LayoutRevision != revision)
        {
            return false;
        }

        return _focusRefresh.Admits(snapshot, generation, revision, requestVersion);
    }
}
