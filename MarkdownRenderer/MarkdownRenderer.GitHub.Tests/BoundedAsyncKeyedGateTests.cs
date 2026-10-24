using MarkdownRenderer.Layout.Boxes;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

public sealed class BoundedAsyncKeyedGateTests
{
    [Fact]
    public async Task SameIdentityWaitsForItsCurrentAcquisition()
    {
        var gate = new BoundedAsyncKeyedGate(
            maximumTrackedKeys: 4,
            overflowStripeCount: 8);
        BoundedAsyncKeyedGate.Entry? first = await gate.AcquireAsync("same");
        try
        {
            Task<BoundedAsyncKeyedGate.Entry> secondTask = gate.AcquireAsync("same").AsTask();
            Assert.False(secondTask.IsCompleted);

            gate.Release(ref first);
            Assert.Null(first);
            gate.Release(ref first);

            BoundedAsyncKeyedGate.Entry? second = await secondTask.WaitAsync(TimeSpan.FromSeconds(2));
            try
            {
                Assert.NotNull(second);
            }
            finally
            {
                gate.Release(ref second);
            }
        }
        finally
        {
            gate.Release(ref first);
        }
    }

    [Fact]
    public async Task DistinctKeysThatWouldShareALegacyStripeCanProceedTogether()
    {
        var gate = new BoundedAsyncKeyedGate(
            maximumTrackedKeys: 4,
            overflowStripeCount: 1);
        BoundedAsyncKeyedGate.Entry? first = await gate.AcquireAsync("content-a");
        BoundedAsyncKeyedGate.Entry? second = null;
        try
        {
            second = await gate.AcquireAsync("content-b");
        }
        finally
        {
            gate.Release(ref second);
            gate.Release(ref first);
        }
    }

    [Fact]
    public async Task CancelingWaiterDoesNotReleaseCurrentOwnerOrStrandTheKey()
    {
        var gate = new BoundedAsyncKeyedGate(
            maximumTrackedKeys: 2,
            overflowStripeCount: 2);
        BoundedAsyncKeyedGate.Entry? owner = await gate.AcquireAsync("same");
        try
        {
            using var cancellation = new CancellationTokenSource();
            Task<BoundedAsyncKeyedGate.Entry> canceledWaiter = gate
                .AcquireAsync("same", cancellation.Token)
                .AsTask();
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await canceledWaiter);

            Task<BoundedAsyncKeyedGate.Entry> nextWaiter = gate.AcquireAsync("same").AsTask();
            Assert.False(nextWaiter.IsCompleted);
            gate.Release(ref owner);

            BoundedAsyncKeyedGate.Entry? next = await nextWaiter.WaitAsync(TimeSpan.FromSeconds(2));
            gate.Release(ref next);
        }
        finally
        {
            gate.Release(ref owner);
        }
    }

    [Fact]
    public async Task CancelingOverflowWaiterLeavesTheStripePermitWithItsOwner()
    {
        var gate = new BoundedAsyncKeyedGate(
            maximumTrackedKeys: 1,
            overflowStripeCount: 1);
        BoundedAsyncKeyedGate.Entry? tracked = await gate.AcquireAsync("tracked");
        BoundedAsyncKeyedGate.Entry? overflowOwner = null;
        BoundedAsyncKeyedGate.Entry? third = null;
        BoundedAsyncKeyedGate.Entry? retry = null;
        try
        {
            overflowOwner = await gate.AcquireAsync("overflow-owner");
            using var cancellation = new CancellationTokenSource();
            Task<BoundedAsyncKeyedGate.Entry> canceledWaiter = gate
                .AcquireAsync("overflow-waiter", cancellation.Token)
                .AsTask();
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await canceledWaiter);
            Task<BoundedAsyncKeyedGate.Entry> stillBlocked = gate.AcquireAsync("third-overflow-waiter").AsTask();
            Assert.False(stillBlocked.IsCompleted);

            gate.Release(ref overflowOwner);
            third = await stillBlocked.WaitAsync(TimeSpan.FromSeconds(2));
            gate.Release(ref third);
            retry = await gate.AcquireAsync("overflow-waiter").AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally
        {
            gate.Release(ref retry);
            gate.Release(ref third);
            gate.Release(ref overflowOwner);
            gate.Release(ref tracked);
        }
    }

    [Fact]
    public async Task OverflowIdentityStaysOnItsStripeUntilAllWaitersDrain()
    {
        var gate = new BoundedAsyncKeyedGate(
            maximumTrackedKeys: 1,
            overflowStripeCount: 1);
        BoundedAsyncKeyedGate.Entry? trackedOwner = await gate.AcquireAsync("fills-table");
        BoundedAsyncKeyedGate.Entry? firstOverflow = null;
        BoundedAsyncKeyedGate.Entry? secondOverflow = null;
        BoundedAsyncKeyedGate.Entry? promoted = null;
        BoundedAsyncKeyedGate.Entry? distinct = null;
        try
        {
            Task<BoundedAsyncKeyedGate.Entry> firstOverflowTask = gate.AcquireAsync("same-overflow-key").AsTask();

            gate.Release(ref trackedOwner);
            firstOverflow = await firstOverflowTask.WaitAsync(TimeSpan.FromSeconds(2));

            Task<BoundedAsyncKeyedGate.Entry> secondSameKeyTask = gate
                .AcquireAsync("same-overflow-key")
                .AsTask();
            Assert.False(secondSameKeyTask.IsCompleted);

            gate.Release(ref firstOverflow);
            secondOverflow = await secondSameKeyTask.WaitAsync(TimeSpan.FromSeconds(2));
            gate.Release(ref secondOverflow);

            promoted = await gate.AcquireAsync("same-overflow-key");
            distinct = await gate
                .AcquireAsync("different-key")
                .AsTask()
                .WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally
        {
            gate.Release(ref distinct);
            gate.Release(ref promoted);
            gate.Release(ref secondOverflow);
            gate.Release(ref firstOverflow);
            gate.Release(ref trackedOwner);
        }
    }
}
