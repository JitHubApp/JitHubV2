using System.Diagnostics;
using MarkdownRenderer.Controls.Embeds;
using MarkdownRenderer.Controls.Focus;
using MarkdownRenderer.Controls.Highlighting;
using MarkdownRenderer.Controls.Images;
using MarkdownRenderer.Controls.Input;
using MarkdownRenderer.Controls.Readiness;
using MarkdownRenderer.Controls.Rendering;
using MarkdownRenderer.Controls.Selection;
using MarkdownRenderer.Layout.Internal.Snapshots;
using MarkdownRenderer.Selection;
using MarkdownRenderer.Selection.Internal;

namespace MarkdownRenderer.Controls.Pipeline;

/// <summary>
/// Completes a published replacement: presentation, overlays, selection,
/// realization plans and automation, recording each phase on the
/// replacement's timeline.
/// </summary>
internal sealed class ReplacementPresentation
{
    private readonly MarkdownViewSurface _surface;
    private readonly SnapshotRetirementTracker _snapshotRetirements;
    private readonly RebuildScheduler _rebuild;
    private readonly LayoutCommitState _layoutCommit;
    private readonly LayoutCommitPublisher _layoutPublisher;
    private readonly DocumentInputs _documentInputs;
    private readonly SnapshotExtent _extent;
    private readonly ScrollViewTracking _scrolling;
    private readonly PointerHoverState _hover;
    private readonly SelectionController _selection;
    private readonly TouchSelectionRestore _touchRestore;
    private readonly SelectionContextMenuPresenter _contextMenu;
    private readonly SelectionDragShield _dragShield;
    private readonly SelectionAdornerPresenter _adorner;
    private readonly InteractiveTextAdorner _textAdorner;
    private readonly SelectionHandlesPresenter _handles;
    private readonly EmbedRealization _embeds;
    private readonly ImageSubscriptions _imageSubscriptions;
    private readonly ImagePlanSet _imagePlans;
    private readonly ImageResidency _imageResidency;
    private readonly KeyboardFocusModel _focus;
    private readonly FocusRingPresenter _focusRing;
    private readonly FocusedItemCommit _focusCommit;
    private readonly CodeBlockHighlighting _highlighting;
    private readonly VisibleWorkScheduler _visibleWork;
    private readonly CanvasInvalidation _canvasInvalidation;
    private readonly RendererAutomation _automation;
    private readonly DeferredSceneReadiness _deferredScenes;

    internal ReplacementPresentation(
        MarkdownViewSurface surface,
        SnapshotRetirementTracker snapshotRetirements,
        RebuildScheduler rebuild,
        LayoutCommitState layoutCommit,
        LayoutCommitPublisher layoutPublisher,
        DocumentInputs documentInputs,
        SnapshotExtent extent,
        ScrollViewTracking scrolling,
        PointerHoverState hover,
        SelectionController selection,
        TouchSelectionRestore touchRestore,
        SelectionContextMenuPresenter contextMenu,
        SelectionDragShield dragShield,
        SelectionAdornerPresenter adorner,
        InteractiveTextAdorner textAdorner,
        SelectionHandlesPresenter handles,
        EmbedRealization embeds,
        ImageSubscriptions imageSubscriptions,
        ImagePlanSet imagePlans,
        ImageResidency imageResidency,
        KeyboardFocusModel focus,
        FocusRingPresenter focusRing,
        FocusedItemCommit focusCommit,
        CodeBlockHighlighting highlighting,
        VisibleWorkScheduler visibleWork,
        CanvasInvalidation canvasInvalidation,
        RendererAutomation automation,
        DeferredSceneReadiness deferredScenes)
    {
        _surface = surface;
        _snapshotRetirements = snapshotRetirements;
        _rebuild = rebuild;
        _layoutCommit = layoutCommit;
        _layoutPublisher = layoutPublisher;
        _documentInputs = documentInputs;
        _extent = extent;
        _scrolling = scrolling;
        _hover = hover;
        _selection = selection;
        _touchRestore = touchRestore;
        _contextMenu = contextMenu;
        _dragShield = dragShield;
        _adorner = adorner;
        _textAdorner = textAdorner;
        _handles = handles;
        _embeds = embeds;
        _imageSubscriptions = imageSubscriptions;
        _imagePlans = imagePlans;
        _imageResidency = imageResidency;
        _focus = focus;
        _focusRing = focusRing;
        _focusCommit = focusCommit;
        _highlighting = highlighting;
        _visibleWork = visibleWork;
        _canvasInvalidation = canvasInvalidation;
        _automation = automation;
        _deferredScenes = deferredScenes;
    }

