using System;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using MarkdownRenderer.Accessibility;
using MarkdownRenderer.Controls.Focus;
using MarkdownRenderer.Controls.Images;
using MarkdownRenderer.Controls.LazyLayout;
using MarkdownRenderer.Controls.Navigation;
using MarkdownRenderer.Controls.Rendering;
using MarkdownRenderer.Controls.Selection;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Selection;

namespace MarkdownRenderer.Controls.Pipeline;

/// <summary>
/// Publishes a built snapshot in place of the current one, with the
/// presentation state that describes it, and decides what the rest of the
/// commit must do: whether this is the same document, whether its selection
/// survives, and where the reader's position was.
/// </summary>
internal sealed class ReplacementPublisher
{
    private readonly MarkdownRendererControl _control;
    private readonly PublishedLayout _published;
    private readonly MarkdownViewSurface _surface;
    private readonly RebuildScheduler _rebuild;
    private readonly TouchSelectionRestore _touchRestore;
    private readonly MarkdownAutomationDocumentGeneration _automationDocumentGenerations;
    private readonly SelectionController _selection;
    private readonly LazyLayoutRealization _lazyLayout;
    private readonly LayoutCommitState _layoutCommit;
    private readonly ScrollViewTracking _scrolling;
    private readonly CanvasTilePainter _painter;
    private readonly RendererLocalization _localization;
    private readonly FocusableItemsRefreshRequest _focusRefresh;
    private readonly VectorCapacityRetry _vectorCapacity;
    private readonly GeometryRecovery _geometryRecovery;
    private readonly ImageSubscriptions _imageSubscriptions;
    private readonly ImageRelayout _imageRelayout;
    private readonly BlockScrollNavigation _blockScroll;
    private readonly ImagePlanSet _imagePlans;
    private readonly KeyboardFocusModel _focus;
    private string? _committedImageDocumentId;

    internal ReplacementPublisher(
        MarkdownRendererControl control,
        PublishedLayout published,
        MarkdownViewSurface surface,
        RebuildScheduler rebuild,
        TouchSelectionRestore touchRestore,
        MarkdownAutomationDocumentGeneration automationDocumentGenerations,
        SelectionController selection,
        LazyLayoutRealization lazyLayout,
        LayoutCommitState layoutCommit,
        ScrollViewTracking scrolling,
        CanvasTilePainter painter,
        RendererLocalization localization,
        FocusableItemsRefreshRequest focusRefresh,
        VectorCapacityRetry vectorCapacity,
        GeometryRecovery geometryRecovery,
        ImageSubscriptions imageSubscriptions,
        ImageRelayout imageRelayout,
        BlockScrollNavigation blockScroll,
        ImagePlanSet imagePlans,
        KeyboardFocusModel focus)
    {
        _control = control;
        _published = published;
        _surface = surface;
        _rebuild = rebuild;
        _touchRestore = touchRestore;
        _automationDocumentGenerations = automationDocumentGenerations;
        _selection = selection;
        _lazyLayout = lazyLayout;
        _layoutCommit = layoutCommit;
        _scrolling = scrolling;
        _painter = painter;
        _localization = localization;
        _focusRefresh = focusRefresh;
        _vectorCapacity = vectorCapacity;
        _geometryRecovery = geometryRecovery;
        _imageSubscriptions = imageSubscriptions;
        _imageRelayout = imageRelayout;
        _blockScroll = blockScroll;
        _imagePlans = imagePlans;
        _focus = focus;
    }

