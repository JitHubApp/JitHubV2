using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MarkdownRenderer.Diagnostics;
using MarkdownRenderer.Images;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Performance;
using Windows.Foundation;

namespace MarkdownRenderer.Controls;

public partial class MarkdownRendererControl
{
    private readonly object _imagePreparationOwnerToken = new();
    // Captured on the UI thread; this handoff uses weak control identity and
    // never forwards callbacks belonging to the old layout generation.
    private sealed class RasterPlacementInputs(
        WeakReference<MarkdownRendererControl> receiver,
        long generation,
        object imagePreparationOwner,
        CancellationToken imageLifetime,
        SnapshotRetirementTracker retirements,
        double? scrollOffset)
    {
        private WeakReference<LayoutSnapshot>? _candidateSnapshot;
        internal readonly CancellationToken ImageLifetime = imageLifetime;
        internal readonly object ImagePreparationOwner = imagePreparationOwner;
        internal readonly Action<Task> RetirementSink = retirements.Track;
        internal readonly double? ScrollOffset = scrollOffset;

        internal void Bind(LayoutSnapshot snapshot) =>
            Volatile.Write(ref _candidateSnapshot, new WeakReference<LayoutSnapshot>(snapshot));

        internal void ImageUnavailable(string url, MarkdownImageUnavailableReason reason, MarkdownSvgFailureReason? svgReason)
        {
            if (receiver.TryGetTarget(out MarkdownRendererControl? control) &&
                Volatile.Read(ref _candidateSnapshot) is { } identity && identity.TryGetTarget(out LayoutSnapshot? snapshot) &&
                !control._isDisposed && !control._isUnloaded && generation == control._snapshotGeneration &&
                ReferenceEquals(snapshot, control._snapshot))
                control.RaiseImageUnavailable(url, reason, svgReason);
        }
    }

    // All geometry and membership are prepared privately. Discard releases the
    // strong candidate; adopted lists are never cleared by the former owner.
    private sealed class RasterPlacementPlan : IDisposable
    {
        internal readonly SingleRasterLayoutCandidate Owner;
        internal List<ImageBox>? Images;
        internal HashSet<ImageBox>? Subscriptions;
        internal ImagePlanPublication? Publication;
        internal IReadOnlyList<FocusableItem>? Focus;
        internal List<CodeBlockActionPlan>? CodeActionPlans;
        internal List<WeakReference<CodeBlockBox>>? CodeActionSources;
        internal int[]? CodeActionPlacementIndices;
        internal CodeBlockPlacementIdentity[]? CodePlacements;
        internal ViewportBandIndex? CodeActionIndex;
        internal List<(CodeBlockBox Box, Rect Rect, CodeBlockHostedElementKind Kind)>? CodeActionRects;
        internal List<int>? ActiveCodeActionOrdinals;
        internal bool HasMeasuredCodeBlocks;
        internal List<(CodeBlockActionPlan OldPlan, CodeBlockActionPlan NewPlan, int NewOrdinal)>? CodeActionAdoptions;
        internal Layout.FocusableItem? ResumeFocusIdentity;
        internal readonly double? AnchorOffset;
        internal readonly double? CapturedScrollOffset;

