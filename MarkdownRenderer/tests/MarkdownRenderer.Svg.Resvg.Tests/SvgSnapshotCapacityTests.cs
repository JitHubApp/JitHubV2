using System.Runtime.CompilerServices;
using MarkdownRenderer.Images;
using MarkdownRenderer.Svg.Resvg.Internal;
using Xunit;

namespace MarkdownRenderer.Svg.Resvg.Tests;

/// <summary>
/// Pins the process snapshot admission counter itself: priority-tier
/// headroom, growth and shrink accounting, capacity versions, threshold
/// waits, and the allocation-free release path.
/// </summary>
public sealed class SvgSnapshotCapacityTests
{
    [Theory]
    [InlineData(32, 8)]
    [InlineData(64, 16)]
    public void OverscanRespectsHeadroomWhileVisibleCanUseTheWholeProfile(
        int capacityMiB,
        int headroomMiB)
    {
        long capacity = capacityMiB * 1024L * 1024L;
        long headroom = headroomMiB * 1024L * 1024L;
        var owner = new SvgSnapshotAdmission(capacity, headroom);
        Assert.True(owner.TryReserve(capacity - headroom, MarkdownSvgRenderPriority.Overscan,
            out SvgSnapshotAdmission.Lease? overscan, out long firstVersion));
        Assert.Equal(0L, firstVersion);
        Assert.False(owner.TryReserve(1, MarkdownSvgRenderPriority.Overscan, out _, out long deniedVersion));
        Assert.Equal(firstVersion, deniedVersion);
        Assert.True(owner.TryReserve(headroom, MarkdownSvgRenderPriority.Visible,
            out SvgSnapshotAdmission.Lease? visible, out _));
        Assert.Equal(capacity, owner.ReservedBytes);
        Assert.False(owner.TryReserve(1, MarkdownSvgRenderPriority.Visible, out _, out _));

        visible!.Dispose();
        overscan!.Dispose();
        Assert.Equal(0, owner.ReservedBytes);
    }

    [Fact]
    public async Task ShrinkAndReleaseAdvanceCapacityVersionWithoutLostWakeups()
    {
        var owner = new SvgSnapshotAdmission(capacityBytes: 100, visibleHeadroomBytes: 20);
        Assert.True(owner.TryReserve(80, MarkdownSvgRenderPriority.Overscan,
            out SvgSnapshotAdmission.Lease? lease, out long observedVersion));

        lease!.SetLength(40);
        Assert.Equal(40, lease.Bytes);
        Assert.Equal(40, owner.ReservedBytes);
        Assert.True(owner.CapacityVersion > observedVersion);
        await owner.WaitForCapacityChangeAsync(observedVersion, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(1));

        long releaseVersion = owner.CapacityVersion;
        lease.Dispose();
        lease.Dispose();
        Assert.Equal(0, owner.ReservedBytes);
        Assert.True(owner.CapacityVersion > releaseVersion);
        // The release intentionally happens before subscription; versioned
        // waiting completes immediately instead of losing the capacity pulse.
        await owner.WaitForCapacityChangeAsync(releaseVersion, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void TryGrowUsesCurrentTierAndPublishesLeaseAndOwnerWeightsTogether()
    {
        var owner = new SvgSnapshotAdmission(capacityBytes: 100, visibleHeadroomBytes: 20);
        Assert.True(owner.TryReserve(60, MarkdownSvgRenderPriority.Overscan,
            out SvgSnapshotAdmission.Lease? lease, out long initialVersion));

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            lease!.TryGrow(0, MarkdownSvgRenderPriority.Visible, out _));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            lease!.TryGrow(59, MarkdownSvgRenderPriority.Visible, out _));
        Assert.Equal(60, lease!.Bytes);
        Assert.Equal(60, owner.ReservedBytes);

        Assert.False(lease.TryGrow(81, MarkdownSvgRenderPriority.Overscan, out long deniedVersion));
        Assert.Equal(initialVersion, deniedVersion);
        Assert.Equal(60, lease.Bytes);
        Assert.Equal(60, owner.ReservedBytes);
        Assert.Equal(initialVersion, owner.CapacityVersion);

        // The visible tier may consume headroom that the overscan tier cannot.
        Assert.True(lease.TryGrow(81, MarkdownSvgRenderPriority.Visible, out long grownVersion));
        Assert.Equal(initialVersion, grownVersion);
        Assert.Equal(81, lease.Bytes);
        Assert.Equal(81, owner.ReservedBytes);
        Assert.Equal(initialVersion, owner.CapacityVersion);

        // Growing to the same size is an idempotent no-op; a second larger
        // growth updates both accounting views before a later shrink/release.
        Assert.True(lease.TryGrow(81, MarkdownSvgRenderPriority.Visible, out long sameSizeVersion));
        Assert.Equal(initialVersion, sameSizeVersion);
        Assert.Equal(81, lease.Bytes);
        Assert.Equal(81, owner.ReservedBytes);
        Assert.True(lease.TryGrow(90, MarkdownSvgRenderPriority.Visible, out long secondGrowthVersion));
        Assert.Equal(initialVersion, secondGrowthVersion);
        Assert.Equal(90, lease.Bytes);
        Assert.Equal(90, owner.ReservedBytes);

        lease.SetLength(50);
        Assert.Equal(50, lease.Bytes);
        Assert.Equal(50, owner.ReservedBytes);
        Assert.Equal(initialVersion + 1, owner.CapacityVersion);
        lease.Dispose();
        Assert.Equal(0, owner.ReservedBytes);
        Assert.Equal(initialVersion + 2, owner.CapacityVersion);
    }

