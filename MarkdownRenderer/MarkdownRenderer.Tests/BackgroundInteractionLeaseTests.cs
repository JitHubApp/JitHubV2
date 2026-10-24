using MarkdownRenderer.Sample;
using Xunit;

namespace MarkdownRenderer.Tests;

public sealed class BackgroundInteractionLeaseTests
{
    [Fact]
    public async Task ActualMonitorIsHeldAndReleasedOnTheSameNonCallerThread()
    {
        object gate = new();
        int caller = Environment.CurrentManagedThreadId;
        int acquisitionThread = 0;
        int releaseThread = 0;
        var lease = new BackgroundInteractionLease(
            () => { acquisitionThread = Environment.CurrentManagedThreadId; return Monitor.TryEnter(gate); },
            () => { releaseThread = Environment.CurrentManagedThreadId; Monitor.Exit(gate); });
        try
        {
            Assert.True(await lease.Acquired.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.NotEqual(caller, acquisitionThread);
            Assert.False(Monitor.TryEnter(gate));
        }
        finally { lease.Release(); }
        Assert.True(await lease.Completed.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Null(lease.Failure);
        Assert.Equal(acquisitionThread, releaseThread);
        Assert.True(Monitor.TryEnter(gate));
        Monitor.Exit(gate);
    }

    [Fact]
    public async Task AbandonedHoldTimesOutButAlwaysReleasesActualMonitor()
    {
        object gate = new();
        var lease = new BackgroundInteractionLease(() => Monitor.TryEnter(gate), () => Monitor.Exit(gate), holdTimeoutMilliseconds: 30);
        Assert.True(await lease.Acquired.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.False(await lease.Completed.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.IsType<TimeoutException>(lease.Failure);
        Assert.True(Monitor.TryEnter(gate));
        Monitor.Exit(gate);
    }

    [Fact]
    public async Task FailedAcquisitionDoesNotCallEnd()
    {
        int endCalls = 0;
        var lease = new BackgroundInteractionLease(() => false, () => Interlocked.Increment(ref endCalls), acquisitionTimeoutMilliseconds: 20);
        Assert.False(await lease.Acquired.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.False(await lease.Completed.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.IsType<TimeoutException>(lease.Failure);
        Assert.Equal(0, endCalls);
    }

    [Fact]
    public async Task ReleaseIsIdempotentAndCanCancelPendingAcquisition()
    {
        int endCalls = 0;
        var lease = new BackgroundInteractionLease(() => false, () => Interlocked.Increment(ref endCalls));
        lease.Release();
        lease.Release();
        Assert.False(await lease.Acquired.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(await lease.Completed.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Null(lease.Failure);
        Assert.Equal(0, endCalls);
    }

    [Fact]
    public async Task AcquisitionAndReleaseExceptionsBecomeObservableFailures()
    {
        var acquisition = new BackgroundInteractionLease(() => throw new InvalidOperationException("begin"), () => throw new Exception("must not end"));
        Assert.False(await acquisition.Acquired.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.False(await acquisition.Completed.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal("begin", acquisition.Failure!.Message);

        var release = new BackgroundInteractionLease(() => true, () => throw new InvalidOperationException("end"));
        Assert.True(await release.Acquired.WaitAsync(TimeSpan.FromSeconds(5)));
        release.Release();
        Assert.False(await release.Completed.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal("end", release.Failure!.Message);
    }

    [Theory]
    [InlineData(0, 10)]
    [InlineData(2001, 10)]
    [InlineData(10, 0)]
    [InlineData(10, 15001)]
    public void DeadlinesCannotBeDisabledOrRaised(int acquisition, int hold)
        => Assert.Throws<ArgumentOutOfRangeException>(() => new BackgroundInteractionLease(() => false, () => { }, acquisition, hold));
}
