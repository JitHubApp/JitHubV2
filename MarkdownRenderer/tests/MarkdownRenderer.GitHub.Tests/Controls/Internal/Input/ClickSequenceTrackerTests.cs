using Windows.Foundation;
using Xunit;
using MarkdownRenderer.Controls.Internal.Input;

namespace MarkdownRenderer.GitHub.Tests.Controls.Internal.Input;

public sealed class ClickSequenceTrackerTests
{
    private const int DoubleClickTimeMs = 500;
    private const long Start = 10_000;

    [Fact]
    public void PressesAtOneSpotWithinTheIntervalCountUpToThree()
    {
        var clicks = new ClickSequenceTracker(DoubleClickTimeMs);
        var point = new Point(100, 100);

        for (int press = 1; press <= 5; press++)
        {
            long tick = Start + press * 100;
            int count = clicks.RegisterPress(point, tick);
            clicks.AcceptPress(point, tick);
            Assert.Equal(System.Math.Min(press, 3), count);
        }
    }

    [Theory]
    [InlineData(3.9, 0, 500, 2)]
    [InlineData(4, 0, 500, 1)]
    [InlineData(0, 0, 501, 1)]
    public void ASecondPressContinuesOnlyNearTheFirstAndWithinTheInterval(
        double dx,
        double dy,
        long elapsedMs,
        int expected)
    {
        var clicks = new ClickSequenceTracker(DoubleClickTimeMs);
        var first = new Point(100, 100);
        clicks.RegisterPress(first, Start);
        clicks.AcceptPress(first, Start);

        int count = clicks.RegisterPress(new Point(first.X + dx, first.Y + dy), Start + elapsedMs);

        Assert.Equal(expected, count);
    }

    [Fact]
    public void AnUnacceptedPressDoesNotMoveTheTimingWindow()
    {
        var clicks = new ClickSequenceTracker(DoubleClickTimeMs);
        var point = new Point(100, 100);
        clicks.RegisterPress(point, Start);
        clicks.AcceptPress(point, Start);

        // A press that missed content is counted but not accepted, so the
        // next press is still measured from the first one.
        clicks.RegisterPress(point, Start + 400);

        Assert.Equal(1, clicks.RegisterPress(point, Start + 800));
    }

    [Fact]
    public void ResetStartsANewSequence()
    {
        var clicks = new ClickSequenceTracker(DoubleClickTimeMs);
        var point = new Point(100, 100);
        clicks.RegisterPress(point, Start);
        clicks.AcceptPress(point, Start);

        clicks.Reset();

        Assert.Equal(0, clicks.Count);
        Assert.Equal(1, clicks.RegisterPress(point, Start + 100));
    }
}
