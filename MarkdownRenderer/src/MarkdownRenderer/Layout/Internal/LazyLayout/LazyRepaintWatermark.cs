using System;
using System.Threading;

namespace MarkdownRenderer.Layout.Internal.LazyLayout;

/// <summary>
/// The lowest document Y whose painted pixels a lazy band commit may have
/// changed since the UI last took it. Geometry above it is untouched, so a
/// commit that measures below the viewport repaints nothing on screen.
/// </summary>
/// <remarks>
/// Workers lower it under the layout gate; the UI thread takes it without
/// the gate. Every commit lowers it before its completion is dispatched, so a
/// commit whose completion is later dropped is still repainted by the next
/// one that publishes.
/// </remarks>
internal sealed class LazyRepaintWatermark
{
    private long _topBits = BitConverter.DoubleToInt64Bits(double.PositiveInfinity);

    /// <summary>Records that pixels at or below <paramref name="top"/> may have changed.</summary>
    internal void Lower(double top)
    {
        if (double.IsNaN(top))
            top = 0;

        long observed = Volatile.Read(ref _topBits);
        while (top < BitConverter.Int64BitsToDouble(observed))
        {
            long previous = Interlocked.CompareExchange(
                ref _topBits, BitConverter.DoubleToInt64Bits(top), observed);
            if (previous == observed)
                return;
            observed = previous;
        }
    }

    /// <summary>
    /// Returns the lowest changed Y and resets it, or positive infinity when
    /// nothing changed since the last call.
    /// </summary>
    internal double Take() => BitConverter.Int64BitsToDouble(
        Interlocked.Exchange(ref _topBits, BitConverter.DoubleToInt64Bits(double.PositiveInfinity)));
}
