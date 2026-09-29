using System.Diagnostics;

namespace MarkdownRenderer.PerformanceHarness;

/// <summary>
/// Produces UTC evidence timestamps from one wall-clock anchor and a monotonic
/// timestamp source. Values are serialized through one gate so every emitted
/// instant is strictly later than the previous instant, even when the source
/// stalls or a test source moves backwards.
/// </summary>
internal sealed class MonotonicUtcClock
{
    private static readonly long MaximumUtcTicks = DateTimeOffset.MaxValue.UtcDateTime.Ticks;

    private readonly object _gate = new();
    private readonly long _anchorUtcTicks;
    private readonly long _anchorTimestamp;
    private readonly long _timestampFrequency;
    private readonly Func<long> _getTimestamp;
    private long _lastEmittedUtcTicks;

    internal MonotonicUtcClock()
        : this(
            DateTimeOffset.UtcNow,
            Stopwatch.GetTimestamp(),
            Stopwatch.Frequency,
            Stopwatch.GetTimestamp)
    {
    }

    internal MonotonicUtcClock(
        DateTimeOffset utcAnchor,
        long timestampAnchor,
        long timestampFrequency,
        Func<long> getTimestamp)
    {
        if (timestampFrequency <= 0)
            throw new ArgumentOutOfRangeException(nameof(timestampFrequency));

        _getTimestamp = getTimestamp ?? throw new ArgumentNullException(nameof(getTimestamp));
        _anchorUtcTicks = utcAnchor.ToUniversalTime().UtcDateTime.Ticks;
        _anchorTimestamp = timestampAnchor;
        _timestampFrequency = timestampFrequency;
        _lastEmittedUtcTicks = _anchorUtcTicks - 1;
    }

    internal DateTimeOffset GetUtcNow()
    {
        lock (_gate)
        {
            long candidateTicks = GetCandidateUtcTicks(_getTimestamp());
            if (_lastEmittedUtcTicks == MaximumUtcTicks)
            {
                throw new InvalidOperationException(
                    "The monotonic UTC clock exhausted the DateTimeOffset range.");
            }

            long nextTicks = Math.Max(candidateTicks, _lastEmittedUtcTicks + 1);
            _lastEmittedUtcTicks = nextTicks;
            return new DateTimeOffset(nextTicks, TimeSpan.Zero);
        }
    }

    private long GetCandidateUtcTicks(long timestamp)
    {
        decimal elapsedTimestampTicks = (decimal)timestamp - _anchorTimestamp;
        if (elapsedTimestampTicks <= 0)
            return _anchorUtcTicks;

        decimal elapsedUtcTicks = elapsedTimestampTicks * TimeSpan.TicksPerSecond /
            _timestampFrequency;
        decimal availableUtcTicks = MaximumUtcTicks - _anchorUtcTicks;
        if (elapsedUtcTicks >= availableUtcTicks)
            return MaximumUtcTicks;

        return _anchorUtcTicks + decimal.ToInt64(decimal.Floor(elapsedUtcTicks));
    }
}
