using MarkdownRenderer.Performance;
using System.Reflection;
using System.Threading;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

public sealed class FairDocumentAdmissionTests
{
    [Fact]
    public async Task CancellationWhileBlockedOnAdmissionGateDoesNotGrantOrLeakCapacity()
    {
        var admission = new FairDocumentAdmission(capacity: 1);
        object gate = typeof(FairDocumentAdmission)
            .GetField("_gate", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(admission)!;
        using var cancellation = new CancellationTokenSource();
        using var started = new ManualResetEventSlim();
        var completion = new TaskCompletionSource<IDisposable>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var requester = new Thread(async () =>
        {
            started.Set();
            try
            {
                IDisposable lease = await admission.EnterAsync(
                    new object(), cancellation.Token).ConfigureAwait(false);
                completion.TrySetResult(lease);
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
        }) { IsBackground = true };

        Monitor.Enter(gate);
        try
        {
            requester.Start();
            Assert.True(started.Wait(TimeSpan.FromSeconds(5)));
            Assert.True(SpinWait.SpinUntil(
                () => (requester.ThreadState & ThreadState.WaitSleepJoin) ==
                    ThreadState.WaitSleepJoin,
                TimeSpan.FromSeconds(5)));
            cancellation.Cancel();
        }
        finally
        {
            Monitor.Exit(gate);
        }

        IDisposable? unexpectedLease = null;
        bool wasCanceled = false;
        try
        {
            unexpectedLease = await completion.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (OperationCanceledException)
        {
            wasCanceled = true;
        }
        finally
        {
            unexpectedLease?.Dispose();
        }

        Assert.True(wasCanceled, "Canceled admission must not grant a lease after the gate opens.");
        using IDisposable recovered = await admission.EnterAsync(
                new object(), CancellationToken.None)
            .AsTask().WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task ExclusivePreparationOwnsEverySlotUntilDisposed(int capacity)
    {
        var admission = new FairDocumentAdmission(capacity);
        using IDisposable exclusive = await admission.EnterAsync(
            new object(), exclusive: true, CancellationToken.None);
        Task<IDisposable> small = admission.EnterAsync(new object(), CancellationToken.None).AsTask();
        Assert.False(small.IsCompleted);
        Assert.Equal(1, admission.PendingRequests);
        exclusive.Dispose();
        exclusive.Dispose();
        using IDisposable granted = await small.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, admission.PendingRequests);
    }

    [Fact]
    public async Task WaitingExclusivePreparationDrainsActiveWorkWithoutBeingBypassed()
    {
        var admission = new FairDocumentAdmission(2);
        using IDisposable first = await admission.EnterAsync(new object(), CancellationToken.None);
        using IDisposable second = await admission.EnterAsync(new object(), CancellationToken.None);
        Task<IDisposable> large = admission.EnterAsync(
            new object(), exclusive: true, CancellationToken.None).AsTask();
        Task<IDisposable> small = admission.EnterAsync(new object(), CancellationToken.None).AsTask();

        first.Dispose();
        Assert.False(large.IsCompleted);
        Assert.False(small.IsCompleted);
        second.Dispose();
        using IDisposable exclusive = await large.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(small.IsCompleted);
        exclusive.Dispose();
        using IDisposable next = await small.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task CancelingWaitingExclusivePreparationMakesTheSpareSlotAvailable()
    {
        var admission = new FairDocumentAdmission(2);
        using IDisposable active = await admission.EnterAsync(new object(), CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        Task<IDisposable> large = admission.EnterAsync(
            new object(), exclusive: true, cancellation.Token).AsTask();
        Task<IDisposable> small = admission.EnterAsync(new object(), CancellationToken.None).AsTask();
        Assert.False(small.IsCompleted);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => large);
        using IDisposable next = await small.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task CancellationRacingExclusiveGrantDoesNotLeakAnySlot()
    {
        for (int iteration = 0; iteration < 100; iteration++)
        {
            var admission = new FairDocumentAdmission(2);
            using IDisposable initial = await admission.EnterAsync(new object(), CancellationToken.None);
            using var cancellation = new CancellationTokenSource();
            Task<IDisposable> racing = admission.EnterAsync(
                new object(), exclusive: true, cancellation.Token).AsTask();
            await Task.WhenAll(Task.Run(initial.Dispose), Task.Run(cancellation.Cancel));
            try { using IDisposable granted = await racing.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (OperationCanceledException) { }
            using IDisposable first = await admission.EnterAsync(new object(), CancellationToken.None)
                .AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            using IDisposable second = await admission.EnterAsync(new object(), CancellationToken.None)
                .AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task OneDocumentCanFillAllSpeculativeSlots()
    {
        var admission = new FairDocumentAdmission(2);
        var owner = new object();
        using IDisposable first = await admission.EnterAsync(owner, CancellationToken.None);
        using IDisposable second = await admission.EnterAsync(owner, CancellationToken.None);
        Task<IDisposable> pending = admission.EnterAsync(owner, CancellationToken.None).AsTask();

        Assert.False(pending.IsCompleted);
        first.Dispose();
        using IDisposable third = await pending.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task QueuedDocumentsReceiveSlotsInRoundRobinOrder()
    {
        var admission = new FairDocumentAdmission(1);
        var firstOwner = new object();
        var secondOwner = new object();
        using IDisposable initial = await admission.EnterAsync(firstOwner, CancellationToken.None);
        Task<IDisposable> firstA = admission.EnterAsync(firstOwner, CancellationToken.None).AsTask();
        Task<IDisposable> secondA = admission.EnterAsync(firstOwner, CancellationToken.None).AsTask();
        Task<IDisposable> firstB = admission.EnterAsync(secondOwner, CancellationToken.None).AsTask();
        Task<IDisposable> secondB = admission.EnterAsync(secondOwner, CancellationToken.None).AsTask();

        initial.Dispose();
        using IDisposable leaseA1 = await firstA.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(firstB.IsCompleted);
        leaseA1.Dispose();
        using IDisposable leaseB1 = await firstB.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(secondA.IsCompleted);
        leaseB1.Dispose();
        using IDisposable leaseA2 = await secondA.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(secondB.IsCompleted);
        leaseA2.Dispose();
        using IDisposable leaseB2 = await secondB.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task CanceledWaiterIsRemovedWithoutConsumingTheNextSlot()
    {
        var admission = new FairDocumentAdmission(1);
        using IDisposable initial = await admission.EnterAsync(new object(), CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        Task<IDisposable> canceled = admission.EnterAsync(new object(), cancellation.Token).AsTask();
        Task<IDisposable> surviving = admission.EnterAsync(new object(), CancellationToken.None).AsTask();
        Assert.Equal(2, admission.PendingRequests);

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceled);
        Assert.Equal(1, admission.PendingRequests);
        initial.Dispose();
        using IDisposable next = await surviving.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, admission.PendingRequests);
    }

    [Fact]
    public async Task CancellationRacingGrantDoesNotLeakCapacity()
    {
        for (int iteration = 0; iteration < 100; iteration++)
        {
            var admission = new FairDocumentAdmission(1);
            using IDisposable initial = await admission.EnterAsync(new object(), CancellationToken.None);
            using var cancellation = new CancellationTokenSource();
            Task<IDisposable> racing = admission.EnterAsync(new object(), cancellation.Token).AsTask();

            await Task.WhenAll(
                Task.Run(initial.Dispose),
                Task.Run(cancellation.Cancel));
            try
            {
                using IDisposable granted = await racing.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (OperationCanceledException)
            {
            }

            using IDisposable next = await admission.EnterAsync(new object(), CancellationToken.None)
                .AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        }
    }
}
