using System;
using System.Threading;
using MarkdownRenderer.Layout.Internal.Inlines.Runs;
using MarkdownRenderer.Layout.Internal.Vectors;
using MarkdownRenderer.Layout.Internal.Vectors.Drawing;

namespace MarkdownRenderer.Performance;

/// <summary>
/// A scene owner (block or inline run) pinned by the residency policy, with
/// its diagnostic weight.
/// </summary>
internal readonly struct VectorSceneResidencyEntry(object Owner, long EstimatedBytes)
{
    public object Owner { get; } = Owner;
    public long EstimatedBytes { get; } = EstimatedBytes;

    internal bool IsPrepared => Owner switch
    {
        VectorSceneBox box => box.IsNativeResourcesPrepared,
        InlineVectorSceneRun run => run.IsNativeResourcesPrepared,
        _ => false,
    };

    internal bool IsCapacityFallback => Owner switch
    {
        VectorSceneBox box => box.IsCapacityFallbackPaintable,
        InlineVectorSceneRun run => run.IsCapacityFallbackPaintable,
        _ => false,
    };

    internal bool IsCapacityDeferred => Owner switch
    {
        VectorSceneBox box => box.NativePreparationStatus == VectorNativePreparationStatus.DeferredCapacity,
        InlineVectorSceneRun run => run.NativePreparationStatus == VectorNativePreparationStatus.DeferredCapacity,
        _ => false,
    };

    internal void Prepare(CancellationToken cancellationToken)
    {
        switch (Owner)
        {
            case VectorSceneBox box:
                box.PrepareNativeResources(cancellationToken);
                break;
            case InlineVectorSceneRun run:
                run.PrepareNativeResources(cancellationToken);
                break;
            default:
                throw new InvalidOperationException("The residency band contains an unsupported scene owner.");
        }
    }

    internal void Release()
    {
        switch (Owner)
        {
            case VectorSceneBox box:
                box.ReleaseNativeResources();
                break;
            case InlineVectorSceneRun run:
                run.ReleaseNativeResources();
                break;
        }
    }
}
