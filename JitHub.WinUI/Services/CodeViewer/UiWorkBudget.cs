using System;
using System.Diagnostics;

namespace JitHub.Services.CodeViewer;

public sealed class UiWorkBudget
{
    public static readonly TimeSpan DefaultSlice = TimeSpan.FromMilliseconds(4);

    private readonly long _maximumTicks;
    private long _sliceStarted;

    public UiWorkBudget(TimeSpan? maximumSlice = null)
    {
        TimeSpan slice = maximumSlice ?? DefaultSlice;
        if (slice <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(maximumSlice));

        _maximumTicks = Math.Max(1, (long)(slice.TotalSeconds * Stopwatch.Frequency));
        _sliceStarted = Stopwatch.GetTimestamp();
    }

    public bool ShouldYield()
    {
        long now = Stopwatch.GetTimestamp();
        if (now - _sliceStarted < _maximumTicks) return false;
        _sliceStarted = now;
        return true;
    }

    public void Restart() => _sliceStarted = Stopwatch.GetTimestamp();
}
