using MarkdownRenderer.PerformanceHarness;
using Xunit;

namespace MarkdownRenderer.Tests;

public sealed class MonotonicUtcClockTests
{
    private static readonly DateTimeOffset Anchor =
        DateTimeOffset.Parse("2026-09-10T12:00:00-07:00");

    [Fact]
    public void ConvertsElapsedTimestampTicksFromOneUtcAnchor()
    {
        long timestamp = 1_000;
        var clock = new MonotonicUtcClock(
            Anchor,
            timestampAnchor: timestamp,
            timestampFrequency: 1_000,
            getTimestamp: () => timestamp);

        DateTimeOffset first = clock.GetUtcNow();
        timestamp = 1_500;
        DateTimeOffset second = clock.GetUtcNow();

        Assert.Equal(Anchor.ToUniversalTime(), first);
        Assert.Equal(Anchor.ToUniversalTime().AddMilliseconds(500), second);
        Assert.Equal(TimeSpan.Zero, first.Offset);
        Assert.Equal(TimeSpan.Zero, second.Offset);
    }

    [Fact]
    public void StalledOrRegressingTimestampSourceStillEmitsStrictlyIncreasingUtc()
    {
        long[] timestamps = [100, 90, 90, 101];
        int nextTimestamp = -1;
        var clock = new MonotonicUtcClock(
            Anchor,
            timestampAnchor: 100,
            timestampFrequency: TimeSpan.TicksPerSecond,
            getTimestamp: () => timestamps[++nextTimestamp]);

        DateTimeOffset[] emitted = Enumerable.Range(0, timestamps.Length)
            .Select(_ => clock.GetUtcNow())
            .ToArray();

        Assert.All(emitted, value => Assert.Equal(TimeSpan.Zero, value.Offset));
        for (int index = 1; index < emitted.Length; index++)
            Assert.True(emitted[index] > emitted[index - 1]);
    }

    [Fact]
    public async Task ConcurrentCallersReceiveUniqueStrictlyOrderedInstants()
    {
        const int callerCount = 256;
        var clock = new MonotonicUtcClock(
            Anchor,
            timestampAnchor: 42,
            timestampFrequency: TimeSpan.TicksPerSecond,
            getTimestamp: static () => 42);

        DateTimeOffset[] emitted = await Task.WhenAll(
            Enumerable.Range(0, callerCount)
                .Select(_ => Task.Run(clock.GetUtcNow)));
        long[] orderedTicks = emitted.Select(static value => value.UtcDateTime.Ticks)
            .Order()
            .ToArray();

        Assert.Equal(callerCount, orderedTicks.Distinct().Count());
        Assert.Equal(
            Enumerable.Range(0, callerCount).Select(offset =>
                Anchor.ToUniversalTime().UtcDateTime.Ticks + offset),
            orderedTicks);
    }

    [Fact]
    public void SaturatesSafelyAndFailsInsteadOfRepeatingMaximumInstant()
    {
        DateTimeOffset nearMaximum = DateTimeOffset.MaxValue.AddTicks(-1);
        var clock = new MonotonicUtcClock(
            nearMaximum,
            timestampAnchor: long.MinValue,
            timestampFrequency: 1,
            getTimestamp: static () => long.MaxValue);

        Assert.Equal(DateTimeOffset.MaxValue, clock.GetUtcNow());
        Assert.Throws<InvalidOperationException>(() => clock.GetUtcNow());
    }

    [Fact]
    public void RejectsInvalidClockDependencies()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new MonotonicUtcClock(Anchor, 0, 0, static () => 0));
        Assert.Throws<ArgumentNullException>(() =>
            new MonotonicUtcClock(Anchor, 0, 1, null!));
    }
}
