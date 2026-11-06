using System;
using System.Runtime.CompilerServices;
using System.Threading;
using MarkdownRenderer.Extensions;
using MarkdownRenderer.Layout;

namespace MarkdownRenderer.Performance;

/// <summary>
/// Memoizes the diagnostic native-resource weight of immutable vector scenes
/// for one scope (normally a <see cref="MarkdownPerformanceSession"/>). Command
/// graphs are immutable and may be visited by more than one layout snapshot
/// during resize/rebuild, so every residency policy of the scope shares this
/// cache. It keeps only the scalar estimate: the weak key must never become
/// another owner of a scene graph.
/// </summary>
internal sealed class MarkdownVectorSceneEstimateCache
{
    private readonly ConditionalWeakTable<MarkdownVectorScene, SceneResourceEstimate> _estimates = new();

    /// <summary>
    /// Returns the scene's memoized weight, walking its commands at most once
    /// per scope. <paramref name="estimateCount"/> is incremented only by the
    /// caller that performs the walk.
    /// </summary>
    internal long GetOrEstimate(
        MarkdownVectorScene scene,
        ref int estimateCount,
        CancellationToken cancellationToken)
    {
        // The warm path does not need a factory or the table's insertion path.
        if (_estimates.TryGetValue(scene, out SceneResourceEstimate? retained))
            return retained.GetOrEstimate(scene, ref estimateCount, cancellationToken);
        return _estimates.GetValue(
                scene,
                static _ => new SceneResourceEstimate())
            .GetOrEstimate(scene, ref estimateCount, cancellationToken);
    }

    /// <summary>
    /// Win2D does not expose allocation sizes for these device resources.
    /// This model is a relative diagnostic weight only, never a hard budget.
    /// </summary>
    private static long EstimatePreparedResourceBytes(MarkdownVectorScene scene, CancellationToken cancellationToken)
    {
        long estimate = 256;
        foreach (MarkdownVectorCommand command in scene.Commands)
        {
            cancellationToken.ThrowIfCancellationRequested();
            estimate = SaturatingAdd(estimate, 256);
            if (command.Path is { } path)
            {
                estimate = SaturatingAdd(estimate, 2_048);
                estimate = SaturatingAdd(estimate, SaturatingMultiply(path.Count, 96));
            }

            if (command.Kind == MarkdownVectorCommandKind.DrawText && command.Text is { } text)
            {
                estimate = SaturatingAdd(estimate, 4_096);
                estimate = SaturatingAdd(estimate, SaturatingMultiply(text.Length, 64));
            }

            if (command.Style is { } style && VectorSceneResourceShapes.RequiresStrokeStyle(style))
            {
                estimate = SaturatingAdd(estimate, 1_024);
                estimate = SaturatingAdd(
                    estimate,
                    SaturatingMultiply(style.DashArray.Count, sizeof(float)));
            }
        }

        estimate = SaturatingAdd(estimate, SaturatingMultiply(scene.Semantics.Count, 128));
        estimate = SaturatingAdd(estimate, SaturatingMultiply(scene.Links.Count, 96));
        return Math.Max(estimate, 1);
    }

    private static long SaturatingMultiply(int count, int weight)
        => count > 0 ? Math.Min(long.MaxValue, (long)count * weight) : 0;

    private static long SaturatingAdd(long left, long right)
        => left > long.MaxValue - right ? long.MaxValue : left + right;

    private sealed class SceneResourceEstimate
    {
        private long _estimatedBytes;
        private int _initialized;

        internal long GetOrEstimate(
            MarkdownVectorScene scene,
            ref int estimateCount,
            CancellationToken cancellationToken)
        {
            if (Volatile.Read(ref _initialized) != 0)
                return Volatile.Read(ref _estimatedBytes);

            lock (this)
            {
                if (_initialized == 0)
                {
                    long estimate = EstimatePreparedResourceBytes(scene, cancellationToken);
                    Interlocked.Increment(ref estimateCount);
                    Volatile.Write(ref _estimatedBytes, estimate);
                    Volatile.Write(ref _initialized, 1);
                }

                return Volatile.Read(ref _estimatedBytes);
            }
        }
    }
}