    [Fact]
    public async Task TryGrowSerializesWithLeaseDisposal()
    {
        var owner = new SvgSnapshotAdmission(capacityBytes: 1024, visibleHeadroomBytes: 128);
        Assert.True(owner.TryReserve(128, MarkdownSvgRenderPriority.Visible,
            out SvgSnapshotAdmission.Lease? lease, out _));
        using var start = new ManualResetEventSlim(initialState: false);

        Task<bool> grow = Task.Run(() =>
        {
            start.Wait();
            try
            {
                return lease!.TryGrow(512, MarkdownSvgRenderPriority.Visible, out _);
            }
            catch (ObjectDisposedException)
            {
                return false;
            }
        });
        Task dispose = Task.Run(() =>
        {
            start.Wait();
            lease!.Dispose();
        });
        start.Set();

        await Task.WhenAll(grow, dispose).WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(0, owner.ReservedBytes);
        Assert.Equal(1L, owner.CapacityVersion);
    }

    [Fact]
    public async Task ThresholdWaitIgnoresAnInsufficientSelfReleaseThenWakesWhenCapacityFits()
    {
        var owner = new SvgSnapshotAdmission(capacityBytes: 100, visibleHeadroomBytes: 20);
        Assert.True(owner.TryReserve(40, MarkdownSvgRenderPriority.Visible,
            out SvgSnapshotAdmission.Lease? otherOwner, out _));
        Assert.True(owner.TryReserve(30, MarkdownSvgRenderPriority.Visible,
            out SvgSnapshotAdmission.Lease? attempt, out _));
        Assert.False(attempt!.TryGrow(65, MarkdownSvgRenderPriority.Visible, out _));

        Task wait = owner.WaitForCapacityAsync(65, MarkdownSvgRenderPriority.Visible, CancellationToken.None);
        Assert.False(wait.IsCompleted);

        long deniedVersion = owner.CapacityVersion;
        attempt.Dispose();
        Assert.True(owner.CapacityVersion > deniedVersion);
        Assert.Equal(40, owner.ReservedBytes);
        Task insufficientReleaseProbe = Task.Delay(TimeSpan.FromMilliseconds(50));
        Assert.Same(insufficientReleaseProbe, await Task.WhenAny(wait, insufficientReleaseProbe));
        Assert.False(wait.IsCompleted, "The attempt's own release is insufficient for a complete retry.");

        otherOwner!.Dispose();
        await wait.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(0L, owner.ReservedBytes);
    }

    [Fact]
    public async Task ThresholdWaitUsesTheRequestedPriorityTier()
    {
        var owner = new SvgSnapshotAdmission(capacityBytes: 100, visibleHeadroomBytes: 20);
        Assert.True(owner.TryReserve(30, MarkdownSvgRenderPriority.Visible,
            out SvgSnapshotAdmission.Lease? lease, out _));

        Task overscanWait = owner.WaitForCapacityAsync(60, MarkdownSvgRenderPriority.Overscan,
            CancellationToken.None);
        Task visibleWait = owner.WaitForCapacityAsync(60, MarkdownSvgRenderPriority.Visible,
            CancellationToken.None);

        Assert.False(overscanWait.IsCompleted);
        await visibleWait.WaitAsync(TimeSpan.FromSeconds(1));

        lease!.Dispose();
        await overscanWait.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task ThresholdWaitHandlesPreCanceledAndAlreadySatisfiedSubscriptions()
    {
        var owner = new SvgSnapshotAdmission(capacityBytes: 100, visibleHeadroomBytes: 20);
        Assert.True(owner.TryReserve(80, MarkdownSvgRenderPriority.Visible,
            out SvgSnapshotAdmission.Lease? lease, out _));
        lease!.Dispose();

        // Capacity released before subscription is not lost.
        await owner.WaitForCapacityAsync(100, MarkdownSvgRenderPriority.Visible, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(1));

        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await owner.WaitForCapacityAsync(1, MarkdownSvgRenderPriority.Visible, canceled.Token));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await owner.WaitForCapacityAsync(81, MarkdownSvgRenderPriority.Overscan, CancellationToken.None));
    }

    [Fact]
    public async Task PendingThresholdWaitCanBeCanceledWithoutChangingReservations()
    {
        var owner = new SvgSnapshotAdmission(capacityBytes: 100, visibleHeadroomBytes: 20);
        Assert.True(owner.TryReserve(90, MarkdownSvgRenderPriority.Visible,
            out SvgSnapshotAdmission.Lease? lease, out _));
        using var canceled = new CancellationTokenSource();
        Task wait = owner.WaitForCapacityAsync(20, MarkdownSvgRenderPriority.Visible, canceled.Token);
        Assert.False(wait.IsCompleted);

        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await wait)
            .WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(90, owner.ReservedBytes);
        Assert.Equal(90, lease!.Bytes);

        lease.Dispose();
        Assert.Equal(0, owner.ReservedBytes);
    }

    [Fact]
    public void ShrinkAndReleaseDoNotAllocateWhenNoHostWaiterExists()
    {
        var owner = new SvgSnapshotAdmission(capacityBytes: 4096, visibleHeadroomBytes: 512);

        _ = MeasureShrinkAndReleaseAllocations(owner); // JIT and warm the exact measured path.
        long allocatedBytes = MeasureShrinkAndReleaseAllocations(owner);

        Assert.Equal(0, allocatedBytes);
        Assert.Equal(0, owner.ReservedBytes);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long MeasureShrinkAndReleaseAllocations(SvgSnapshotAdmission owner)
    {
        if (!owner.TryReserve(512, MarkdownSvgRenderPriority.Visible,
                out SvgSnapshotAdmission.Lease? lease, out _))
            throw new InvalidOperationException("The local admission fixture could not reserve its measured lease.");

        long before = GC.GetAllocatedBytesForCurrentThread();
        lease!.SetLength(256);
        lease.Dispose();
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }
}
