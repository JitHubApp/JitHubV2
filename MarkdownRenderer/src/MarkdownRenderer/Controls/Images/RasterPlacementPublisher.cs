using System;
using System.Collections.Generic;
using MarkdownRenderer.Controls.Embeds;
using MarkdownRenderer.Controls.Focus;
using MarkdownRenderer.Controls.Input;
using MarkdownRenderer.Controls.Pipeline;
using MarkdownRenderer.Diagnostics.Internal;
using MarkdownRenderer.Images;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using Windows.Foundation;

namespace MarkdownRenderer.Controls.Images;

/// <summary>
/// Publishes a raster placement prepared off the UI thread. One synchronous
/// fence checks every input the candidate was prepared from, then one swap
/// adopts its subscriptions, image plans, focus items and code block actions
/// and transfers the candidate snapshot to the control.
/// </summary>
internal sealed class RasterPlacementPublisher
{
    private readonly IRasterPlacementHost _host;
    private readonly MarkdownViewLifetime _lifetime;
    private readonly PublishedLayout _published;
    private readonly LayoutCommitState _layoutCommit;
    private readonly MarkdownViewSurface _surface;
    private readonly PointerInputRouter _pointerInput;
    private readonly ImageSubscriptions _imageSubscriptions;
    private readonly ImagePlanSet _imagePlans;
    private readonly ImageResidency _imageResidency;
    private readonly EmbedRealization _embeds;
    private readonly KeyboardFocusModel _focus;
    private readonly FocusRingPresenter _focusRing;
    private readonly HostedFocusPlanPublication _hostedFocusPlan;
    private readonly SnapshotRetirementTracker _retirements;

    internal RasterPlacementPublisher(
        IRasterPlacementHost host,
        MarkdownViewLifetime lifetime,
        PublishedLayout published,
        LayoutCommitState layoutCommit,
        MarkdownViewSurface surface,
        PointerInputRouter pointerInput,
        ImageSubscriptions imageSubscriptions,
        ImagePlanSet imagePlans,
        ImageResidency imageResidency,
        EmbedRealization embeds,
        KeyboardFocusModel focus,
        FocusRingPresenter focusRing,
        HostedFocusPlanPublication hostedFocusPlan,
        SnapshotRetirementTracker retirements)
    {
        _host = host;
        _lifetime = lifetime;
        _published = published;
        _layoutCommit = layoutCommit;
        _surface = surface;
        _pointerInput = pointerInput;
        _imageSubscriptions = imageSubscriptions;
        _imagePlans = imagePlans;
        _imageResidency = imageResidency;
        _embeds = embeds;
        _focus = focus;
        _focusRing = focusRing;
        _hostedFocusPlan = hostedFocusPlan;
        _retirements = retirements;
    }

    /// <summary>
    /// Captures the inputs for a private raster placement of
    /// <paramref name="snapshot"/>, or returns null when the snapshot is not
    /// eligible: no performance session opted in, too many roots or images,
    /// or hosted embeds that a private placement cannot carry.
    /// </summary>
    internal RasterPlacementInputs? TryCreateInputs(LayoutSnapshot snapshot, bool optedIn) =>
        RasterPlacementEligibility.Allows(optedIn, snapshot.TopLevelBlockCount, _embeds.EmbedPlans.Count, _imagePlans.Count)
            ? new RasterPlacementInputs(new WeakReference<RasterPlacementPublisher>(this), _published.Generation,
                _host.ImagePreparationOwner, _host.ImageLifetime,
                _retirements, _surface.Scroll?.VerticalOffset)
            : null;

    /// <summary>
    /// Raises an image-unavailable notice from a private candidate only while
    /// that candidate's generation and snapshot are still published.
    /// </summary>
    internal void RaiseImageUnavailableIfCurrent(
        LayoutSnapshot candidate,
        long generation,
        string url,
        MarkdownImageUnavailableReason reason,
        MarkdownSvgFailureReason? svgReason)
    {
        if (!_lifetime.IsDisposed && !_lifetime.IsUnloaded && generation == _published.Generation &&
            ReferenceEquals(candidate, _published.Snapshot))
            _host.RaiseImageUnavailable(url, reason, svgReason);
    }

