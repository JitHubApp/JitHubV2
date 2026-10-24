using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using MarkdownRenderer.Images;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

public sealed class MarkdownRasterDecodeAdmissionTests
{
    [Fact]
    public void SharedAdmissionUsesTheArchitectureCapacityCeiling()
    {
        Assert.Equal(IntPtr.Size == 4 ? 64L * 1024 * 1024 : 128L * 1024 * 1024,
            MarkdownImagePreparationAdmission.Shared.CapacityBytes);
        Assert.Equal(IntPtr.Size == 4 ? 1 : 2,
            MarkdownImagePreparationAdmission.Shared.MaxConcurrency);
    }

    [Fact]
    public async Task WeightedAdmissionsRespectByteAndOperationBudgets()
    {
        var admission = new MarkdownImagePreparationAdmission(100, 2);
        using IDisposable first = await admission.EnterAsync(new object(), 60, false, CancellationToken.None);
        using IDisposable second = await admission.EnterAsync(new object(), 40, false, CancellationToken.None);
        Task<IDisposable> queued = admission.EnterAsync(new object(), 1, false, CancellationToken.None).AsTask();

        Assert.Equal(100, admission.ActiveBytes);
        Assert.Equal(2, admission.ActiveCount);
        Assert.Equal(1, admission.PendingRequests);
        Assert.False(queued.IsCompleted);

        first.Dispose();
        using IDisposable third = await queued.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(41, admission.ActiveBytes);
        Assert.Equal(2, admission.ActiveCount);
    }

    [Theory]
    [InlineData(100L)]
    [InlineData(101L)]
    public async Task FullBudgetEstimateIsAdmittedAloneRatherThanRejected(long estimate)
    {
        var admission = new MarkdownImagePreparationAdmission(100, 2);
        IDisposable ordinary = await admission.EnterAsync(new object(), 40, false, CancellationToken.None);
        Task<IDisposable> oversized = admission.EnterAsync(new object(), estimate, false, CancellationToken.None).AsTask();
        Assert.Equal(1, admission.PendingRequests);
        Assert.False(oversized.IsCompleted);

        ordinary.Dispose();
        using IDisposable admitted = await oversized.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(100, admission.ActiveBytes);
        Assert.Equal(1, admission.ActiveCount);
        Assert.Equal(0, admission.PendingRequests);
    }

    [Fact]
    public async Task ExclusiveHeadWaiterIsNotBypassedBySmallerLaterRequest()
    {
        var admission = new MarkdownImagePreparationAdmission(100, 2);
        IDisposable blocker = await admission.EnterAsync(new object(), 60, false, CancellationToken.None);
        Task<IDisposable> large = admission.EnterAsync(new object(), 100, false, CancellationToken.None).AsTask();
        Task<IDisposable> small = admission.EnterAsync(new object(), 10, false, CancellationToken.None).AsTask();
        await WaitUntilAsync(() => admission.PendingRequests == 2);

        // Forty estimated bytes remain, enough for the later request, but the
        // earlier full-budget request keeps FIFO fairness and cannot starve.
        Assert.False(small.IsCompleted);
        blocker.Dispose();
        using IDisposable admittedLarge = await large.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(100, admission.ActiveBytes);
        Assert.Equal(1, admission.ActiveCount);
        Assert.False(small.IsCompleted);

        admittedLarge.Dispose();
        using IDisposable admittedSmall = await small.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(10, admission.ActiveBytes);
        Assert.Equal(1, admission.ActiveCount);
    }

