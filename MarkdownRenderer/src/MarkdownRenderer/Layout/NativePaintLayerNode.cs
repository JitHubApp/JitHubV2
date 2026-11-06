using Microsoft.Graphics.Canvas;

namespace MarkdownRenderer.Layout;

/// <summary>
/// Pooled custody record for one active paint layer of a tracked drawing
/// session. A retired node is never returned to the pool.
/// </summary>
internal sealed class NativePaintLayerNode
{
    internal NativePaintLayerNode(NativeResourceRetirementRecord retirement) => Retirement = retirement;
    internal NativeResourceRetirementRecord Retirement { get; }
    internal NativePaintLayerNode? PoolNext;
    internal NativePaintLayerNode? StackNext;
    internal CanvasActiveLayer? Layer;
    internal NativePaintSessionResources.AuxiliaryLease ClipOwner;
    internal long ClipOwnerGeneration;
    internal long Generation;
    internal bool Active;
    internal bool FactoryPending;
    internal bool Retired;
}