    /// <summary>
    /// Publishes <paramref name="plan"/> in place of <paramref name="source"/>
    /// when every input it was prepared from is still current. Returns false,
    /// changing nothing, when any fence fails.
    /// </summary>
    /// <remarks>
    /// The steps live in their own methods; this skeleton stays whole because its nested
    /// try/finally must enclose all of them so the source retires once and publishing ends.
    /// </remarks>
    internal bool TryPublish(LayoutSnapshot source, RasterPlacementPlan plan, long generation)
    {
        SingleRasterLayoutCandidate owner = plan.Owner;
        LayoutSnapshot candidate = owner.Snapshot;
        MarkdownLayoutContext context = owner.Context;
        // An SVG provider can implement its generation getter with host code.
        // Query it before, not within/after, the local UI fence below. Reentrant
        // unload, document replacement or policy changes must then fail that
        // complete fence before any subscriptions or snapshot ownership change.
        if (!owner.HasCurrentSourceProviders())
            return false;
        // UI-dispatcher-only, synchronous fence and swap. No host/XAML/native
        // callbacks intervene. Image publication readers verify snapshot identity.
        if (HasStalePublicationInputs(source, owner, candidate, context, generation))
            return false;

        if (!TryPrepareCodeBlockActionPublication(plan, out IReadOnlyList<CodeBlockActionPlan> oldCodeActionPlans))
            return false;

        Layout.FocusableItem? focusedIdentity = _focus.Current;
        Layout.FocusableItem? resumeFocusIdentity =
            _focus.Items is { } currentItems &&
            _focus.ResumeIndex >= 0 && _focus.ResumeIndex < currentItems.Count
                ? currentItems[_focus.ResumeIndex]
                : null;
        plan.ResumeFocusIdentity = resumeFocusIdentity;

        _layoutCommit.BeginPublishing();
        bool sourceRetirementRequested = false;
        bool committed = false;
        try
        {
            long epoch = CommitCandidate(
                plan, owner, candidate, generation, focusedIdentity, resumeFocusIdentity);
            committed = true;

            try
            {
                // Ask the old tree to retire before any callback-capable code
                // button localization/highlighting or UI publication work.
                _host.RetireSnapshot(source);
                sourceRetirementRequested = true;

                if (ReferenceEquals(_pointerInput.DeferredSnapshot, source))
                {
                    // A logical geometry waiter belongs to the exact snapshot
                    // whose hit-test produced it. Candidate publication must
                    // cancel it, never silently retarget it to a sibling tree.
                    // Capture release is callback-capable, so perform it only
                    // after ownership has committed and the old tree is retired.
                    ResetSourceInputWaiter();

                    if (!IsCurrentPublication(
                            candidate, generation, candidate.LayoutRevision, epoch))
                        return true;
                }

                AdoptCodeBlockActions(plan, candidate, generation, epoch);

                // Highlight tasks are snapshot-bound, so their old callbacks
                // cannot mutate this candidate. Re-run the normal visible path
                // after publishing the fresh code placements.
                if (IsCurrentPublication(
                        candidate, generation, candidate.LayoutRevision, epoch))
                    _host.ScheduleVisibleCodeBlockHighlighting();
            }
            finally
            {
                DerealizeRetiredCodeBlockActions(oldCodeActionPlans);
            }

            PublishCandidateUi(plan, candidate, generation, epoch);
            return true;
        }
        finally
        {
            try
            {
                if (committed && !sourceRetirementRequested)
                    _host.RetireSnapshot(source);
            }
            finally
            {
                _layoutCommit.EndPublishing();
            }
        }
    }

    private bool HasStalePublicationInputs(
        LayoutSnapshot source,
        SingleRasterLayoutCandidate owner,
        LayoutSnapshot candidate,
        MarkdownLayoutContext context,
        long generation) =>
        _lifetime.IsDisposed || _lifetime.IsUnloaded || _layoutCommit.IsPublishing || _host.HasCompetingLayoutWork ||
            _pointerInput.HasSnapshotInteraction ||
            !ReferenceEquals(source, _published.Snapshot) || generation != _published.Generation ||
            source.IsRetiringOrDisposed || source.LayoutRevision != owner.SourceRevision ||
            _host.LayoutWidth != owner.Width ||
            !_host.MatchesLayoutInputs(context) ||
            _embeds.EmbedPlans.Count != 0 ||
            !owner.MatchesSourceImages(_imagePlans.Items, _imageSubscriptions.Images) ||
            (_pointerInput.DeferredSnapshot is not null && !ReferenceEquals(_pointerInput.DeferredSnapshot, source)) ||
            !owner.HasCurrentSourceRaster() ||
            !ReferenceEquals(source.SourceMap, candidate.SourceMap) ||
            source.AutomationDocumentGeneration != candidate.AutomationDocumentGeneration;

