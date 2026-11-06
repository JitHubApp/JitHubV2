using System.Threading;
using MarkdownRenderer.Controls;
using Xunit;

namespace MarkdownRenderer.Tests;

public sealed class ThreadCpuClockTests
{
    [Fact]
    public void CurrentThreadCpuClock_ReportsMonotonicTimeOnWindows()
    {
        if (!OperatingSystem.IsWindows())
            return;

        long start = ThreadCpuClock.ReadCurrentThreadTicks();
        Thread.SpinWait(1_000_000);
        long end = ThreadCpuClock.ReadCurrentThreadTicks();

        Assert.True(start >= 0);
        Assert.True(end >= start);
        Assert.True(ThreadCpuClock.ElapsedMilliseconds(start, end) >= 0);
    }

    [Fact]
    public void ElapsedMilliseconds_UsesUnavailableSentinelForInvalidSamples()
    {
        Assert.Equal(-1, ThreadCpuClock.ElapsedMilliseconds(-1, 100));
        Assert.Equal(-1, ThreadCpuClock.ElapsedMilliseconds(100, 99));
        Assert.Equal(1, ThreadCpuClock.ElapsedMilliseconds(0, 10_000));
    }
}
