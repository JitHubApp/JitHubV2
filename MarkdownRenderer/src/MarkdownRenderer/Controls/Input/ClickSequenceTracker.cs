using Windows.Foundation;

namespace MarkdownRenderer.Controls.Input;

/// <summary>
/// Counts consecutive presses at one spot within the system double-click
/// time: one selects by character, two by word, three by block.
/// </summary>
internal sealed class ClickSequenceTracker
{
    /// <summary>Presses closer than this, in DIPs, count as the same spot.</summary>
    private const double SameSpotRadius = 4;

    private const int MaxCount = 3;

    private static readonly int SystemDoubleClickTimeMs = SystemInputMetrics.GetDoubleClickTimeMs();

    private readonly int _doubleClickTimeMs;
    private long _lastPressTickMs;
    private Point _lastPressPoint;

    /// <summary>Uses the system double-click time.</summary>
    public ClickSequenceTracker()
        : this(SystemDoubleClickTimeMs)
    {
    }

    public ClickSequenceTracker(int doubleClickTimeMs)
    {
        _doubleClickTimeMs = doubleClickTimeMs;
    }

    /// <summary>
    /// Presses in the current sequence, from 1 to 3. Zero after a reset.
    /// Four or more presses stay at 3.
    /// </summary>
    public int Count { get; private set; }

    /// <summary>
    /// Counts a press. It continues the sequence when it is near the last
    /// accepted press and within the double-click time, and starts a new one
    /// otherwise.
    /// </summary>
    /// <returns>The updated <see cref="Count"/>.</returns>
    public int RegisterPress(Point point, long tickMs)
    {
        double dx = point.X - _lastPressPoint.X;
        double dy = point.Y - _lastPressPoint.Y;
        bool sameSpot = dx * dx + dy * dy < SameSpotRadius * SameSpotRadius;
        bool withinTime = (tickMs - _lastPressTickMs) <= _doubleClickTimeMs;
        if (sameSpot && withinTime)
        {
            if (Count < MaxCount)
                Count++;
        }
        else
        {
            Count = 1;
        }

        return Count;
    }

    /// <summary>
    /// Makes a press the reference for the next one. Called only when the
    /// press hit content, so a miss at the same spot does not extend the
    /// timing window.
    /// </summary>
    public void AcceptPress(Point point, long tickMs)
    {
        _lastPressTickMs = tickMs;
        _lastPressPoint = point;
    }

    /// <summary>Ends the sequence, so the next press starts a new one.</summary>
    public void Reset()
    {
        Count = 0;
        _lastPressTickMs = 0;
        _lastPressPoint = default;
    }
}
