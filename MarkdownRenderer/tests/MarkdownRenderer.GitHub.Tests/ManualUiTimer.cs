using MarkdownRenderer.Controls.Internal.Threading;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>A UI timer from <see cref="ManualUiScheduler"/> that ticks only when the test fires it.</summary>
internal sealed class ManualUiTimer : IMarkdownUiTimer
{
    public TimeSpan Interval { get; set; }

    public bool IsRepeating { get; set; }

    internal bool IsRunning { get; private set; }

    internal int StartCount { get; private set; }

    internal int StopCount { get; private set; }

    public event EventHandler? Tick;

    public void Start()
    {
        StartCount++;
        IsRunning = true;
    }

    public void Stop()
    {
        StopCount++;
        IsRunning = false;
    }

    /// <summary>Raises one tick, as the dispatcher would, whether or not the timer runs.</summary>
    internal void Fire() => Tick?.Invoke(this, EventArgs.Empty);
}
