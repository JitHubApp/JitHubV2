using System.Runtime.CompilerServices;
using MarkdownRenderer.Images;
using Xunit;

namespace MarkdownRenderer.Tests;

[Collection(AllocationSensitiveContractCollection.Name)]
public sealed class MarkdownSvgRenderDemandTests
{
    [Theory]
    [InlineData(MarkdownSvgRenderPriority.Overscan, 0)]
    [InlineData(MarkdownSvgRenderPriority.Visible, 1)]
    public void InitialDemandIsAnAtomicSnapshot(MarkdownSvgRenderPriority priority, long version)
    {
        var ticket = new MarkdownSvgRenderDemand(priority);
        Assert.Equal(priority, ticket.Priority);
        Assert.Equal(version, ticket.Version);
        Assert.Equal(0, ticket.PendingPromotionCount);
    }

    [Fact]
    public void InvalidInitialPriorityIsRejected() => Assert.Throws<ArgumentOutOfRangeException>(
        () => new MarkdownSvgRenderDemand((MarkdownSvgRenderPriority)42));

    [Fact]
    public void VisibleObservationIsMonotonicAndAllocationFree()
    {
        var ticket = new MarkdownSvgRenderDemand(MarkdownSvgRenderPriority.Overscan);
        Assert.True(ticket.PromoteVisible());
        for (int index = 0; index < 100; index++)
            ticket.PromoteVisible();
        long before = GC.GetAllocatedBytesForCurrentThread();
        bool unexpectedPromotion = false;
        for (int index = 0; index < 50_000; index++)
            unexpectedPromotion |= ticket.PromoteVisible();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.False(unexpectedPromotion);
        Assert.Equal(0, allocated);
        Assert.Equal(1, ticket.Version);
        Assert.Equal(MarkdownSvgRenderPriority.Visible, ticket.Priority);
    }

    [Fact]
    public async Task RegistrationBeforeAndAfterPromotionIsDeliveredExactlyOnce()
    {
        var ticket = new MarkdownSvgRenderDemand(MarkdownSvgRenderPriority.Overscan);
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int firstCalls = 0, secondCalls = 0;
        using IDisposable firstRegistration = ticket.RegisterPromotion(() =>
        {
            Interlocked.Increment(ref firstCalls);
            first.TrySetResult();
        });
        Assert.Equal(1, ticket.PendingPromotionCount);
        Assert.True(ticket.PromoteVisible());
        using IDisposable secondRegistration = ticket.RegisterPromotion(() =>
        {
            Interlocked.Increment(ref secondCalls);
            second.TrySetResult();
        });
        Assert.False(ticket.PromoteVisible());
        await Task.WhenAll(first.Task, second.Task).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, firstCalls);
        Assert.Equal(1, secondCalls);
        Assert.Equal(0, ticket.PendingPromotionCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DeliveryNeverInvokesTheSubscriberInline(bool initiallyVisible)
    {
        var ticket = new MarkdownSvgRenderDemand(initiallyVisible
            ? MarkdownSvgRenderPriority.Visible : MarkdownSvgRenderPriority.Overscan);
        using var completed = new ManualResetEventSlim();
        int callerThread = Environment.CurrentManagedThreadId;
        int callbackThread = 0;
        using IDisposable registration = ticket.RegisterPromotion(() =>
        {
            callbackThread = Environment.CurrentManagedThreadId;
            completed.Set();
        });
        ticket.PromoteVisible();
        // This is a pure worker-thread unit probe, not a UI dispatcher wait.
        Assert.True(completed.Wait(TimeSpan.FromSeconds(5)));
        Assert.NotEqual(callerThread, callbackThread);
    }

    [Fact]
    public void DisposingBeforePromotionRemovesTheCallbackAndIsIdempotent()
    {
        var ticket = new MarkdownSvgRenderDemand(MarkdownSvgRenderPriority.Overscan);
        int calls = 0;
        IDisposable registration = ticket.RegisterPromotion(() => Interlocked.Increment(ref calls));
        Assert.Equal(1, ticket.PendingPromotionCount);
        registration.Dispose();
        registration.Dispose();
        Assert.Equal(0, ticket.PendingPromotionCount);
        Assert.True(ticket.PromoteVisible());
        Assert.Equal(0, calls);
    }

    [Fact]
    public void SubscriptionCountIsBoundedAndCanceledSlotsCanBeReused()
    {
        var ticket = new MarkdownSvgRenderDemand(MarkdownSvgRenderPriority.Overscan);
        var registrations = new List<IDisposable>();
        try
        {
            for (int index = 0; index < MarkdownSvgRenderDemand.MaximumPromotionRegistrations; index++)
                registrations.Add(ticket.RegisterPromotion(static () => { }));
            Assert.Equal(MarkdownSvgRenderDemand.MaximumPromotionRegistrations, ticket.PendingPromotionCount);
            Assert.Throws<InvalidOperationException>(() => ticket.RegisterPromotion(static () => { }));
            registrations[0].Dispose();
            using IDisposable replacement = ticket.RegisterPromotion(static () => { });
            Assert.Equal(MarkdownSvgRenderDemand.MaximumPromotionRegistrations, ticket.PendingPromotionCount);
        }
        finally
        {
            foreach (IDisposable registration in registrations)
                registration.Dispose();
        }
        Assert.Equal(0, ticket.PendingPromotionCount);
    }

    [Fact]
    public async Task ConcurrentRegistrationAndPromotionNeverLosesTheSignal()
    {
        for (int index = 0; index < 200; index++)
        {
            var ticket = new MarkdownSvgRenderDemand(MarkdownSvgRenderPriority.Overscan);
            var delivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            int calls = 0;
            IDisposable? registration = null;
            await Task.WhenAll(
                Task.Run(() => registration = ticket.RegisterPromotion(() =>
                {
                    Interlocked.Increment(ref calls);
                    delivered.TrySetResult();
                })),
                Task.Run(() => ticket.PromoteVisible()));
            using (registration!)
            {
                await delivered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(1, calls);
                Assert.Equal(0, ticket.PendingPromotionCount);
            }
        }
    }

    [Fact]
    public async Task OneBadSubscriberCannotStopOtherNotifications()
    {
        var ticket = new MarkdownSvgRenderDemand(MarkdownSvgRenderPriority.Overscan);
        var failedSubscriberEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var successfulSubscriberEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using IDisposable bad = ticket.RegisterPromotion(() =>
        {
            failedSubscriberEntered.TrySetResult();
            throw new InvalidOperationException("Injected internal subscriber failure.");
        });
        using IDisposable good = ticket.RegisterPromotion(() => successfulSubscriberEntered.TrySetResult());
        Assert.True(ticket.PromoteVisible());
        await Task.WhenAll(failedSubscriberEntered.Task, successfulSubscriberEntered.Task)
            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, ticket.PendingPromotionCount);
    }

