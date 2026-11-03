using MarkdownRenderer.Images;
using MarkdownRenderer.Svg.Resvg.Internal;
using Xunit;

namespace MarkdownRenderer.Svg.Resvg.Tests;

/// <summary>
/// A README can have more SVGs than the worker's bounded priority queue
/// (country flags, sponsor avatars, tag badges). A full queue is backpressure,
/// not a permanent ResourceLimitExceeded failure: new work waits for capacity.
/// </summary>
public sealed class WorkerQueueBackpressureTests
{
    [Fact]
    public async Task RequestsBeyondQueueCapacityWaitAndAreAllServed()
    {
        var options = new ResvgMarkdownSvgRendererOptions();
        // Energy saver on: only the primary slot, so queued work stays queued.
        using WorkerScheduler pool = TestWorkerSchedulers.Create(static () => true, options: options);
        using var cancellation = new CancellationTokenSource();
        var pending = new List<Task<WorkerLease>>();
        IDisposable? held = null;
        try
        {
            held = await AcquireLeaseAsync(InvokeAcquireAsync(pool, cancellation.Token));
            for (int index = 0; index < options.QueueCapacity; index++)
                pending.Add(InvokeAcquireAsync(pool, cancellation.Token));

            Task<WorkerLease> beyondCapacity = InvokeAcquireAsync(pool, cancellation.Token);
            pending.Add(beyondCapacity);
            await Task.Delay(200);
            Assert.False(beyondCapacity.IsFaulted, beyondCapacity.Exception?.InnerException?.Message);
            Assert.False(beyondCapacity.IsCompleted);

            held.Dispose();
            held = null;
            foreach (Task<WorkerLease> admission in pending.ToArray())
            {
                IDisposable lease = await AcquireLeaseAsync(admission);
                lease.Dispose();
                pending.Remove(admission);
            }
        }
        finally
        {
            cancellation.Cancel();
            foreach (Task<WorkerLease> admission in pending)
            {
                try
                {
                    await admission.WaitAsync(TimeSpan.FromSeconds(5));
                    if (admission.IsCompletedSuccessfully)
                        (await admission).Dispose();
                }
                catch (Exception)
                {
                    // Cleanup observes cancellation of work left in the queue.
                }
            }
            held?.Dispose();
        }
    }

    [Fact]
    public async Task WaitingForCapacityHonorsCancellation()
    {
        var options = new ResvgMarkdownSvgRendererOptions();
        using WorkerScheduler pool = TestWorkerSchedulers.Create(static () => true, options: options);
        using var queued = new CancellationTokenSource();
        using var waiting = new CancellationTokenSource();
        var admissions = new List<Task<WorkerLease>>();
        IDisposable? held = null;
        try
        {
            held = await AcquireLeaseAsync(InvokeAcquireAsync(pool, CancellationToken.None));
            for (int index = 0; index < options.QueueCapacity; index++)
                admissions.Add(InvokeAcquireAsync(pool, queued.Token));

            Task<WorkerLease> beyondCapacity = InvokeAcquireAsync(pool, waiting.Token);
            waiting.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                async () => await beyondCapacity.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally
        {
            queued.Cancel();
            foreach (Task admission in admissions)
            {
                try
                {
                    await admission.WaitAsync(TimeSpan.FromSeconds(5));
                }
                catch (Exception)
                {
                    // Canceled queued work.
                }
            }
            held?.Dispose();
        }
    }

    private static async Task<IDisposable> AcquireLeaseAsync(Task<WorkerLease> acquire) =>
        await acquire.WaitAsync(TimeSpan.FromSeconds(10));

    private static Task<WorkerLease> InvokeAcquireAsync(WorkerScheduler pool, CancellationToken cancellationToken) =>
        TestWorkerSchedulers.AcquireAsync(pool, cancellationToken);
}
