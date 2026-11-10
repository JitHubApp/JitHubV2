using System.Runtime.CompilerServices;
using MarkdownRenderer.Controls;
using MarkdownRenderer.Controls.Internal.Recovery;
using MarkdownRenderer.Controls.Internal.Threading;
using MarkdownRenderer.Controls.Internal.View;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>
/// The renderer's recovery clock runs one repeating 100 ms UI timer that asks
/// its host to retry deferred work, falls back to a weak managed timer when the
/// UI timer cannot be created, and never keeps the renderer alive.
/// </summary>
public sealed class RecoveryClockTests
{
    [Fact]
    public void StartRunsOneRepeatingHundredMillisecondTimerThatRetriesOnEveryTick()
    {
        var scheduler = new ManualUiScheduler();
        var host = new CountingHost();
        var clock = new RecoveryClock(host, LoadedLifetime(), scheduler);

        clock.Start();
        clock.Start();

        ManualUiTimer timer = Assert.Single(scheduler.Timers);
        Assert.True(clock.IsRunning);
        Assert.True(timer.IsRunning);
        Assert.True(timer.IsRepeating);
        Assert.Equal(TimeSpan.FromMilliseconds(100), timer.Interval);

        timer.Fire();
        timer.Fire();

        Assert.Equal(2, host.Ticks);
    }

    [Fact]
    public void StoppedClockIgnoresAndStopsALateTickFromItsOldTimer()
    {
        var scheduler = new ManualUiScheduler();
        var host = new CountingHost();
        var clock = new RecoveryClock(host, LoadedLifetime(), scheduler);
        clock.Start();
        ManualUiTimer first = scheduler.Timers[0];

        clock.Stop();
        Assert.False(clock.IsRunning);
        Assert.False(first.IsRunning);

        clock.Start();
        ManualUiTimer second = scheduler.Timers[1];
        first.Fire();

        Assert.Equal(0, host.Ticks);
        Assert.Equal(2, first.StopCount);
        Assert.True(second.IsRunning);
    }

    [Fact]
    public void ClockWithoutAUiSchedulerNeverRuns()
    {
        var clock = new RecoveryClock(new CountingHost(), LoadedLifetime(), scheduler: null);

        clock.Start();

        Assert.False(clock.IsRunning);
    }

    [Fact]
    public void FailedUiTimerFallsBackToAManagedTimerThatPostsTicksToTheUiThread()
    {
        var scheduler = new ManualUiScheduler { FailTimerCreation = true };
        var host = new CountingHost();
        var clock = new RecoveryClock(host, LoadedLifetime(), scheduler);

        clock.Start();
        Assert.True(clock.IsRunning);
        Assert.Empty(scheduler.Timers);

        scheduler.WaitForPending();
        Assert.Equal(MarkdownUiPriority.Normal, scheduler.Posted[0]);
        Assert.Equal(0, host.Ticks);
        scheduler.RunPending();
        Assert.Equal(1, host.Ticks);

        clock.Stop();
        Assert.False(clock.IsRunning);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedUiTimerHasNoFallbackOnceTheViewIsUnloadedOrDisposed(bool disposed)
    {
        var lifetime = LoadedLifetime();
        if (disposed)
            lifetime.MarkDisposing();
        else
            lifetime.MarkUnloaded();
        var clock = new RecoveryClock(new CountingHost(), lifetime, new ManualUiScheduler { FailTimerCreation = true });

        clock.Start();

        Assert.False(clock.IsRunning);
    }

    [Fact]
    public void RunningTimerDoesNotKeepTheClockAlive()
    {
        var scheduler = new ManualUiScheduler();
        var host = new CountingHost();
        WeakReference clock = StartUnreferencedClock(host, scheduler);

        for (int attempt = 0; attempt < 10 && clock.IsAlive; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
        Assert.False(clock.IsAlive);

        ManualUiTimer timer = Assert.Single(scheduler.Timers);
        timer.Fire();

        Assert.Equal(0, host.Ticks);
        Assert.False(timer.IsRunning);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference StartUnreferencedClock(CountingHost host, ManualUiScheduler scheduler)
    {
        var clock = new RecoveryClock(host, LoadedLifetime(), scheduler);
        clock.Start();
        return new WeakReference(clock);
    }

    private static MarkdownViewLifetime LoadedLifetime()
    {
        var lifetime = new MarkdownViewLifetime();
        lifetime.MarkLoaded();
        return lifetime;
    }

    private sealed class CountingHost : IRecoveryClockHost
    {
        internal int Ticks { get; private set; }

        public void ProcessRecoveryTick() => Ticks++;
    }
}
