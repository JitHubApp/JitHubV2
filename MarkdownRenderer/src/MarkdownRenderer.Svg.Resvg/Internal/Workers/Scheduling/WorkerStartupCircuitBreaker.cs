namespace MarkdownRenderer.Svg.Resvg.Internal.Workers.Scheduling;

/// <summary>
/// Disables worker startup for 60 seconds after three startup failures within
/// 60 seconds. Like <see cref="WorkerSchedulingQueue{T}"/>, it holds no lock:
/// the scheduler calls every member under its state lock.
/// </summary>
internal sealed class WorkerStartupCircuitBreaker
{
    private readonly Queue<DateTimeOffset> _failures = new();
    private DateTimeOffset _disabledUntil;

    /// <summary>Records one failed startup and opens the breaker on the third recent failure.</summary>
    /// <param name="now">The failure time.</param>
    internal void RecordFailure(DateTimeOffset now)
    {
        while (_failures.TryPeek(out DateTimeOffset oldest) &&
               now - oldest > TimeSpan.FromSeconds(60))
        {
            _failures.Dequeue();
        }
        _failures.Enqueue(now);
        if (_failures.Count >= 3)
        {
            _disabledUntil = now.AddSeconds(60);
            _failures.Clear();
        }
    }

    /// <summary>Whether startup is still disabled.</summary>
    /// <param name="now">The current time.</param>
    /// <returns>True while the breaker is open.</returns>
    internal bool IsOpen(DateTimeOffset now) => _disabledUntil > now;
}
