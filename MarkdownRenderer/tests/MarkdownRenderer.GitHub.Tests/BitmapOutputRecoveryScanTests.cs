using MarkdownRenderer.Controls;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

public sealed class BitmapOutputRecoveryScanTests
{
    [Fact]
    public void IdleScanDoesNoWork()
    {
        var scan = new BitmapOutputRecoveryScan();
        Assert.False(scan.TryTake(500, out _));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(32)]
    [InlineData(33)]
    [InlineData(500)]
    public void EveryOrdinalIsVisitedBeforeRecoveryStops(int count)
    {
        var scan = new BitmapOutputRecoveryScan();
        scan.Request();
        for (int ordinal = 0; ordinal < count; ordinal++)
        {
            Assert.True(scan.TryTake(count, out int actual));
            Assert.Equal(ordinal, actual);
            scan.Complete(needsRecovery: false);
            Assert.Equal(ordinal != count - 1, scan.IsPending);
        }
        Assert.False(scan.TryTake(count, out _));
    }

    [Fact]
    public void RegisteringOrFailedRequestKeepsAnotherCompleteCycleAlive()
    {
        var scan = new BitmapOutputRecoveryScan();
        scan.Request();
        for (int ordinal = 0; ordinal < 65; ordinal++)
        {
            Assert.True(scan.TryTake(65, out int actual));
            Assert.Equal(ordinal, actual);
            scan.Complete(needsRecovery: ordinal == 0);
        }
        Assert.True(scan.IsPending);
        for (int ordinal = 0; ordinal < 65; ordinal++)
        {
            Assert.True(scan.TryTake(65, out int actual));
            Assert.Equal(ordinal, actual);
            scan.Complete(needsRecovery: false);
        }
        Assert.False(scan.IsPending);
    }

    [Fact]
    public void NewSignalDoesNotStarveTailAndKeepsAlreadyVisitedOrdinalAlive()
    {
        var scan = new BitmapOutputRecoveryScan();
        scan.Request();
        for (int ordinal = 0; ordinal < 100; ordinal++)
        {
            Assert.True(scan.TryTake(100, out int actual));
            Assert.Equal(ordinal, actual);
            scan.Request();
            scan.Complete(needsRecovery: false);
        }
        Assert.True(scan.IsPending);
        Assert.True(scan.TryTake(100, out int first));
        Assert.Equal(0, first);
    }

    [Fact]
    public void ReplacementResetDoesNotRetainOldCursorOrPendingState()
    {
        var scan = new BitmapOutputRecoveryScan();
        scan.Request();
        Assert.True(scan.TryTake(500, out _));
        scan.Complete(needsRecovery: true);
        scan.Reset();
        Assert.False(scan.IsPending);
        scan.Request();
        Assert.True(scan.TryTake(1, out int first));
        Assert.Equal(0, first);
        scan.Complete(needsRecovery: false);
        Assert.False(scan.IsPending);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void EmptyPublicationEndsRecovery(int count)
    {
        var scan = new BitmapOutputRecoveryScan();
        scan.Request();
        Assert.False(scan.TryTake(count, out _));
        Assert.False(scan.IsPending);
    }
}
