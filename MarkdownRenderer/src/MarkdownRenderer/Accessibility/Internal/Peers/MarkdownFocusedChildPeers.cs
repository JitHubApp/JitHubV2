using MarkdownRenderer.Accessibility.Internal.Semantics;
using MarkdownRenderer.Controls;
using MarkdownRenderer.Layout.Internal.Boxes;
using MarkdownRenderer.Layout.Internal.Inlines;
using MarkdownRenderer.Layout.Internal.Inlines.Runs;
using MarkdownRenderer.Layout.Internal.Vectors;
using Microsoft.UI.Xaml.Automation.Peers;

namespace MarkdownRenderer.Accessibility.Internal.Peers;

/// <summary>
/// Resolves a focused virtual child to the same cached peer that the
/// document's UIA tree exposes for it, within one current semantic document.
/// </summary>
internal sealed class MarkdownFocusedChildPeers : IMarkdownFocusedChildPeers<AutomationPeer>
{
    private readonly MarkdownRendererControl _owner;
    private readonly MarkdownAutomationProjection _projection;
    private readonly MarkdownSemanticDocument _document;

    /// <param name="owner">The renderer whose child-peer caches hold link and linked-image peers.</param>
    /// <param name="projection">The document peer's projection, which holds block and node peers.</param>
    /// <param name="document">
    /// The projection's current document. Reading it first makes the
    /// projection drop peers of an older snapshot, so every peer returned here
    /// belongs to the published layout.
    /// </param>
    internal MarkdownFocusedChildPeers(
        MarkdownRendererControl owner,
        MarkdownAutomationProjection projection,
        MarkdownSemanticDocument document)
    {
        _owner = owner;
        _projection = projection;
        _document = document;
    }

    public AutomationPeer? ForLink(InlineContainerBox inline, LinkRun link) =>
        _owner.AutomationHost.GetOrCreateLinkPeer(_projection.GetOrCreateBlockPeer(inline), link);

    public AutomationPeer? ForLinkedImage(InlineContainerBox inline, InlineImageRun image) =>
        _owner.AutomationHost.GetOrCreateLinkedImagePeer(_projection.GetOrCreateBlockPeer(inline), image);

    public AutomationPeer? ForInlineVectorScene(InlineVectorSceneRun scene)
    {
        foreach (MarkdownSemanticNode node in MarkdownAutomationProjection.EnumerateSemanticNodes(_document.Root))
        {
            if (node.Role == MarkdownSemanticRole.Math && ReferenceEquals(node.InlineRun, scene))
                return _projection.GetOrCreateNodePeer(node);
        }

        return null;
    }

    public AutomationPeer? ForVectorSemantic(VectorSceneBox box, int semanticIndex)
    {
        foreach (MarkdownSemanticNode node in MarkdownAutomationProjection.EnumerateSemanticNodes(_document.Root))
        {
            if (ReferenceEquals(node.VectorSceneBox, box) && node.VectorSemanticIndex == semanticIndex)
                return _projection.GetOrCreateNodePeer(node);
        }

        return null;
    }

    public AutomationPeer? ForHorizontalOverflow(IHorizontalOverflowBox overflow) =>
        MarkdownAutomationPeer.FindHorizontalOverflowFocusNode(_document, overflow) is { } node
            ? _projection.GetOrCreateNodePeer(node)
            : null;
}