        internal RasterPlacementPlan(SingleRasterLayoutCandidate owner, long generation,
            (int BlockIndex, double OffsetFromTop)? anchor, double? scrollOffset)
        {
            Owner = owner;
            LayoutSnapshot snapshot = owner.Snapshot;
            // Estimated roots carry only never-started descriptors. They must
            // not become subscriptions or start work before normal lazy-band
            // realization discovers them in the newly published snapshot.
            var imagePlan = new RasterPlacementImagePlan(
                snapshot, snapshot.GetMeasuredTopLevelBlocks(), owner.SourceSubscribedImageCount);
            Images = imagePlan.Images;
            Subscriptions = imagePlan.Subscriptions;
            Publication = new ImagePlanPublication(snapshot, generation, snapshot.LayoutRevision,
                imagePlan.PublicationImages, imagePlan.ImageBounds, imagePlan.Index);
            Focus = snapshot.CollectFocusableItems();

            IReadOnlyList<CodeBlockPlacementIdentity> placements = owner.CodePlacements;
            CodePlacements = new CodeBlockPlacementIdentity[placements.Count];
            var actions = new List<CodeBlockActionPlan>(placements.Count);
            var sources = new List<WeakReference<CodeBlockBox>>(placements.Count);
            var placementIndices = new List<int>(placements.Count);
            for (int index = 0; index < placements.Count; index++)
            {
                CodeBlockPlacementIdentity identity = placements[index];
                CodePlacements[index] = identity;
                CodeBlockBox placement = identity.Placement;
                if (identity.WasMeasured)
                    HasMeasuredCodeBlocks = true;
                if (!identity.WasMeasured || !placement.HasCopyButtonAction ||
                    placement.CopyButtonBounds.Width <= 0 || placement.CopyButtonBounds.Height <= 0)
                    continue;

                actions.Add(new CodeBlockActionPlan
                {
                    Box = placement,
                    FocusIdentity = new FocusableItem(
                        placement.BlockIndex, 0, FocusableItemKind.CodeBlockCopy),
                    Rect = snapshot.ToLogicalRectForBlockIndex(
                        placement.BlockIndex, placement.CopyButtonBounds),
                    Kind = CodeBlockHostedElementKind.Copy,
                });
                sources.Add(identity.Source);
                placementIndices.Add(index);
            }
            CodeActionPlans = actions;
            CodeActionSources = sources;
            CodeActionPlacementIndices = placementIndices.ToArray();
            CodeActionIndex = BuildPlanIndex(actions);
            CodeActionRects = new List<(CodeBlockBox Box, Rect Rect, CodeBlockHostedElementKind Kind)>(actions.Count);
            ActiveCodeActionOrdinals = new List<int>(actions.Count);
            CodeActionAdoptions = new List<(CodeBlockActionPlan OldPlan, CodeBlockActionPlan NewPlan, int NewOrdinal)>(actions.Count);
            AnchorOffset = GetScrollAnchorOffset(snapshot, anchor);
            CapturedScrollOffset = scrollOffset;
        }

        public void Dispose()
        {
            Owner.Dispose();
            Images = null;
            Subscriptions = null;
            Publication = null;
            Focus = null;
            CodeActionPlans = null;
            CodeActionSources = null;
            CodeActionPlacementIndices = null;
            CodePlacements = null;
            CodeActionIndex = null;
            CodeActionRects = null;
            ActiveCodeActionOrdinals = null;
            CodeActionAdoptions = null;
        }
    }

    private bool TryPublishRasterPlacement(LayoutSnapshot source, RasterPlacementPlan plan, long generation)
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
        if (_isDisposed || _isUnloaded || _publishingLayoutCommit || _canvasDeviceRecoveryQueued ||
            _rebuildCoordinator.HasPendingOrBuilding || HasPendingLazyLayoutWork || HasSnapshotPointerInteraction ||
            !ReferenceEquals(source, _snapshot) || generation != _snapshotGeneration ||
            source.IsRetiringOrDisposed || source.LayoutRevision != owner.SourceRevision ||
            (float)Math.Max(50, ActualWidth) != owner.Width ||
            !ReferenceEquals(_themeSnapshot, context.ThemeSnapshot) ||
            !ReferenceEquals(PerformanceSession, context.PerformanceSession) ||
            context.PerformanceSession?.IsDisposed == true ||
            context.ImageCancellationToken.IsCancellationRequested ||
            !ReferenceEquals(_imagePreparationOwnerToken, context.ImagePreparationOwner) ||
            !RasterPlacementResolverFence.Matches(
                ImageResolver,
                context.ImageResolver,
                PerformanceSession as IMarkdownPerformanceSessionInternal,
                context.PerformanceSession,
                new MarkdownImageResolveContext(
                    ImageBaseUri, ImageDocumentPath, AllowThirdPartyRemoteImages, ImageDocumentSource),
                new MarkdownImageResolveContext(
                    context.ImageBaseUri, context.ImageDocumentPath,
                    context.AllowThirdPartyRemoteImages, context.ImageDocumentSource)) ||
            !ReferenceEquals(SvgRenderer, context.SvgRenderer) ||
            !ReferenceEquals(ImageDocumentSource, context.ImageDocumentSource) ||
            !Equals(ImageBaseUri, context.ImageBaseUri) ||
            !string.Equals(ImageDocumentPath, context.ImageDocumentPath, StringComparison.Ordinal) ||
            AllowThirdPartyRemoteImages != context.AllowThirdPartyRemoteImages ||
            FlowDirection != context.FlowDirection || (XamlRoot?.RasterizationScale ?? 1) != context.RasterizationScale ||
            _embedPlans.Count != 0 ||
            !owner.MatchesSourceImages(_imagePlans, _subscribedImages) ||
            (_deferredPointerSnapshot is not null && !ReferenceEquals(_deferredPointerSnapshot, source)) ||
            !owner.HasCurrentSourceRaster() ||
            !ReferenceEquals(source.SourceMap, candidate.SourceMap) ||
            source.AutomationDocumentGeneration != candidate.AutomationDocumentGeneration)
            return false;