    private long CommitCandidate(
        RasterPlacementPlan plan,
        SingleRasterLayoutCandidate owner,
        LayoutSnapshot candidate,
        long generation,
        Layout.FocusableItem? focusedIdentity,
        Layout.FocusableItem? resumeFocusIdentity)
    {
        _imageSubscriptions.Replace(plan.Subscriptions!);
        _imagePlans.Adopt(plan.Images!, plan.Publication!.Index);
        _focus.ReplaceItems(plan.Focus, focusedIdentity, resumeFocusIdentity);
        _embeds.AdoptCodeBlockActionPublication(
            plan.CodeActionPlans!,
            plan.CodeActionIndex,
            plan.ActiveCodeActionOrdinals!,
            plan.HasMeasuredCodeBlocks,
            plan.CodeActionRects!);
        plan.CodeActionPlans = null;
        plan.CodeActionIndex = null;
        plan.ActiveCodeActionOrdinals = null;
        plan.CodeActionRects = null;
        _layoutCommit.MarkApplied(candidate.LayoutRevision);
        _layoutCommit.ClearPending();
        _host.ForgetHoverTarget();
        long epoch = _hostedFocusPlan.Invalidate();
        _published.Snapshot = owner.TransferToControl();
        _imagePlans.Publication = plan.Publication;
        _hostedFocusPlan.Publish(generation, candidate.LayoutRevision, epoch);
        return epoch;
    }

    private void ResetSourceInputWaiter()
    {
        try
        {
            _pointerInput.Reset();
        }
        catch (Exception exception)
        {
            MarkdownDiagnostics.WriteLine(
                $"[MarkdownRendererControl] Candidate input-waiter cleanup failed: {exception.GetType().Name}.");
        }
    }

    private void AdoptCodeBlockActions(
        RasterPlacementPlan plan,
        LayoutSnapshot candidate,
        long generation,
        long epoch)
    {
        foreach (var adoption in plan.CodeActionAdoptions!)
        {
            if (!IsCurrentPublication(
                    candidate, generation, candidate.LayoutRevision, epoch))
                return;

            bool adopted;
            try
            {
                adopted = adoption.NewPlan.AdoptRealizedFromRasterCandidate(
                    adoption.OldPlan, _embeds.Context, candidate, generation, candidate.LayoutRevision, epoch);
            }
            catch (Exception exception)
            {
                adopted = false;
                MarkdownDiagnostics.WriteLine(
                    $"[MarkdownRendererControl] Candidate code-action adoption failed: {exception.GetType().Name}.");
            }

            if (!IsCurrentPublication(
                    candidate, generation, candidate.LayoutRevision, epoch))
                return;

            if (adopted && adoption.NewPlan.Realized is { } adoptedElement &&
                !adoption.NewPlan.IsCurrentRasterActionOwner(
                    _embeds.Context.Host, candidate, generation, candidate.LayoutRevision, epoch, adoptedElement))
                return;

            if (!adopted && IsCurrentPublication(
                    candidate, generation, candidate.LayoutRevision, epoch))
            {
                if (adoption.NewPlan.Realized is { } failedElement &&
                    ReferenceEquals(adoption.NewPlan.Box.RealizedCopyButton, failedElement))
                {
                    try
                    {
                        adoption.NewPlan.Derealize(_embeds.Context);
                    }
                    catch (Exception exception)
                    {
                        MarkdownDiagnostics.WriteLine(
                            $"[MarkdownRendererControl] Candidate code-action cleanup failed: {exception.GetType().Name}.");
                    }
                }
                if (!IsCurrentPublication(
                        candidate, generation, candidate.LayoutRevision, epoch))
                    return;
                _embeds.ForgetCodeBlockAction(adoption.NewOrdinal, adoption.NewPlan.Box);
            }
        }
    }

