using System;
using MarkdownRenderer.Layout.Internal.Boxes;
using MarkdownRenderer.Layout.Internal.Inlines;
using MarkdownRenderer.Layout.Internal.Inlines.Runs;
using MarkdownRenderer.Layout.Internal.Vectors;

namespace MarkdownRenderer.Accessibility.Internal.Peers;

/// <summary>
/// The painted item inside the document that holds the renderer's virtual
/// keyboard focus: a link, linked image or linked inline vector scene, a
/// focusable vector semantic, or a horizontal overflow region. The document
/// peer resolves it to a child peer both for the focus-changed event and for
/// a client's focused-element query, so the two always name the same peer.
/// Items backed by a real XAML element (hosted controls, the code-block copy
/// button) are not virtual children; their own peers answer for themselves.
/// </summary>
internal readonly struct MarkdownFocusedChild
{
    private readonly InlineContainerBox? _inline;
    private readonly InlineRun? _run;
    private readonly VectorSceneBox? _vector;
    private readonly int _semanticIndex;
    private readonly IHorizontalOverflowBox? _overflow;

    private MarkdownFocusedChild(
        InlineContainerBox? inline,
        InlineRun? run,
        VectorSceneBox? vector,
        int semanticIndex,
        IHorizontalOverflowBox? overflow)
    {
        _inline = inline;
        _run = run;
        _vector = vector;
        _semanticIndex = semanticIndex;
        _overflow = overflow;
    }

    /// <summary>No virtual child has focus; the document peer answers for itself.</summary>
    internal static MarkdownFocusedChild None => default;

    internal bool IsNone => _run is null && _vector is null && _overflow is null;

    /// <summary>A link, linked image or linked inline vector scene in <paramref name="inline"/>.</summary>
    internal static MarkdownFocusedChild ForLinkedAtomic(InlineContainerBox inline, InlineRun run)
    {
        ArgumentNullException.ThrowIfNull(inline);
        ArgumentNullException.ThrowIfNull(run);
        return new MarkdownFocusedChild(inline, run, null, -1, null);
    }

    internal static MarkdownFocusedChild ForVectorSemantic(VectorSceneBox box, int semanticIndex)
    {
        ArgumentNullException.ThrowIfNull(box);
        return new MarkdownFocusedChild(null, null, box, semanticIndex, null);
    }

    internal static MarkdownFocusedChild ForHorizontalOverflow(IHorizontalOverflowBox overflow)
    {
        ArgumentNullException.ThrowIfNull(overflow);
        return new MarkdownFocusedChild(null, null, null, -1, overflow);
    }

    /// <summary>
    /// The peer <paramref name="peers"/> gives for this child, or null when
    /// there is none and the document peer keeps focus.
    /// </summary>
    internal TPeer? ResolvePeer<TPeer>(IMarkdownFocusedChildPeers<TPeer> peers)
        where TPeer : class
    {
        ArgumentNullException.ThrowIfNull(peers);
        if (_run is not null)
        {
            return _run switch
            {
                LinkRun link => peers.ForLink(_inline!, link),
                InlineImageRun image => peers.ForLinkedImage(_inline!, image),
                InlineVectorSceneRun scene => peers.ForInlineVectorScene(scene),
                _ => null,
            };
        }

        if (_vector is not null)
            return peers.ForVectorSemantic(_vector, _semanticIndex);

        return _overflow is not null ? peers.ForHorizontalOverflow(_overflow) : null;
    }
}
