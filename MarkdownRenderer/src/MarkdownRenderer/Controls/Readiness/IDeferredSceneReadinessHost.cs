using MarkdownRenderer.Accessibility;
using MarkdownRenderer.Performance;

namespace MarkdownRenderer.Controls.Readiness;

/// <summary>The renderer services <see cref="DeferredSceneReadiness"/> reads at query time.</summary>
internal interface IDeferredSceneReadinessHost
{
    /// <summary>The renderer that owns this document in the performance session.</summary>
    MarkdownRendererControl DocumentOwner { get; }

    /// <summary>The attached performance session, read anew for every query.</summary>
    IMarkdownPerformanceSessionInternal? PerformanceSession { get; }

    /// <summary>The renderer's automation peer, without creating one.</summary>
    MarkdownAutomationPeer? FindAutomationPeer();
}