        if (!TryPrepareCodeBlockActionPublication(plan, out List<CodeBlockActionPlan> oldCodeActionPlans))
            return false;

        Layout.FocusableItem? focusedIdentity = GetCurrentFocusableIdentity();
        Layout.FocusableItem? resumeFocusIdentity =
            _focusableItems is { } currentItems &&
            _focusResumeItemIndex >= 0 && _focusResumeItemIndex < currentItems.Count
                ? currentItems[_focusResumeItemIndex]
                : null;
        plan.ResumeFocusIdentity = resumeFocusIdentity;

        _publishingLayoutCommit = true;
        bool sourceRetirementRequested = false;
        bool committed = false;
        try
        {
            // Compiler-generated events on sealed ImageBox do not run host code.
            // Both subscriptions and all new containers were allocated off-thread.
            ResetBitmapOutputRecovery();
            foreach (ImageBox image in _subscribedImages)
                image.LoadCompleted -= OnImageLoadCompleted;
            foreach (ImageBox image in plan.Subscriptions!)
                image.LoadCompleted += OnImageLoadCompleted;
            _imagePlans = plan.Images!;
            _subscribedImages = plan.Subscriptions!;
            _imagePlanIndex = plan.Publication!.Index;
            _focusableItems = plan.Focus;
            _focusedItemIndex = focusedIdentity is { } identity
                ? FindFocusableItemIndex(_focusableItems, identity)
                : -1;
            _focusResumeItemIndex = resumeFocusIdentity is { } resumeIdentity
                ? FindFocusableItemIndex(_focusableItems, resumeIdentity)
                : -1;
            _codeBlockActionPlans = plan.CodeActionPlans!;
            plan.CodeActionPlans = null;
            _codeBlockActionPlanIndex = plan.CodeActionIndex;
            plan.CodeActionIndex = null;
            _activeCodeBlockActionOrdinals = plan.ActiveCodeActionOrdinals!;
            plan.ActiveCodeActionOrdinals = null;
            _hasMeasuredCodeBlocks = plan.HasMeasuredCodeBlocks;
            _codeBlockActionRects = plan.CodeActionRects!;
            plan.CodeActionRects = null;
            _appliedLazyLayoutRevision = candidate.LayoutRevision;
            _pendingLayoutCommit = null;
            _lastHoveredRun = null;
            _lastHoveredBox = null;
            _lastHoveredAbbreviation = null;
            _pendingAbbreviationTooltipRun = null;
            long epoch = InvalidateHostedFocusPlanPublication();
            _snapshot = owner.TransferToControl();
            Volatile.Write(ref _imagePlanPublication, plan.Publication);
            Volatile.Write(ref _publishedHostedFocusPlanGeneration, generation);
            Volatile.Write(ref _publishedHostedFocusPlanRevision, candidate.LayoutRevision);
            Volatile.Write(ref _publishedHostedFocusPlanEpoch, epoch);
            committed = true;

            try
            {
                // Ask the old tree to retire before any callback-capable code
                // button localization/highlighting or UI publication work.
                RetireSnapshot(source);
                sourceRetirementRequested = true;

                if (ReferenceEquals(_deferredPointerSnapshot, source))
                {
                    // A logical geometry waiter belongs to the exact snapshot
                    // whose hit-test produced it. Candidate publication must
                    // cancel it, never silently retarget it to a sibling tree.
                    // Capture release is callback-capable, so perform it only
                    // after ownership has committed and the old tree is retired.
                    try
                    {
                        ResetDeferredPointerInput();
                    }
                    catch (Exception exception)
                    {
                        MarkdownDiagnostics.WriteLine(
                            $"[MarkdownRendererControl] Candidate input-waiter cleanup failed: {exception.GetType().Name}.");
                    }

                    if (!IsCurrentRasterCodeActionPublication(
                            candidate, generation, candidate.LayoutRevision, epoch))
                        return true;
                }

                foreach (var adoption in plan.CodeActionAdoptions!)
                {
                    if (!IsCurrentRasterCodeActionPublication(
                            candidate, generation, candidate.LayoutRevision, epoch))
                        break;

                    bool adopted;
                    try
                    {
                        adopted = adoption.NewPlan.AdoptRealizedFromRasterCandidate(
                            adoption.OldPlan, this, candidate, generation, candidate.LayoutRevision, epoch);
                    }
                    catch (Exception exception)
                    {
                        adopted = false;
                        MarkdownDiagnostics.WriteLine(
                            $"[MarkdownRendererControl] Candidate code-action adoption failed: {exception.GetType().Name}.");
                    }

                    if (!IsCurrentRasterCodeActionPublication(
                            candidate, generation, candidate.LayoutRevision, epoch))
                        break;

                    if (adopted && adoption.NewPlan.Realized is { } adoptedElement &&
                        !adoption.NewPlan.IsCurrentRasterActionOwner(
                            this, candidate, generation, candidate.LayoutRevision, epoch, adoptedElement))
                        break;

                    if (!adopted && IsCurrentRasterCodeActionPublication(
                            candidate, generation, candidate.LayoutRevision, epoch))
                    {
                        if (adoption.NewPlan.Realized is { } failedElement &&
                            ReferenceEquals(adoption.NewPlan.Box.RealizedCopyButton, failedElement))
                        {
                            try
                            {
                                adoption.NewPlan.Derealize(this);
                            }
                            catch (Exception exception)
                            {
                                MarkdownDiagnostics.WriteLine(
                                    $"[MarkdownRendererControl] Candidate code-action cleanup failed: {exception.GetType().Name}.");
                            }
                        }
                        if (!IsCurrentRasterCodeActionPublication(
                                candidate, generation, candidate.LayoutRevision, epoch))
                            break;
                        _activeCodeBlockActionOrdinals?.Remove(adoption.NewOrdinal);
                        _codeBlockActionRects.RemoveAll(
                            rect => ReferenceEquals(rect.Box, adoption.NewPlan.Box));
                    }
                }

                // Highlight tasks are snapshot-bound, so their old callbacks
                // cannot mutate this candidate. Re-run the normal visible path
                // after publishing the fresh code placements.
                if (IsCurrentRasterCodeActionPublication(
                        candidate, generation, candidate.LayoutRevision, epoch))
                    ScheduleVisibleCodeBlockHighlighting();
            }
            finally
            {
                foreach (CodeBlockActionPlan oldPlan in oldCodeActionPlans)
                {
                    if (oldPlan.Realized is not null)
                    {
                        try
                        {
                            oldPlan.Derealize(this);
                        }
                        catch (Exception exception)
                        {
                            MarkdownDiagnostics.WriteLine(
                                $"[MarkdownRendererControl] Retired code-action cleanup failed: {exception.GetType().Name}.");
                        }
                    }
                }
            }

            if (!IsCurrentRasterCodeActionPublication(
                    candidate, generation, candidate.LayoutRevision, epoch))
                return true;
            NotifyAutomationSnapshotChanged();
            if (!IsCurrentRasterCodeActionPublication(
                    candidate, generation, candidate.LayoutRevision, epoch))
                return true;
            HideAbbreviationTooltip();
            if (!IsCurrentRasterCodeActionPublication(
                    candidate, generation, candidate.LayoutRevision, epoch))
                return true;
            SetCursorShape(null);
            if (!IsCurrentRasterCodeActionPublication(
                    candidate, generation, candidate.LayoutRevision, epoch))
                return true;
            ApplySnapshotSize(candidate.Size);
            if (!IsCurrentRasterCodeActionPublication(
                    candidate, generation, candidate.LayoutRevision, epoch))
                return true;
            if (plan.AnchorOffset is { } target && _scroll is { } scroll &&
                LazyLayoutScrollAnchorPolicy.ShouldRestore(plan.CapturedScrollOffset, scroll.VerticalOffset, _scrollViewIntermediate) &&
                Math.Abs(target - scroll.VerticalOffset) >= .5)
                scroll.ChangeView(null, target, null, disableAnimation: true);
            if (!IsCurrentRasterCodeActionPublication(
                    candidate, generation, candidate.LayoutRevision, epoch))
                return true;
            PrepareImageResidency(candidate);
            if (!IsCurrentRasterCodeActionPublication(
                    candidate, generation, candidate.LayoutRevision, epoch))
                return true;
            // Image geometry can move code actions into or out of the current
            // viewport. Reconcile the new index now, without waiting for a
            // subsequent scroll to realize newly visible Copy buttons or trim
            // constructor-acquired offscreen image leases.
            RealizeVisibleEmbeds();
            if (!IsCurrentRasterCodeActionPublication(
                    candidate, generation, candidate.LayoutRevision, epoch))
                return true;
            UpdateFocusRing();
            if (!IsCurrentRasterCodeActionPublication(
                    candidate, generation, candidate.LayoutRevision, epoch))
                return true;
            RefreshInteractiveTextAdornerAfterLayoutChange();
            if (!IsCurrentRasterCodeActionPublication(
                    candidate, generation, candidate.LayoutRevision, epoch))
                return true;
            InvalidateAutomationLayout();
            if (!IsCurrentRasterCodeActionPublication(
                    candidate, generation, candidate.LayoutRevision, epoch))
                return true;
            InvalidateCanvas();
            if (!IsCurrentRasterCodeActionPublication(
                    candidate, generation, candidate.LayoutRevision, epoch))
                return true;
            return true;
        }
        finally
        {
            try
            {
                if (committed && !sourceRetirementRequested)
                    RetireSnapshot(source);
            }
            finally
            {
                _publishingLayoutCommit = false;
            }
        }
    }

    private bool TryPrepareCodeBlockActionPublication(
        RasterPlacementPlan plan,
        out List<CodeBlockActionPlan> oldCodeActionPlans)
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
            candidatePlans.Count != _codeBlockActionPlans.Count)
            return false;

        List<CodeBlockActionPlan> oldPlans = _codeBlockActionPlans;
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
                FindFocusableItemIndex(plan.Focus, newFocus) < 0 ||
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

    private bool IsCurrentRasterCodeActionPublication(
        LayoutSnapshot candidate,
        long generation,
        long layoutRevision,
        long epoch)
        => !_isDisposed && !_isUnloaded && ReferenceEquals(candidate, _snapshot) &&
           generation == Volatile.Read(ref _snapshotGeneration) &&
           candidate.LayoutRevision == layoutRevision &&
           layoutRevision == Volatile.Read(ref _publishedHostedFocusPlanRevision) &&
           generation == Volatile.Read(ref _publishedHostedFocusPlanGeneration) &&
           epoch == Volatile.Read(ref _hostedFocusPlanEpoch) &&
           epoch == Volatile.Read(ref _publishedHostedFocusPlanEpoch);
}
