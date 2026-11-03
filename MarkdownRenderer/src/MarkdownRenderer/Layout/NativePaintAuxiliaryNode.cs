using System;

namespace MarkdownRenderer.Layout;

/// <summary>
/// Pooled custody record for one native resource, such as a clip geometry,
/// that paint layers of a tracked drawing session depend on.
/// </summary>
internal sealed class NativePaintAuxiliaryNode
{
    internal NativePaintAuxiliaryNode(NativeResourceRetirementRecord retirement) => Retirement = retirement;
    internal NativeResourceRetirementRecord Retirement { get; }
    internal NativePaintAuxiliaryNode? PoolNext;
    internal NativePaintAuxiliaryNode? ActiveNext;
    internal IDisposable? Resource;
    internal long Generation;
    internal bool Active;
    internal bool FactoryPending;
    internal bool DependentLayerOpen;
    internal bool Retired;
}