    /// <summary>
    /// Publishes <paramref name="snapshot"/> and the presentation state that
    /// describes it in place of the current snapshot. Returns false, having
    /// published nothing, when <paramref name="generation"/> was superseded.
    /// </summary>
    internal bool TryPublish(
        RebuildLayoutPlan plan,
        LayoutSnapshot snapshot,
        long generation,
        long pipelineStartTimestamp,
        long sourceUtf16Bytes,
        CancellationToken ct,
        [NotNullWhen(true)] out ReplacementPublication? publication)
    {
        publication = null;
        if (ct.IsCancellationRequested || generation != _rebuild.Generation)
            return false;

        // Scroll anchoring: capture the current read position before the canvas
        // height changes, so content above the fold shifting (e.g. an image
        // loading) doesn't jump the reader's position.
        string? imageDocumentId = plan.Inputs.ImageDocumentSource?.DocumentId;
        bool sameDocumentIdentity = _committedImageDocumentId == imageDocumentId;
        ReplacementScrollAnchor? scrollAnchor = sameDocumentIdentity
            ? ReplacementScrollAnchor.Capture(_published.Snapshot, _surface)
            : null;

        if (ct.IsCancellationRequested || generation != _rebuild.Generation)
            return false;

        // Atomically swap snapshots, then retire the old one so its
        // CanvasTextLayout / placeholder handles are released without making
        // this UI-thread commit wait for an in-flight lazy measure.
        var old = _published.Snapshot;
        long touchSelectionRestoreSourceGeneration = _published.Generation;
        TouchSelectionPresentationRestoreIntent? touchSelectionRestoreIntent = _touchRestore.Pending;
        var automationDocumentIdentity = plan.CreateAutomationDocumentIdentity(snapshot);
        bool sameAutomationDocument = _automationDocumentGenerations.IsCurrent(automationDocumentIdentity);
        bool sameDocument = old is not null && sameDocumentIdentity &&
            old.SourceFingerprint.Equals(snapshot.SourceFingerprint) &&
            sameAutomationDocument;
        bool semanticInputChanged = HasSemanticInputChanged(plan, sameDocument);
        bool preserveSelection = !_selection.Range.Normalized().IsEmpty && sameDocument;
        // Scrolling or paint can queue another band realization while this
        // replacement pipeline is building. Fence that old-snapshot work at
        // the commit boundary before publishing and retiring its owner.
        _lazyLayout.Cancel();
        _layoutCommit.ClearPending();
        if (!sameDocument)
            _scrolling.ResetIntermediate();
        snapshot.AssignAutomationDocumentGeneration(
            _automationDocumentGenerations.Publish(automationDocumentIdentity));
        ForgetSnapshotScopedWork(sameDocument);
        _published.Snapshot = snapshot;
        _published.LayoutDevice = plan.Device;
        _published.RenderResources = plan.Context.RenderResources;
        // Publish presentation state with the new snapshot before any
        // dependency-property notification can re-enter UIA or menu queries.
        // These immutable objects therefore always describe the same commit.
        _published.Theme = plan.Theme;
        // The tile clear color is presentation state too. Publish it only with
        // the snapshot whose boxes use the matching immutable ThemeSnapshot;
        // otherwise an invalidation during background layout can clear using
        // the next theme and repaint using the previous one.
        _painter.PublishBackground(plan.Theme.SurfaceColor);
        _localization.Publish(plan.Inputs.Localization);
        _committedImageDocumentId = imageDocumentId;
        _published.Generation = generation;
        _focusRefresh.Mark(snapshot, generation);
        _vectorCapacity.Synchronize(snapshot);
        _painter.ArmFirstViewportMeasurement(pipelineStartTimestamp, sourceUtf16Bytes);
        publication = new ReplacementPublication(
            old, scrollAnchor, touchSelectionRestoreSourceGeneration, touchSelectionRestoreIntent,
            semanticInputChanged, preserveSelection);
        return true;
    }

    /// <summary>
    /// Forgets work bound to the outgoing snapshot: subscriptions and dirty
    /// indices cannot cross the snapshot boundary, including reentrant
    /// property notifications during the commit.
    /// </summary>
    private void ForgetSnapshotScopedWork(bool sameDocument)
    {
        _geometryRecovery.ClearPendingFocusGeometryWork();
        _imageSubscriptions.UnsubscribeAll();
        _imageRelayout.ClearForSnapshotReplacement();
        _blockScroll.ClearPending();
        _imagePlans.Publication = null;
        if (!sameDocument)
        {
            // A new logical document must not expose the former document's
            // value-only focus identities to automation or reentrant property
            // callbacks below.
            _focus.Reset();
        }
    }

    /// <summary>
    /// True when automation must treat the replacement as a new document:
    /// another logical document, document object or localization.
    /// </summary>
    private bool HasSemanticInputChanged(RebuildLayoutPlan plan, bool sameDocument) =>
        !sameDocument ||
        !ReferenceEquals(_control.Document, plan.Parse.SemanticDocument) ||
        !ReferenceEquals(_localization.Committed?.StringProvider, plan.Inputs.StringProvider) ||
        !string.Equals(
            _localization.Committed?.Culture.Name,
            plan.Inputs.Language,
            StringComparison.OrdinalIgnoreCase);
}
