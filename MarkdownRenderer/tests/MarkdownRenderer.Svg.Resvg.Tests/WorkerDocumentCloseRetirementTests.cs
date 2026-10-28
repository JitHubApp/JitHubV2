using System.IO;
using System.Runtime.CompilerServices;
using MarkdownRenderer.Images;
using MarkdownRenderer.Svg.Resvg.Internal;
using Xunit;

namespace MarkdownRenderer.Svg.Resvg.Tests;

public sealed class WorkerDocumentCloseRetirementTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RetainedInvalidCloseTaskDoesNotPinOtherDelegateCapture(bool missingClose)
    {
        (Task task, WeakReference capture) = CreateInvalidClose(missingClose);
        ArgumentNullException failure = await Assert.ThrowsAsync<ArgumentNullException>(async () => await task);
        Assert.Equal(missingClose ? "close" : "invalidate", failure.ParamName);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Assert.False(capture.IsAlive);
        GC.KeepAlive(task);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (Task, WeakReference) CreateInvalidClose(bool missingClose)
    {
        byte[] capture = new byte[1024 * 1024];
        Func<Task<WorkerResponse>> close = () =>
        {
            GC.KeepAlive(capture);
            return Task.FromResult(Response(WorkerStatus.Ok));
        };
        Action invalidate = () => GC.KeepAlive(capture);
        return (
            WorkerPool.CloseDocumentOrInvalidateAsync(
                missingClose ? null! : close,
                missingClose ? invalidate : null!),
            new WeakReference(capture));
    }

    [Fact]
    public async Task SuccessfulCloseDoesNotInvalidateWorker()
    {
        int invalidations = 0;

        await WorkerPool.CloseDocumentOrInvalidateAsync(
            () => Task.FromResult(Response(WorkerStatus.Ok)),
            () => invalidations++);

        Assert.Equal(0, invalidations);
    }

    [Fact]
    public async Task QueueFullCloseIsBestEffortAndDoesNotInvalidateHealthyWorker()
    {
        int invalidations = 0;

        await WorkerPool.CloseDocumentOrInvalidateAsync(
            () => Task.FromException<WorkerResponse>(new MarkdownSvgException(
                MarkdownSvgFailureReason.ResourceLimitExceeded,
                "The SVG worker queue is full.")),
            () => invalidations++);

        Assert.Equal(0, invalidations);
    }

    [Fact]
    public async Task KnownCloseFailuresInvalidateWorker()
    {
        Exception[] failures =
        [
            new OperationCanceledException("Worker close was interrupted."),
            new IOException("Worker pipe closed."),
            new WorkerProtocolException("Worker returned an invalid response."),
            new ObjectDisposedException("worker"),
            new MarkdownSvgException(MarkdownSvgFailureReason.WorkerFailure),
        ];

        foreach (Exception failure in failures)
        {
            int invalidations = 0;

            await WorkerPool.CloseDocumentOrInvalidateAsync(
                () => Task.FromException<WorkerResponse>(failure),
                () => invalidations++);

            Assert.Equal(1, invalidations);
        }
    }

    [Fact]
    public async Task MalformedCloseResponsesInvalidateWorker()
    {
        WorkerResponse[] malformedResponses =
        [
            Response(WorkerStatus.Unsupported),
            Response(WorkerStatus.Ok, outputLength: 1),
            Response(WorkerStatus.ResourceLimit),
        ];

        foreach (WorkerResponse response in malformedResponses)
        {
            int invalidations = 0;

            await WorkerPool.CloseDocumentOrInvalidateAsync(
                () => Task.FromResult(response),
                () => invalidations++);

            Assert.Equal(1, invalidations);
        }
    }

    [Fact]
    public async Task ActiveCloseDrainsBeforeFallbackInvalidatesWorker()
    {
        var closeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var closeCompletion = new TaskCompletionSource<WorkerResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        int invalidations = 0;

        Task close = WorkerPool.CloseDocumentOrInvalidateAsync(
            () =>
            {
                closeStarted.TrySetResult();
                return closeCompletion.Task;
            },
            () => invalidations++);

        await closeStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(close.IsCompleted);
        Assert.Equal(0, invalidations);

        closeCompletion.SetException(new IOException("Worker pipe closed during CLOSE."));
        await close.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, invalidations);
    }

    private static WorkerResponse Response(WorkerStatus status, int outputLength = 0) =>
        new(
            status,
            IntrinsicWidth: 0,
            IntrinsicHeight: 0,
            AspectRatio: 0,
            OutputLength: outputLength,
            Stride: 0,
            Width: 0,
            Height: 0,
            PixelFormat: MarkdownSvgPixelFormat.Rgba8Premultiplied,
            HasText: false,
            UsesCurrentColor: false,
            UsesColorScheme: false,
            Detail: string.Empty);
}
