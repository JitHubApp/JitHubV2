using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using RebuildReason = MarkdownRenderer.Controls.MarkdownRebuildCoordinatorReason;
using MarkdownRenderer.Document;
using MarkdownRenderer.Hosting;
using MarkdownRenderer.Images;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Performance;
using MarkdownRenderer.Selection;
using MarkdownRenderer.Theming;
using MarkdownRenderer.Controls.Composition;
using MarkdownRenderer.Controls.Embeds;
using MarkdownRenderer.Controls.Focus;
using MarkdownRenderer.Controls.Highlighting;
using MarkdownRenderer.Controls.Images;
using MarkdownRenderer.Controls.Rendering;
using MarkdownRenderer.Controls.Input;
using MarkdownRenderer.Controls.LazyLayout;
using MarkdownRenderer.Controls.Pipeline;
using MarkdownRenderer.Controls.Readiness;
using MarkdownRenderer.Controls.Selection;
using MarkdownRenderer.Accessibility.Internal.Peers;

namespace MarkdownRenderer.Controls.Rendering;

/// <summary>
/// Releases the published snapshot before the performance harness replaces
/// the shared Win2D device on purpose.
/// </summary>
internal sealed class SyntheticDeviceReset
{
    private readonly PublishedLayout _published;
    private readonly SnapshotRetirementTracker _snapshotRetirements;
    private readonly MarkdownAutomationDocumentGeneration _automationDocumentGenerations;
    private readonly LayoutCommitState _layoutCommit;
    private readonly RendererAutomation _automation;
    private readonly CanvasTilePainter _painter;
    private readonly LazyLayoutRealization _lazyLayout;
    private readonly VectorCapacityRetry _vectorCapacity;

    internal SyntheticDeviceReset(
        PublishedLayout published,
        SnapshotRetirementTracker snapshotRetirements,
        MarkdownAutomationDocumentGeneration automationDocumentGenerations,
        LayoutCommitState layoutCommit,
        RendererAutomation automation,
        CanvasTilePainter painter,
        LazyLayoutRealization lazyLayout,
        VectorCapacityRetry vectorCapacity)
    {
        _published = published;
        _snapshotRetirements = snapshotRetirements;
        _automationDocumentGenerations = automationDocumentGenerations;
        _layoutCommit = layoutCommit;
        _automation = automation;
        _painter = painter;
        _lazyLayout = lazyLayout;
        _vectorCapacity = vectorCapacity;
    }

    /// <summary>
    /// Releases device-bound snapshot resources before the performance harness
    /// deliberately replaces Win2D's shared device. Real device-loss HRESULTs
    /// are handled by the normal paint recovery path; this avoids a synthetic
    /// Dispose race producing an unrelated cross-factory E_FAIL.
    /// </summary>
    internal void Prepare()
    {
        _vectorCapacity.Reset();
        _lazyLayout.Cancel();
        _layoutCommit.ClearPending();
        LayoutSnapshot? snapshot = _published.Snapshot;
        _published.Snapshot = null;
        _automationDocumentGenerations.Invalidate();
        _automation.NotifySnapshotChanged();
        _painter.CancelFirstViewportMeasurement();
        _snapshotRetirements.Retire(snapshot);
    }
}
