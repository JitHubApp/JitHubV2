using System.Threading;
using MarkdownRenderer.Hosting;
using MarkdownRenderer.Layout.Internal.Boxes;
using MarkdownRenderer.Layout.Internal.Snapshots;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace MarkdownRenderer.Controls.Internal.Embeds;

/// <summary>
/// What embed and code block action plans need from their owner: the copy
/// button pool, the pipeline's currency, focus restoration and the hosted
/// element fallback.
/// </summary>
internal interface IEmbedPlanHost
{
    /// <summary>The current pipeline's cancellation token, or none.</summary>
    CancellationToken PipelineToken { get; }

    /// <summary>The current pipeline generation.</summary>
    long PipelineGeneration { get; }

    (Button Button, bool Attached) RentCodeBlockCopyButton();

    void ReturnCodeBlockCopyButton(Button button);

    void SetCodeBlockCopyButtonState(Button button, bool copied);

    bool SetCodeBlockCopyButtonStateForRasterCandidate(
        CodeBlockActionPlan plan,
        Button button,
        FrameworkElement element,
        LayoutSnapshot candidate,
        long generation,
        long layoutRevision,
        long hostedFocusPlanEpoch);

    bool IsCurrentRasterCodeActionPublication(
        LayoutSnapshot candidate,
        long generation,
        long layoutRevision,
        long hostedFocusPlanEpoch);

    void CopyCodeBlockToClipboard(CodeBlockActionPlan plan);

    void RestoreFocusedCodeBlockActionIfNeeded(CodeBlockActionPlan plan);

    void PreserveLogicalFocusBeforeDerealizingCodeBlockAction(FrameworkElement element);

    MarkdownHostedElementRequest CreateHostedElementRequest(
        DeclarativeHostedElementBox box,
        Rect layoutBounds);

    bool CanAttachHostedElement(DeclarativeHostedElementPlan plan, long pipelineGeneration);

    bool CanActivateHostedElementFallback(DeclarativeHostedElementPlan plan, long pipelineGeneration);

    void ActivateHostedElementFallback(DeclarativeHostedElementPlan plan);

    void RealizeVisibleEmbeds();

    void RestoreFocusedHostedElementIfNeeded(DeclarativeHostedElementPlan plan);
}
