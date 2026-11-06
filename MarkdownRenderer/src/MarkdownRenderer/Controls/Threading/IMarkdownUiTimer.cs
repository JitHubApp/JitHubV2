using System;

namespace MarkdownRenderer.Controls.Threading;

/// <summary>A timer created by <see cref="IMarkdownUiScheduler"/> that ticks on the UI thread.</summary>
internal interface IMarkdownUiTimer
{
    /// <summary>The time between ticks.</summary>
    TimeSpan Interval { get; set; }

    /// <summary>True to keep ticking until stopped; false to tick once per start.</summary>
    bool IsRepeating { get; set; }

    /// <summary>Raised on the UI thread on each tick. The sender is this timer.</summary>
    event EventHandler? Tick;

    void Start();

    void Stop();
}