    [Fact]
    public async Task FailedSchedulingReturnsTheSlotAndDoesNotStrandOtherSubscribers()
    {
        int attempts = 0;
        var ticket = new MarkdownSvgRenderDemand(MarkdownSvgRenderPriority.Overscan,
            () => Interlocked.Increment(ref attempts) != 1);
        int failedCalls = 0;
        var delivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using IDisposable failed = ticket.RegisterPromotion(() => Interlocked.Increment(ref failedCalls));
        using IDisposable good = ticket.RegisterPromotion(() => delivered.TrySetResult());
        Assert.True(ticket.PromoteVisible());
        await delivered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, failedCalls);
        Assert.Equal(2, attempts);
        Assert.Equal(0, ticket.PendingPromotionCount);
    }

    [Fact]
    public void FailedVisibleRegistrationDoesNotExhaustTheSubscriptionLimit()
    {
        var ticket = new MarkdownSvgRenderDemand(MarkdownSvgRenderPriority.Visible, static () => false);
        for (int index = 0; index < MarkdownSvgRenderDemand.MaximumPromotionRegistrations * 2; index++)
            Assert.Throws<InvalidOperationException>(() => ticket.RegisterPromotion(static () => { }));
        Assert.Equal(0, ticket.PendingPromotionCount);
    }

    [Fact]
    public async Task ThrowingQueueProbeCannotStrandTheRestOfThePromotionBatch()
    {
        int attempts = 0;
        var ticket = new MarkdownSvgRenderDemand(MarkdownSvgRenderPriority.Overscan, () =>
        {
            if (Interlocked.Increment(ref attempts) == 1)
                throw new InvalidOperationException("Injected scheduling failure.");
            return true;
        });
        var delivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using IDisposable failed = ticket.RegisterPromotion(static () => { });
        using IDisposable good = ticket.RegisterPromotion(() => delivered.TrySetResult());
        Assert.True(ticket.PromoteVisible());
        await delivered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, attempts);
        Assert.Equal(0, ticket.PendingPromotionCount);
    }

    [Fact]
    public void CanceledRegistrationDoesNotRetainItsCallbackOwner()
    {
        var ticket = new MarkdownSvgRenderDemand(MarkdownSvgRenderPriority.Overscan);
        WeakReference callbackOwner = RegisterAndCancel(ticket);
        for (int index = 0; index < 3; index++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
        Assert.False(callbackOwner.IsAlive);
        Assert.Equal(0, ticket.PendingPromotionCount);
        GC.KeepAlive(ticket);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference RegisterAndCancel(MarkdownSvgRenderDemand ticket)
    {
        var owner = new CallbackOwner();
        var weak = new WeakReference(owner);
        IDisposable registration = ticket.RegisterPromotion(owner.Promoted);
        registration.Dispose();
        return weak;
    }

    private sealed class CallbackOwner
    {
        public void Promoted() { }
    }
}
