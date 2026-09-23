using MarkdownRenderer.Performance;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

public sealed class FairBackgroundFetchAdmissionTests
{
    [Fact]
    public async Task OneDocumentCanFillAllSpeculativeSlots()
    {
        var admission = new FairBackgroundFetchAdmission(2);
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
        var admission = new FairBackgroundFetchAdmission(1);
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
        var admission = new FairBackgroundFetchAdmission(1);
        using IDisposable initial = await admission.EnterAsync(new object(), CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        Task<IDisposable> canceled = admission.EnterAsync(new object(), cancellation.Token).AsTask();
        Task<IDisposable> surviving = admission.EnterAsync(new object(), CancellationToken.None).AsTask();

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceled);
        initial.Dispose();
        using IDisposable next = await surviving.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task CancellationRacingGrantDoesNotLeakCapacity()
    {
        for (int iteration = 0; iteration < 100; iteration++)
        {
            var admission = new FairBackgroundFetchAdmission(1);
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