    private void DerealizeRetiredCodeBlockActions(IReadOnlyList<CodeBlockActionPlan> oldCodeActionPlans)
    {
        foreach (CodeBlockActionPlan oldPlan in oldCodeActionPlans)
        {
            if (oldPlan.Realized is not null)
            {
                try
                {
                    oldPlan.Derealize(_embeds.Context);
                }
                catch (Exception exception)
                {
                    MarkdownDiagnostics.WriteLine(
                        $"[MarkdownRendererControl] Retired code-action cleanup failed: {exception.GetType().Name}.");
                }
            }
        }
    }

    private void PublishCandidateUi(
        RasterPlacementPlan plan,
        LayoutSnapshot candidate,
        long generation,
        long epoch)
    {
        if (!IsCurrentPublication(
                candidate, generation, candidate.LayoutRevision, epoch))
            return;
        _host.NotifyAutomationSnapshotChanged();
        if (!IsCurrentPublication(
                candidate, generation, candidate.LayoutRevision, epoch))
            return;
        _host.HideAbbreviationTooltip();
        if (!IsCurrentPublication(
                candidate, generation, candidate.LayoutRevision, epoch))
            return;
        _host.ResetCursor();
        if (!IsCurrentPublication(
                candidate, generation, candidate.LayoutRevision, epoch))
            return;
        _host.ApplySnapshotSize(candidate.Size);
        if (!IsCurrentPublication(
                candidate, generation, candidate.LayoutRevision, epoch))
            return;
        if (plan.AnchorOffset is { } target && _surface.Scroll is { } scroll &&
            LazyLayoutScrollAnchorPolicy.ShouldRestore(plan.CapturedScrollOffset, scroll.VerticalOffset, _host.IsScrollViewIntermediate) &&
            Math.Abs(target - scroll.VerticalOffset) >= .5)
            scroll.ChangeView(null, target, null, disableAnimation: true);
        if (!IsCurrentPublication(
                candidate, generation, candidate.LayoutRevision, epoch))
            return;
        _imageResidency.Prepare(candidate);
        if (!IsCurrentPublication(
                candidate, generation, candidate.LayoutRevision, epoch))
            return;
        // Image geometry can move code actions into or out of the current
        // viewport. Reconcile the new index now, without waiting for a
        // subsequent scroll to realize newly visible Copy buttons or trim
        // constructor-acquired offscreen image leases.
        _embeds.RealizeVisible();
        if (!IsCurrentPublication(
                candidate, generation, candidate.LayoutRevision, epoch))
            return;
        _focusRing.Update();
        if (!IsCurrentPublication(
                candidate, generation, candidate.LayoutRevision, epoch))
            return;
        _host.RefreshInteractiveTextAdornerAfterLayoutChange();
        if (!IsCurrentPublication(
                candidate, generation, candidate.LayoutRevision, epoch))
            return;
        _host.InvalidateAutomationLayout();
        if (!IsCurrentPublication(
                candidate, generation, candidate.LayoutRevision, epoch))
            return;
        _host.InvalidateCanvas();
    }

