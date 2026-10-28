using MarkdownRenderer.Images;
using Xunit;

namespace MarkdownRenderer.Tests;

[Collection(AllocationSensitiveContractCollection.Name)]
public sealed class MarkdownImageActivationDemandTests
{
    [Fact]
    public void GenericVisibilityReadsAreMonotonicAndAllocationFree()
    {
        var ticket = new MarkdownSvgRenderDemand(MarkdownSvgRenderPriority.Overscan);
        IMarkdownImageActivationDemand demand = ticket;
        Assert.False(demand.IsVisible);
        ticket.PromoteVisible();
        for (int index = 0; index < 100; index++) _ = demand.IsVisible;
        long before = GC.GetAllocatedBytesForCurrentThread();
        bool visible = true;
        for (int index = 0; index < 50_000; index++) visible &= demand.IsVisible;
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(visible);
        Assert.Equal(0, allocated);
        Assert.False(ticket.PromoteVisible());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GenericPromotionUsesTheExistingAsynchronousTicketAndRetiresItsRegistration(bool initiallyVisible)
    {
        var ticket = new MarkdownSvgRenderDemand(initiallyVisible
            ? MarkdownSvgRenderPriority.Visible : MarkdownSvgRenderPriority.Overscan);
        IMarkdownImageActivationDemand demand = ticket;
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int registeringThread = Environment.CurrentManagedThreadId;
        int calls = 0, callActive = 1, inlineCalls = 0;
        using IDisposable registration = demand.RegisterVisiblePromotion(() =>
        {
            Interlocked.Increment(ref calls);
            if (Environment.CurrentManagedThreadId == registeringThread && Volatile.Read(ref callActive) != 0)
                Interlocked.Increment(ref inlineCalls);
            completed.TrySetResult();
        });
        Volatile.Write(ref callActive, 0);
        Volatile.Write(ref callActive, 1);
        ticket.PromoteVisible();
        Volatile.Write(ref callActive, 0);
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, Volatile.Read(ref inlineCalls));
        Assert.True(demand.IsVisible);
        Assert.Equal(1, Volatile.Read(ref calls));
        registration.Dispose();
        Assert.Equal(0, ticket.PendingPromotionCount);
    }
}
