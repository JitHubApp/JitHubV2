using MarkdownRenderer.Performance;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

public sealed class SourceByteAdmissionTests
{
    [Fact]
    public async Task SpeculativeWorkCannotConsumeVisibleReserve()
    {
        var admission = new SourceByteAdmission(64, 16);
        var owner = new object();
        using IDisposable background = await admission.EnterAsync(owner, 48, true, CancellationToken.None);
        Task<IDisposable> nextBackground = admission.EnterAsync(
            owner, 1, true, CancellationToken.None).AsTask();

        Assert.False(nextBackground.IsCompleted);
        Assert.Equal(1, admission.PendingRequests);
        using IDisposable visible = await admission.EnterAsync(
            owner, 16, false, CancellationToken.None);
        Assert.Equal(64, admission.ActiveBytes);
        Assert.Equal(64, admission.PeakActiveBytes);
        Assert.Equal(48, admission.ActiveBackgroundBytes);

        background.Dispose();
        using IDisposable admitted = await nextBackground.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, admission.PendingRequests);
    }

    [Fact]
    public async Task WaitingVisibleImageOutranksFittingSpeculativeImage()
    {
        var admission = new SourceByteAdmission(64, 16);
        using IDisposable occupied = await admission.EnterAsync(
            new object(), 56, false, CancellationToken.None);
        Task<IDisposable> visible = admission.EnterAsync(
            new object(), 16, false, CancellationToken.None).AsTask();
        Task<IDisposable> background = admission.EnterAsync(
            new object(), 8, true, CancellationToken.None).AsTask();

        Assert.False(visible.IsCompleted);
        Assert.False(background.IsCompleted);
        occupied.Dispose();
        using IDisposable visibleLease = await visible.WaitAsync(TimeSpan.FromSeconds(5));
        using IDisposable backgroundLease = await background.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task QueuedDocumentsRotateAfterEachWeightedGrant()
    {
        var admission = new SourceByteAdmission(10, 2);
        using IDisposable occupied = await admission.EnterAsync(
            new object(), 10, false, CancellationToken.None);
        var ownerA = new object();
        var ownerB = new object();
        Task<IDisposable> a1 = admission.EnterAsync(ownerA, 10, false, CancellationToken.None).AsTask();
        Task<IDisposable> a2 = admission.EnterAsync(ownerA, 10, false, CancellationToken.None).AsTask();
        Task<IDisposable> b1 = admission.EnterAsync(ownerB, 10, false, CancellationToken.None).AsTask();
        Task<IDisposable> b2 = admission.EnterAsync(ownerB, 10, false, CancellationToken.None).AsTask();

        occupied.Dispose();
        using IDisposable first = await a1.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(b1.IsCompleted);
        first.Dispose();
        using IDisposable second = await b1.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(a2.IsCompleted);
        second.Dispose();
        using IDisposable third = await a2.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(b2.IsCompleted);
        third.Dispose();
        using IDisposable fourth = await b2.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task CanceledWeightedWaiterDoesNotRetainBytesOrBlockNextOwner()
    {
        var admission = new SourceByteAdmission(64, 16);
        using IDisposable occupied = await admission.EnterAsync(
            new object(), 64, false, CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        Task<IDisposable> canceled = admission.EnterAsync(
            new object(), 32, false, cancellation.Token).AsTask();
        Task<IDisposable> surviving = admission.EnterAsync(
            new object(), 32, false, CancellationToken.None).AsTask();

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceled);
        occupied.Dispose();
        using IDisposable next = await surviving.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(32, admission.ActiveBytes);
        Assert.Equal(0, admission.ActiveBackgroundBytes);
    }

    [Fact]
    public async Task CancellationRacingWeightedGrantDoesNotLeakCapacity()
    {
        for (int iteration = 0; iteration < 100; iteration++)
        {
            var admission = new SourceByteAdmission(64, 16);
            using IDisposable occupied = await admission.EnterAsync(
                new object(), 64, false, CancellationToken.None);
            using var cancellation = new CancellationTokenSource();
            Task<IDisposable> racing = admission.EnterAsync(
                new object(), 32, false, cancellation.Token).AsTask();

            await Task.WhenAll(Task.Run(occupied.Dispose), Task.Run(cancellation.Cancel));
            try
            {
                using IDisposable granted = await racing.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (OperationCanceledException)
            {
            }

            using IDisposable next = await admission.EnterAsync(
                new object(), 64, false, CancellationToken.None).AsTask()
                .WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public void ASourceLargerThanSpeculativeBudgetMustWaitUntilVisible()
    {
        var admission = new SourceByteAdmission(32, 8);
        Assert.True(admission.CanSpeculate(24));
        Assert.False(admission.CanSpeculate(25));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            admission.EnterAsync(new object(), 25, true, CancellationToken.None));
    }
}