    private bool TryPrepareCodeBlockActionPublication(
        RasterPlacementPlan plan,
        out IReadOnlyList<CodeBlockActionPlan> oldCodeActionPlans)
    {
        oldCodeActionPlans = null!;
        CodeBlockPlacementIdentity[]? codePlacements = plan.CodePlacements;
        List<CodeBlockActionPlan>? candidatePlans = plan.CodeActionPlans;
        List<WeakReference<CodeBlockBox>>? actionSources = plan.CodeActionSources;
        int[]? placementIndices = plan.CodeActionPlacementIndices;
        List<(CodeBlockBox Box, Rect Rect, CodeBlockHostedElementKind Kind)>? actionRects = plan.CodeActionRects;
        List<int>? activeOrdinals = plan.ActiveCodeActionOrdinals;
        if (codePlacements is null || candidatePlans is null || actionSources is null ||
            placementIndices is null || actionRects is null || activeOrdinals is null ||
            candidatePlans.Count != actionSources.Count || candidatePlans.Count != placementIndices.Length ||
            candidatePlans.Count != _embeds.CodeBlockActionPlans.Count)
            return false;

        IReadOnlyList<CodeBlockActionPlan> oldPlans = _embeds.CodeBlockActionPlans;
        List<(CodeBlockActionPlan OldPlan, CodeBlockActionPlan NewPlan, int NewOrdinal)>? adoptions = plan.CodeActionAdoptions;
        adoptions?.Clear();
        actionRects.Clear();
        activeOrdinals.Clear();
        if (adoptions is null || adoptions.Capacity < candidatePlans.Count ||
            actionRects.Capacity < candidatePlans.Count || activeOrdinals.Capacity < candidatePlans.Count)
            return false;
        int previousPlacementIndex = -1;
        for (int ordinal = 0; ordinal < candidatePlans.Count; ordinal++)
        {
            int placementIndex = placementIndices[ordinal];
            if ((uint)placementIndex >= (uint)codePlacements.Length || placementIndex <= previousPlacementIndex)
                return false;
            previousPlacementIndex = placementIndex;

            CodeBlockPlacementIdentity identity = codePlacements[placementIndex];
            CodeBlockActionPlan oldPlan = oldPlans[ordinal];
            CodeBlockActionPlan newPlan = candidatePlans[ordinal];
            if (!identity.WasMeasured ||
                !actionSources[ordinal].TryGetTarget(out CodeBlockBox? sourceBox) ||
                !ReferenceEquals(sourceBox, oldPlan.Box) ||
                !identity.Source.TryGetTarget(out CodeBlockBox? capturedSource) ||
                !ReferenceEquals(sourceBox, capturedSource) ||
                !ReferenceEquals(newPlan.Box, identity.Placement) ||
                oldPlan.Kind != CodeBlockHostedElementKind.Copy ||
                newPlan.Kind != CodeBlockHostedElementKind.Copy ||
                oldPlan.FocusIdentity is not { } oldFocus ||
                newPlan.FocusIdentity is not { } newFocus ||
                oldFocus.BlockIndex != sourceBox.BlockIndex || oldFocus.InlineIndex != 0 ||
                oldFocus.Kind != FocusableItemKind.CodeBlockCopy ||
                newFocus.BlockIndex != sourceBox.BlockIndex || newFocus.InlineIndex != 0 ||
                newFocus.Kind != FocusableItemKind.CodeBlockCopy ||
                FocusableItemQueries.FindFocusableItemIndex(plan.Focus, newFocus) < 0 ||
                !ReferenceEquals(sourceBox.Metadata, identity.Placement.Metadata) ||
                !ReferenceEquals(sourceBox.CodeText, identity.Placement.CodeText) ||
                !sourceBox.HasCopyButtonAction || !identity.Placement.HasCopyButtonAction ||
                sourceBox.RealizedCopyButton != oldPlan.Realized ||
                identity.Placement.RealizedCopyButton is not null ||
                !sourceBox.IsCurrentPrivatePlacement(identity.Revision))
                return false;

            if (oldPlan.Realized is not null)
            {
                adoptions.Add((oldPlan, newPlan, ordinal));
                activeOrdinals.Add(ordinal);
                double left = Math.Round(newPlan.Rect.X);
                double top = Math.Round(newPlan.Rect.Y);
                double width = Math.Round(newPlan.Rect.X + newPlan.Rect.Width) - left;
                double height = Math.Round(newPlan.Rect.Y + newPlan.Rect.Height) - top;
                actionRects.Add((newPlan.Box, new Rect(left, top, width, height), newPlan.Kind));
            }
        }

        oldCodeActionPlans = oldPlans;
        return true;
    }

    /// <summary>
    /// True while <paramref name="candidate"/> is still the published
    /// snapshot at the given generation, layout revision and hosted focus
    /// plan epoch.
    /// </summary>
    internal bool IsCurrentPublication(
        LayoutSnapshot candidate,
        long generation,
        long layoutRevision,
        long epoch)
        => !_lifetime.IsDisposed && !_lifetime.IsUnloaded && ReferenceEquals(candidate, _published.Snapshot) &&
           generation == _published.Generation &&
           candidate.LayoutRevision == layoutRevision &&
           layoutRevision == _hostedFocusPlan.PublishedRevision &&
           generation == _hostedFocusPlan.PublishedGeneration &&
           epoch == _hostedFocusPlan.Epoch &&
           epoch == _hostedFocusPlan.PublishedEpoch;
}