    /// <summary>
    /// Completes a published replacement: presentation, overlays, selection,
    /// realization plans and automation, recording each phase on
    /// <paramref name="timeline"/>.
    /// </summary>
    internal void Finish(
        RebuildLayoutPlan plan,
        LayoutSnapshot snapshot,
        long generation,
        ReplacementPublication publication,
        ReplacementTimeline timeline)
    {
        ApplyPresentation(plan, snapshot, publication);
        timeline.CommitEnded = Stopwatch.GetTimestamp();

        ResetOverlays();
        RestoreSelection(publication, generation);
        timeline.OverlayResetEnded = Stopwatch.GetTimestamp();

        Realize(snapshot, timeline);

        _canvasInvalidation.Invalidate();
        if (publication.SemanticInputChanged)
        {
            _automation.FindPeer()?
                .NotifyDocumentChanged();
        }
        else
        {
            // A progressive scene may have been replaced while the semantic
            // source stayed identical. Refresh root UIA ItemStatus after the
            // committed snapshot without publishing text/structure events.
            _deferredScenes.NotifyItemStatusMayHaveChanged();
        }
        timeline.PublicationEnded = Stopwatch.GetTimestamp();
    }

    private void ApplyPresentation(
        RebuildLayoutPlan plan,
        LayoutSnapshot snapshot,
        ReplacementPublication publication)
    {
        _layoutCommit.MarkApplied(snapshot.LayoutRevision);
        _documentInputs.PublishParsedDocument(plan.Parse.SemanticDocument);
        if (publication.Old is not null)
            _contextMenu.Close();
        // Dependency-property publication above can reenter Dispose before
        // this old snapshot is retired. Tie its retirement to this context's
        // pipeline lifetime as well as the control's global drain.
        plan.Context.RegisterRetirement(_snapshotRetirements.Retire(publication.Old));
        // Clear stale hover references so OnPointerExited/OnPointerMoved after the
        // rebuild don't use Bounds from the now-disposed old snapshot for invalidation.
        _hover.Clear();
        _extent.Apply(snapshot.Size);

        // Restore scroll anchor: find the anchor block's new Y in the new layout
        // and adjust the scroll offset so the user's read position is unchanged.
        if (publication.ScrollAnchor is { } anchor && _surface.Scroll is { } scroll)
            anchor.Restore(snapshot, scroll, _scrolling.IsIntermediate);
    }

    private void ResetOverlays()
    {
        // UI thread: collect embed plans (don't realise yet), hook image
        // LoadCompleted, then realise only embeds that fall in the current
        // viewport. Hooking ScrollViewer.ViewChanged drives subsequent realisation
        // as the user scrolls.
        _embeds.DerealizeAll();
        _dragShield.SetActive(false);
        // Keep the attached adorner surface and its placeholder across
        // commits. Repaint against the new snapshot instead of replacing the
        // surface on every relayout.
        _adorner.InvalidatePresentation();
        _adorner.Collapse();
        _adorner.RemoveOtherOverlayChildren();
        _embeds.ClearHitTargets();
        _embeds.ClearPlans();
        _imageSubscriptions.UnsubscribeAll();
        _imagePlans.Clear();
        _imageResidency.Reset();
        // Identities change across rebuild even when the count happens to
        // match — reset so the first post-rebuild realisation always fires.
        _embeds.ResetRealizedCountNotification();
        _adorner.ClearGeometry();
        _handles.MarkEndpointsUnavailable();
    }

    private void RestoreSelection(ReplacementPublication publication, long generation)
    {
        // Rebuild detaches selection chrome with the other overlay children.
        // Surviving selection reattaches what it needs below; inactive chrome
        // stays detached until the next interaction.
        if (publication.PreserveSelection)
        {
            bool restoreTouchSelectionPresentation = _touchRestore.TryRestoreAfterReplacement(
                publication.TouchSelectionRestoreIntent,
                publication.TouchSelectionRestoreSourceGeneration,
                generation,
                _rebuild.HasPending);

            _textAdorner.UpdateSelectionOverlay();
            if (restoreTouchSelectionPresentation)
                _handles.Update();
        }
        else
        {
            _touchRestore.Cancel();
            _selection.Clear();
        }
    }

    private void Realize(LayoutSnapshot snapshot, ReplacementTimeline timeline)
    {
        _focus.ClearResumeIndex();
        _focus.ClearLazyFocus();
        _focusRing.ForgetEvictedRing();
        bool realizationPlansReady = _layoutPublisher.TryRebuildPlans(
            snapshot,
            preserveRealized: false,
            out _,
            out _);
        if (!realizationPlansReady)
            _layoutPublisher.Defer(scrollAnchor: null);
        timeline.PlanConstructionEnded = Stopwatch.GetTimestamp();
        _scrolling.Attach();
        _embeds.RealizeVisible();
        timeline.EmbedRealizationEnded = Stopwatch.GetTimestamp();
        bool resumedPendingHostedFocus = _focusCommit.ResumePendingLazy(snapshot);
        _ = _highlighting.Restart();
        timeline.HighlightRetirementEnded = Stopwatch.GetTimestamp();
        _visibleWork.ScheduleCodeBlockHighlighting();
        timeline.HighlightSchedulingEnded = Stopwatch.GetTimestamp();
        _adorner.InvalidateGeometry();
        if (!resumedPendingHostedFocus)
            _focusRing.Update();
        timeline.VisibleRealizationEnded = Stopwatch.GetTimestamp();
    }
}
