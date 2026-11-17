using MarkdownRenderer.Layout.Internal.Boxes;
using MarkdownRenderer.Layout.Internal.Inlines;
using MarkdownRenderer.Layout.Internal.Inlines.Runs;
using MarkdownRenderer.Layout.Internal.Vectors;

namespace MarkdownRenderer.Accessibility.Internal.Peers;

/// <summary>
/// Gives the child peer for each kind of <see cref="MarkdownFocusedChild"/>.
/// Each method returns null when the child has no peer in the current
/// document.
/// </summary>
/// <typeparam name="TPeer">The peer type; an automation peer in the renderer.</typeparam>
internal interface IMarkdownFocusedChildPeers<TPeer>
    where TPeer : class
{
    TPeer? ForLink(InlineContainerBox inline, LinkRun link);

    TPeer? ForLinkedImage(InlineContainerBox inline, InlineImageRun image);

    TPeer? ForInlineVectorScene(InlineVectorSceneRun scene);

    TPeer? ForVectorSemantic(VectorSceneBox box, int semanticIndex);

    TPeer? ForHorizontalOverflow(IHorizontalOverflowBox overflow);
}