    [Fact]
    public async Task RoundRobinDispatchesOneRequestPerOwnerBeforeRepeatingOwner()
    {
        var admission = new MarkdownImagePreparationAdmission(100, 2);
        IDisposable blocker = await admission.EnterAsync(new object(), 100, true, CancellationToken.None);
        object firstOwner = new();
        object secondOwner = new();
        Task<IDisposable> firstA = admission.EnterAsync(firstOwner, 1, false, CancellationToken.None).AsTask();
        Task<IDisposable> secondA = admission.EnterAsync(firstOwner, 1, false, CancellationToken.None).AsTask();
        Task<IDisposable> firstB = admission.EnterAsync(secondOwner, 1, false, CancellationToken.None).AsTask();
        await WaitUntilAsync(() => admission.PendingRequests == 3);

        blocker.Dispose();
        using IDisposable admittedA = await firstA.WaitAsync(TimeSpan.FromSeconds(5));
        using IDisposable admittedB = await firstB.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(secondA.IsCompleted);

        admittedA.Dispose();
        using IDisposable nextA = await secondA.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, admission.ActiveCount);
    }

    [Fact]
    public async Task QueuedCancellationRemovesRequestWithoutLeakingCapacity()
    {
        var admission = new MarkdownImagePreparationAdmission(100, 2);
        IDisposable blocker = await admission.EnterAsync(new object(), 100, true, CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        Task<IDisposable> canceled = admission.EnterAsync(
            new object(), 1, false, cancellation.Token).AsTask();
        await WaitUntilAsync(() => admission.PendingRequests == 1);

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await canceled.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(0, admission.PendingRequests);
        Assert.Equal(100, admission.ActiveBytes);
        Assert.Equal(1, admission.ActiveCount);

        blocker.Dispose();
        using IDisposable recovered = await admission.EnterAsync(
            new object(), 100, false, CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(100, admission.ActiveBytes);
        Assert.Equal(1, admission.ActiveCount);
    }

    [Fact]
    public async Task CancellationWhileBlockedOnAdmissionLockIsObservedBeforeQueueing()
    {
        var warmAdmission = new MarkdownImagePreparationAdmission(100, 2);
        using (await warmAdmission.EnterAsync(new object(), 1, false, CancellationToken.None)) { }
        _ = GetAdmissionLock(warmAdmission);
        var admission = new MarkdownImagePreparationAdmission(100, 2);
        using var cancellation = new CancellationTokenSource();
        using var entering = new ManualResetEventSlim();
        Exception? failure = null;
        Task<IDisposable>? returnedTask = null;
        var thread = new Thread(() =>
        {
            entering.Set();
            try
            {
                returnedTask = admission.EnterAsync(
                    new object(), 1, false, cancellation.Token).AsTask();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        }) { IsBackground = true };
        object admissionLock = GetAdmissionLock(admission);
        bool blockedOnLock;
        lock (admissionLock)
        {
            thread.Start();
            bool started = entering.Wait(TimeSpan.FromSeconds(5));
            blockedOnLock = started && SpinWait.SpinUntil(
                () => (thread.ThreadState & ThreadState.WaitSleepJoin) != 0,
                TimeSpan.FromSeconds(5));
            cancellation.Cancel();
        }

        Assert.True(thread.Join(TimeSpan.FromSeconds(5)));
        Assert.True(blockedOnLock);
        if (returnedTask is not null)
        {
            try
            {
                (await returnedTask.WaitAsync(TimeSpan.FromSeconds(5))).Dispose();
            }
            catch (OperationCanceledException)
            {
            }
        }
        Assert.IsType<OperationCanceledException>(failure);
        Assert.Null(returnedTask);
        Assert.Equal(0, admission.ActiveBytes);
        Assert.Equal(0, admission.ActiveCount);
        Assert.Equal(0, admission.PendingRequests);
    }

    [Fact]
    public async Task CancellationRacingWithGrantReleasesLeaseExactlyOnce()
    {
        var admission = new MarkdownImagePreparationAdmission(100, 2);
        IDisposable blocker = await admission.EnterAsync(new object(), 100, true, CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        Task<IDisposable> raced = admission.EnterAsync(
            new object(), 1, false, cancellation.Token).AsTask();
        await WaitUntilAsync(() => admission.PendingRequests == 1);
        object admissionLock = GetAdmissionLock(admission);

        // Monitor is reentrant: disposing the blocker dispatches the waiter
        // while this outer lock prevents its continuation from consuming the
        // grant. Cancellation then races the already-published grant.
        lock (admissionLock)
        {
            blocker.Dispose();
            cancellation.Cancel();
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await raced.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(0, admission.PendingRequests);
        Assert.Equal(0, admission.ActiveBytes);
        Assert.Equal(0, admission.ActiveCount);

        using IDisposable recovered = await admission.EnterAsync(
            new object(), 100, true, CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(100, admission.ActiveBytes);
        Assert.Equal(1, admission.ActiveCount);
    }

    [Fact]
    public async Task ActiveAdmissionIsHeldUntilNonCancelableDecodeActuallyReturns()
    {
        var admission = new MarkdownImagePreparationAdmission(100, 1);
        using var cancellation = new CancellationTokenSource();
        var decodeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseDecode = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task decode = RunNonCancelableDecodeAsync(admission, cancellation.Token, decodeStarted, releaseDecode);
        await decodeStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task<IDisposable> waiting = admission.EnterAsync(
            new object(), 1, false, CancellationToken.None).AsTask();

        cancellation.Cancel();
        await WaitUntilAsync(() => admission.PendingRequests == 1);
        Assert.Equal(1, admission.ActiveCount);
        Assert.False(waiting.IsCompleted);

        releaseDecode.SetResult();
        await decode.WaitAsync(TimeSpan.FromSeconds(5));
        using IDisposable next = await waiting.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, admission.ActiveCount);
    }

    [Fact]
    public async Task GrantedLeaseDoesNotRetainDocumentOwner()
    {
        var admission = new MarkdownImagePreparationAdmission(100, 1);
        IDisposable blocker = await admission.EnterAsync(new object(), 100, true, CancellationToken.None);
        Task<(WeakReference<object> Owner, IDisposable Lease)> acquisition =
            AcquireWithTemporaryOwnerAsync(admission);
        await WaitUntilAsync(() => admission.PendingRequests == 1);
        blocker.Dispose();
        (WeakReference<object> owner, IDisposable lease) = await acquisition.WaitAsync(TimeSpan.FromSeconds(5));

        ForceCollection();
        Assert.False(IsAlive(owner));
        Assert.Equal(1, admission.ActiveCount);
        Assert.Equal(1, admission.ActiveBytes);
        lease.Dispose();
        Assert.Equal(0, admission.ActiveCount);
        Assert.Equal(0, admission.ActiveBytes);
    }

    private static async Task RunNonCancelableDecodeAsync(
        MarkdownImagePreparationAdmission admission,
        CancellationToken cancellationToken,
        TaskCompletionSource decodeStarted,
        TaskCompletionSource releaseDecode)
    {
        using IDisposable lease = await admission.EnterAsync(
            new object(), 100, true, cancellationToken);
        decodeStarted.SetResult();
        // Deliberately do not pass the token to this await: it models a native
        // decoder that can only be retired once its operation has returned.
        await releaseDecode.Task;
    }

    private static object GetAdmissionLock(MarkdownImagePreparationAdmission admission) =>
        typeof(MarkdownImagePreparationAdmission)
            .GetField("_gate", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(admission)!;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<(WeakReference<object> Owner, IDisposable Lease)> AcquireWithTemporaryOwnerAsync(
        MarkdownImagePreparationAdmission admission)
    {
        object owner = new();
        var weakOwner = new WeakReference<object>(owner);
        IDisposable lease = await admission.EnterAsync(owner, 1, false, CancellationToken.None);
        owner = null!;
        return (weakOwner, lease);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool IsAlive(WeakReference<object> owner) => owner.TryGetTarget(out _);

    private static void ForceCollection()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition())
            await Task.Delay(10, timeout.Token);
    }
}
