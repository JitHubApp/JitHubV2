using MarkdownRenderer.Performance;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

public sealed class SourceByteAdmissionFairnessTests
{
    [Fact]
    public async Task FittingWaitersPreserveOwnerRoundRobinAndDrainWithoutExtraRelease()
    {
        var admission = new SourceByteAdmission(capacity: 6, reservedVisibleBytes: 1);
        IDisposable occupied = await admission.EnterAsync(
            new object(), bytes: 6, background: false, cancellationToken: CancellationToken.None);
        var ownerA = new object();
        var ownerB = new object();
        Task<IDisposable> a1 = admission.EnterAsync(
            ownerA, bytes: 1, background: false, cancellationToken: CancellationToken.None).AsTask();
        Task<IDisposable> a2 = admission.EnterAsync(
            ownerA, bytes: 1, background: false, cancellationToken: CancellationToken.None).AsTask();
        Task<IDisposable> b1 = admission.EnterAsync(
            ownerB, bytes: 1, background: false, cancellationToken: CancellationToken.None).AsTask();

        Assert.False(a1.IsCompleted);
        Assert.False(a2.IsCompleted);
        Assert.False(b1.IsCompleted);
        occupied.Dispose();

        using IDisposable firstA = await a1.WaitAsync(TimeSpan.FromSeconds(5));
        using IDisposable nextOwner = await b1.WaitAsync(TimeSpan.FromSeconds(5));
        using IDisposable secondA = await a2.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(3, admission.ActiveBytes);
        Assert.Equal(0, admission.PendingRequests);
    }

    [Fact]
    public async Task RepeatedFittingVisibleArrivalsCannotStarveOlderLargeWaiter()
    {
        var admission = new SourceByteAdmission(capacity: 4, reservedVisibleBytes: 1);
        IDisposable occupied = await admission.EnterAsync(
            new object(), bytes: 1, background: false, cancellationToken: CancellationToken.None);
        Task<IDisposable> large = admission.EnterAsync(
            new object(), bytes: 4, background: false, cancellationToken: CancellationToken.None).AsTask();
        Assert.False(large.IsCompleted);

        // The first fitting visible request may pass the blocked large request
        // once. Releasing that lease must not reopen an unlimited bypass path.
        IDisposable oneBypass = await admission.EnterAsync(
            new object(), bytes: 1, background: false, cancellationToken: CancellationToken.None)
            .AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        oneBypass.Dispose();

        var cancellations = new List<CancellationTokenSource>();
        var smallWaiters = new List<Task<IDisposable>>();
        try
        {
            for (int i = 0; i < 12; i++)
            {
                var cancellation = new CancellationTokenSource();
                cancellations.Add(cancellation);
                Task<IDisposable> small = admission.EnterAsync(
                    new object(), bytes: 1, background: false, cancellationToken: cancellation.Token).AsTask();
                smallWaiters.Add(small);
                Assert.False(small.IsCompleted);
            }

            Assert.Equal(13, admission.PendingRequests);
            occupied.Dispose();
            occupied = null!;

            using IDisposable largeLease = await large.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(4, admission.ActiveBytes);
            Assert.All(smallWaiters, waiter => Assert.False(waiter.IsCompleted));

            foreach (CancellationTokenSource cancellation in cancellations)
                cancellation.Cancel();
            foreach (Task<IDisposable> small in smallWaiters)
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => small);
        }
        finally
        {
            occupied?.Dispose();
            foreach (CancellationTokenSource cancellation in cancellations)
                cancellation.Dispose();
        }
    }

    [Fact]
    public async Task CancelingReservedLargeWaiterUnblocksNextVisibleRequest()
    {
        var admission = new SourceByteAdmission(capacity: 4, reservedVisibleBytes: 1);
        using IDisposable occupied = await admission.EnterAsync(
            new object(), bytes: 1, background: false, cancellationToken: CancellationToken.None);
        using var largeCancellation = new CancellationTokenSource();
        Task<IDisposable> large = admission.EnterAsync(
            new object(), bytes: 4, background: false, largeCancellation.Token).AsTask();

        using (IDisposable oneBypass = await admission.EnterAsync(
            new object(), bytes: 1, background: false, cancellationToken: CancellationToken.None)
            .AsTask().WaitAsync(TimeSpan.FromSeconds(5)))
        {
        }

        Task<IDisposable> nextVisible = admission.EnterAsync(
            new object(), bytes: 1, background: false, cancellationToken: CancellationToken.None).AsTask();
        Assert.False(nextVisible.IsCompleted);

        largeCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => large);
        using IDisposable nextLease = await nextVisible.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, admission.ActiveBytes);
        Assert.Equal(0, admission.PendingRequests);
    }

    [Fact]
    public async Task ReservedLargeVisibleWaiterAdvancesAfterBackgroundLeaseDrains()
    {
        var admission = new SourceByteAdmission(capacity: 4, reservedVisibleBytes: 1);
        IDisposable background = await admission.EnterAsync(
            new object(), bytes: 1, background: true, cancellationToken: CancellationToken.None);
        Task<IDisposable> large = admission.EnterAsync(
            new object(), bytes: 4, background: false, cancellationToken: CancellationToken.None).AsTask();

        using (IDisposable oneBypass = await admission.EnterAsync(
            new object(), bytes: 1, background: false, cancellationToken: CancellationToken.None)
            .AsTask().WaitAsync(TimeSpan.FromSeconds(5)))
        {
        }

        using var smallCancellation = new CancellationTokenSource();
        Task<IDisposable> small = admission.EnterAsync(
            new object(), bytes: 1, background: false, cancellationToken: smallCancellation.Token).AsTask();
        Assert.False(large.IsCompleted);
        Assert.False(small.IsCompleted);

        background.Dispose();
        background = null!;
        using IDisposable largeLease = await large.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(4, admission.ActiveBytes);
        Assert.Equal(0, admission.ActiveBackgroundBytes);
        Assert.False(small.IsCompleted);

        smallCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => small);
    }
}
