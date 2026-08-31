using System;
using System.Diagnostics;

namespace JitHub.Services.CodeViewer;

public sealed class UiWorkBudget
{
    public static readonly TimeSpan DefaultSlice = TimeSpan.FromMilliseconds(4);

    private readonly long _maximumTicks;
    private long _sliceStarted;

    public UiWorkBudget(TimeSpan? maximumSlice = null)
        : this(maximumSlice, Stopwatch.GetTimestamp())
    {
    }

    internal UiWorkBudget(TimeSpan? maximumSlice, long sliceStarted)
    {
        TimeSpan slice = maximumSlice ?? DefaultSlice;
        if (slice <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(maximumSlice));

        _maximumTicks = Math.Max(1, (long)(slice.TotalSeconds * Stopwatch.Frequency));
        _sliceStarted = sliceStarted;
    }

    public bool ShouldYield() => ShouldYield(Stopwatch.GetTimestamp());

    internal bool ShouldYield(long now)
    {
        if (now - _sliceStarted < _maximumTicks) return false;
        _sliceStarted = now;
        return true;
    }

    public void Restart() => _sliceStarted = Stopwatch.GetTimestamp();
}
