using MarkdownRenderer.Diagnostics;
using System;
using System.Runtime.InteropServices;
using Xunit;

namespace MarkdownRenderer.Tests;

public class GraphicsDeviceErrorsTests
{
    [Theory]
    [InlineData(GraphicsDeviceErrors.DxgiErrorDeviceRemoved)]
    [InlineData(GraphicsDeviceErrors.DxgiErrorDeviceHung)]
    [InlineData(GraphicsDeviceErrors.DxgiErrorDeviceReset)]
    [InlineData(GraphicsDeviceErrors.DxgiErrorDriverInternalError)]
    [InlineData(GraphicsDeviceErrors.D2DErrorRecreateTarget)]
    [InlineData(GraphicsDeviceErrors.D3DErrorDeviceLost)]
    [InlineData(GraphicsDeviceErrors.D3DErrorDeviceNotReset)]
    public void IsDeviceLostHResult_KnownTransientGraphicsFailures_ReturnsTrue(int hresult)
    {
        Assert.True(GraphicsDeviceErrors.IsDeviceLostHResult(hresult));
    }

    [Fact]
    public void IsDeviceLost_WalksInnerExceptions()
    {
        var inner = new COMException("device removed", GraphicsDeviceErrors.DxgiErrorDeviceRemoved);
        var outer = new Exception("wrapper", inner);

        Assert.True(GraphicsDeviceErrors.IsDeviceLost(outer));
    }

    [Fact]
    public void IsDeviceLost_RecognizesSecondaryNestedPaintCleanupFailure()
    {
        var primary = new InvalidOperationException("paint failed");
        var cleanup = new COMException("native close failed", GraphicsDeviceErrors.D2DErrorRecreateTarget);
        var failures = new AggregateException(primary,
            new Exception("cleanup wrapper", new AggregateException(new Exception("unrelated"), cleanup)));

        Assert.True(GraphicsDeviceErrors.IsDeviceLost(failures));
        Assert.False(GraphicsDeviceErrors.IsShutdownOrDisposed(failures));
    }

    [Fact]
    public void IsShutdownOrDisposed_RecognizesSecondaryCleanupAndDoesNotHideUnrelatedFailure()
    {
        var primary = new InvalidOperationException("paint failed");
        Assert.True(GraphicsDeviceErrors.IsShutdownOrDisposed(new AggregateException(primary,
            new COMException("disconnected", GraphicsDeviceErrors.RpcErrorDisconnected))));
        Assert.True(GraphicsDeviceErrors.IsShutdownOrDisposed(new AggregateException(primary,
            new Exception("wrapper", new ObjectDisposedException("drawing session")))));
        Assert.False(GraphicsDeviceErrors.IsShutdownOrDisposed(new AggregateException(primary,
            new Exception("unrelated cleanup"))));
    }

    [Fact]
    public void FailureClassificationBoundsSharedAggregateGraphsWithoutAllocating()
    {
        Exception graph = new InvalidOperationException("not a graphics failure");
        for (int depth = 0; depth < 24; depth++)
            graph = new AggregateException(graph, graph);
        // An unbounded traversal would revisit tens of millions of shared
        // leaves. No Flatten, collection, pool or per-visit allocation is used.
        Assert.False(GraphicsDeviceErrors.IsDeviceLost(graph));
        Assert.False(GraphicsDeviceErrors.IsShutdownOrDisposed(graph));
        long before = GC.GetAllocatedBytesForCurrentThread();
        bool deviceLost = GraphicsDeviceErrors.IsDeviceLost(graph);
        bool shutdown = GraphicsDeviceErrors.IsShutdownOrDisposed(graph);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.False(deviceLost);
        Assert.False(shutdown);
        Assert.Equal(0, allocated);
    }

    [Fact]
    public void FailureClassificationInspectsSiblingCleanupBeforeLargePrimaryGraph()
    {
        Exception primary = new InvalidOperationException("unrelated primary failure");
        for (int depth = 0; depth < 24; depth++)
            primary = new AggregateException(primary, primary);

        Assert.True(GraphicsDeviceErrors.IsDeviceLost(new AggregateException(primary,
            new COMException("cleanup lost device", GraphicsDeviceErrors.DxgiErrorDeviceRemoved))));
        Assert.True(GraphicsDeviceErrors.IsShutdownOrDisposed(new AggregateException(primary,
            new ObjectDisposedException("drawing session"))));
    }

    [Fact]
    public void FailureClassificationFairlyInspectsWrappedCleanupBesideLargePrimaryGraphWithoutAllocating()
    {
        Exception primary = new InvalidOperationException("unrelated primary failure");
        for (int depth = 0; depth < 24; depth++)
            primary = new AggregateException(primary, primary);
        var deviceFailure = new AggregateException(primary, new AggregateException(
            new Exception("cleanup wrapper",
                new COMException("cleanup lost device", GraphicsDeviceErrors.DxgiErrorDeviceRemoved))));
        var shutdownFailure = new AggregateException(primary, new AggregateException(
            new Exception("cleanup wrapper", new ObjectDisposedException("drawing session"))));

        Assert.True(GraphicsDeviceErrors.IsDeviceLost(deviceFailure));
        Assert.True(GraphicsDeviceErrors.IsShutdownOrDisposed(shutdownFailure));
        Assert.False(GraphicsDeviceErrors.IsShutdownOrDisposed(deviceFailure));
        Assert.False(GraphicsDeviceErrors.IsDeviceLost(shutdownFailure));
        long before = GC.GetAllocatedBytesForCurrentThread();
        bool deviceLost = GraphicsDeviceErrors.IsDeviceLost(deviceFailure);
        bool shutdown = GraphicsDeviceErrors.IsShutdownOrDisposed(shutdownFailure);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(deviceLost);
        Assert.True(shutdown);
        Assert.Equal(0, allocated);
    }

    [Fact]
    public void IsDeviceLostHResult_NonGraphicsFailure_ReturnsFalse()
    {
        Assert.False(GraphicsDeviceErrors.IsDeviceLostHResult(unchecked((int)0x80004005)));
    }

    [Theory]
    [InlineData(GraphicsDeviceErrors.RpcErrorDisconnected)]
    [InlineData(GraphicsDeviceErrors.RpcErrorServerDied)]
    [InlineData(GraphicsDeviceErrors.RpcErrorServerDiedDne)]
    [InlineData(GraphicsDeviceErrors.RoErrorClosed)]
    [InlineData(GraphicsDeviceErrors.Win32ErrorInvalidHandle)]
    [InlineData(GraphicsDeviceErrors.Win32ErrorInvalidWindowHandle)]
    public void IsShutdownHResult_KnownTeardownFailures_ReturnsTrue(int hresult)
    {
        Assert.True(GraphicsDeviceErrors.IsShutdownHResult(hresult));
    }

    [Fact]
    public void IsShutdownOrDisposed_TreatsObjectDisposedAsShutdown()
    {
        Assert.True(GraphicsDeviceErrors.IsShutdownOrDisposed(new ObjectDisposedException("canvas")));
    }

    [Fact]
    public void IsShutdownOrDisposed_DoesNotHideUnrelatedComFailure()
    {
        Assert.False(GraphicsDeviceErrors.IsShutdownOrDisposed(
            new COMException("unrelated failure", unchecked((int)0x80004005))));
    }

    [Fact]
    public void FormatHResult_UsesUnsignedHex()
    {
        Assert.Equal("0x887A0005", GraphicsDeviceErrors.FormatHResult(GraphicsDeviceErrors.DxgiErrorDeviceRemoved));
    }
}
