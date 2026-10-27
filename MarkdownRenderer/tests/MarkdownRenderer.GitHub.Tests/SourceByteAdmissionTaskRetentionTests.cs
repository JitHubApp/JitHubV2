using System.Runtime.CompilerServices;
using MarkdownRenderer.Performance;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

public sealed class SourceByteAdmissionTaskRetentionTests
{
    [Fact]
    public async Task RetainedGrantedTaskDoesNotKeepDocumentOwnerAlive()
    {
        var admission = new SourceByteAdmission(capacity: 4, reservedVisibleBytes: 1);
        QueuedWaiter queued = CreateQueuedWaiter(admission, cancellable: false);

        Assert.False(queued.Task.IsCompleted);
        Assert.True(IsAliveAfterCollection(queued.Owner, queued.Task));

        queued.Blocker.Dispose();
        using IDisposable granted = await queued.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, admission.PendingRequests);

        Assert.False(IsAliveAfterCollection(queued.Owner, queued.Task));
        Assert.Equal(1, admission.ActiveBytes);
        granted.Dispose();
        Assert.Equal(0, admission.ActiveBytes);
    }

    [Fact]
    public async Task RetainedCanceledTaskDoesNotKeepDocumentOwnerAlive()
    {
        var admission = new SourceByteAdmission(capacity: 4, reservedVisibleBytes: 1);
        QueuedWaiter queued = CreateQueuedWaiter(admission, cancellable: true);
        Assert.True(IsAliveAfterCollection(queued.Owner, queued.Task));

        queued.Cancellation!.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued.Task);
        queued.Blocker.Dispose();
        queued.Cancellation.Dispose();

        Assert.Equal(0, admission.PendingRequests);
        Assert.Equal(0, admission.ActiveBytes);
        Assert.False(IsAliveAfterCollection(queued.Owner, queued.Task));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static QueuedWaiter CreateQueuedWaiter(
        SourceByteAdmission admission,
        bool cancellable)
    {
        IDisposable blocker = admission.EnterAsync(
            new object(), admission.Capacity, background: false, CancellationToken.None)
            .AsTask().GetAwaiter().GetResult();
        var owner = new object();
        var ownerReference = new WeakReference<object>(owner);
        CancellationTokenSource? cancellation = cancellable ? new CancellationTokenSource() : null;
        Task<IDisposable> task = admission.EnterAsync(
            owner,
            bytes: 1,
            background: false,
            cancellationToken: cancellation?.Token ?? CancellationToken.None).AsTask();

        return new QueuedWaiter(task, ownerReference, blocker, cancellation);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool IsAliveAfterCollection(
        WeakReference<object> owner,
        Task<IDisposable> retainedTask)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        bool isAlive = owner.TryGetTarget(out _);
        GC.KeepAlive(retainedTask);
        return isAlive;
    }

    private sealed record QueuedWaiter(
        Task<IDisposable> Task,
        WeakReference<object> Owner,
        IDisposable Blocker,
        CancellationTokenSource? Cancellation);
}
