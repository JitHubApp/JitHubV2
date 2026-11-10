using System.Threading;
using MarkdownRenderer.Hosting;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Layout.Internal.Images;
using Microsoft.UI.Xaml;

namespace MarkdownRenderer.Controls.Embeds;

/// <summary>
/// What <see cref="EmbedRealization"/> needs from the control: pipeline
/// currency, the image half of a visible pass, keyboard focus restoration,
/// raster-candidate publication currency and the control's events.
/// </summary>
internal interface IEmbedRealizationHost
{
    /// <summary>The current pipeline's cancellation token, or none.</summary>
    CancellationToken PipelineToken { get; }

    /// <summary>The current pipeline generation.</summary>
    long PipelineGeneration { get; }

    /// <summary>The host's factory for declarative hosted elements, if any.</summary>
    IMarkdownHostedElementFactory? HostedElementFactory { get; }

    /// <summary>The published image plan index a visible pass starts from.</summary>
    ViewportBandIndex? ImagePlanIndex { get; }

    /// <summary>Registers an image found while collecting plans.</summary>
    void AddImagePlan(ImageBox image);

    /// <summary>
    /// Starts loading images near the viewport and reconciles image
    /// residency. Returns false when the pass stopped being current.
    /// </summary>
    bool RealizeVisibleImages(in VisibleRealizationPass pass, double top, double viewportHeight, double bottom);

    /// <summary>
    /// True while <paramref name="snapshot"/>, its generation, the hosted
    /// focus plan epoch and the layout revision still match the plan
    /// publication in progress.
    /// </summary>
    bool IsCurrentPlanPublication(
        LayoutSnapshot snapshot,
        long generation,
        long hostedFocusPlanEpoch,
        long layoutRevision);

    bool IsCurrentRasterCodeActionPublication(
        LayoutSnapshot candidate,
        long generation,
        long layoutRevision,
        long hostedFocusPlanEpoch);

    /// <summary>Invalidates the published hosted focus plan.</summary>
    void InvalidateHostedFocusPlanPublication();

    void CopyCodeBlockToClipboard(CodeBlockActionPlan plan);

    void RestoreFocusedCodeBlockActionIfNeeded(CodeBlockActionPlan plan);

    void PreserveLogicalFocusBeforeDerealizingCodeBlockAction(FrameworkElement element);

    void RestoreFocusedHostedElementIfNeeded(DeclarativeHostedElementPlan plan);

    /// <summary>Requests a restyle rebuild so a failed hosted element renders its fallback.</summary>
    void RequestFallbackRebuild();

    void InvalidateAutomationLayout();

    /// <summary>Raises the control's EmbedsRealizationChanged event, swallowing subscriber exceptions.</summary>
    void RaiseEmbedsRealizationChanged();
}
