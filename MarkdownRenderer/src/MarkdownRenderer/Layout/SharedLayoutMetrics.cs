using System;
using System.Threading;

namespace MarkdownRenderer.Layout;

/// <summary>
/// Concurrent reusable height observations. A zero slot means no observation;
/// positive finite heights are published atomically after successful measure.
/// </summary>
internal sealed class SharedLayoutMetrics
{
    private readonly float[] _heights;

    internal SharedLayoutMetrics(int topLevelBlockCount, long estimatedKeyWeightBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(topLevelBlockCount);
        _heights = topLevelBlockCount == 0
            ? Array.Empty<float>()
            : new float[topLevelBlockCount];
        EstimatedWeightBytes = AddSaturating(
            Math.Max(1, estimatedKeyWeightBytes),
            128L + ((long)_heights.Length * sizeof(float)));
    }

    internal int TopLevelBlockCount => _heights.Length;

    internal readonly long EstimatedWeightBytes;

    internal bool TryGetHeight(int ordinal, out float height)
    {
        if ((uint)ordinal >= (uint)_heights.Length)
        {
            height = 0;
            return false;
        }

        height = Volatile.Read(ref _heights[ordinal]);
        return float.IsFinite(height) && height > 0;
    }

    internal void RecordHeight(int ordinal, float height)
    {
        if ((uint)ordinal >= (uint)_heights.Length ||
            !float.IsFinite(height) ||
            height <= 0)
        {
            return;
        }

        Volatile.Write(ref _heights[ordinal], height);
    }

    private static long AddSaturating(long left, long right) =>
        left >= long.MaxValue - right ? long.MaxValue : left + right;
}
