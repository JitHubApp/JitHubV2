using System.Reflection;
using MarkdownRenderer.Images;
using MarkdownRenderer.Svg.Resvg.Internal;
using Xunit;

namespace MarkdownRenderer.Svg.Resvg.Tests;

/// <summary>
/// P3-010 (Phase 3 ranks 114, 275, 300, 306, 413): a README with more SVGs
/// than the worker's bounded priority queue (country flags, sponsor avatars,
/// tag badges) failed every image past the first 32 as ResourceLimitExceeded,
/// permanently. A full queue is backpressure: new work waits for capacity.
/// </summary>
public sealed class WorkerQueueBackpressureTests
{
    [Fact]
    public async Task RequestsBeyondQueueCapacityWaitAndAreAllServed()
    {
        var options = new ResvgMarkdownSvgRendererOptions();
        using var pool = new WorkerPool(
            options,
            TimeProvider.System,
            // Energy saver on: only the primary slot, so queued work stays queued.
            static () => true,
            static (_, _) => throw new InvalidOperationException(
                "Acquire-only scheduling must not allocate a source mapping."));
        using var cancellation = new CancellationTokenSource();
        var pending = new List<Task>();
        IDisposable? held = null;
        try
        {
            held = await AcquireLeaseAsync(InvokeAcquireAsync(pool, cancellation.Token));
            for (int index = 0; index < options.QueueCapacity; index++)
                pending.Add(InvokeAcquireAsync(pool, cancellation.Token));

            Task beyondCapacity = InvokeAcquireAsync(pool, cancellation.Token);
            pending.Add(beyondCapacity);
            await Task.Delay(200);
            Assert.False(beyondCapacity.IsFaulted, beyondCapacity.Exception?.InnerException?.Message);
            Assert.False(beyondCapacity.IsCompleted);

            held.Dispose();
            held = null;
            foreach (Task admission in pending.ToArray())
            {
                IDisposable lease = await AcquireLeaseAsync(admission);
                lease.Dispose();
                pending.Remove(admission);
            }
        }
        finally
        {
            cancellation.Cancel();
            foreach (Task admission in pending)
            {
                try
                {
                    await admission.WaitAsync(TimeSpan.FromSeconds(5));
                    if (admission.GetType().GetProperty("Result")?.GetValue(admission) is IDisposable lease)
                        lease.Dispose();
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
        using var pool = new WorkerPool(
            options,
            TimeProvider.System,
            static () => true,
            static (_, _) => throw new InvalidOperationException("No source mapping."));
        using var queued = new CancellationTokenSource();
        using var waiting = new CancellationTokenSource();
        var admissions = new List<Task>();
        IDisposable? held = null;
        try
        {
            held = await AcquireLeaseAsync(InvokeAcquireAsync(pool, CancellationToken.None));
            for (int index = 0; index < options.QueueCapacity; index++)
                admissions.Add(InvokeAcquireAsync(pool, queued.Token));

            Task beyondCapacity = InvokeAcquireAsync(pool, waiting.Token);
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

    private static async Task<IDisposable> AcquireLeaseAsync(Task acquire)
    {
        await acquire.WaitAsync(TimeSpan.FromSeconds(10));
        object lease = acquire.GetType().GetProperty("Result")!.GetValue(acquire)!;
        return Assert.IsAssignableFrom<IDisposable>(lease);
    }

    private static Task InvokeAcquireAsync(WorkerPool pool, CancellationToken cancellationToken)
    {
        MethodInfo acquire = typeof(WorkerPool).GetMethod(
            "AcquireAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        return (Task)acquire.Invoke(pool,
        [
            WorkerOperation.Render,
            MarkdownSvgRenderPriority.Visible,
            cancellationToken,
            false,
            0L,
            false,
            null,
            null,
        ])!;
    }
}
