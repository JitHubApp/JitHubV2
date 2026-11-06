using System.Collections.Concurrent;
using System.IO;
using MarkdownRenderer.Svg.Resvg.Internal;
using Xunit;

namespace MarkdownRenderer.Svg.Resvg.Tests;

public sealed class WorkerDocumentStateGateTests
{
    [Fact]
    public async Task TrimWaitsForTheCompleteDocumentMissingReattachAndRetrySequence()
    {
        var gate = new WorkerDocumentStateGate();
        var events = new ConcurrentQueue<string>();
        var probeCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowReattach = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reattachCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowRetry = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task RenderTransactionAsync()
        {
            using WorkerDocumentStateGate.Lease lease = await gate.EnterAsync(CancellationToken.None);
            events.Enqueue("render-probe");
            probeCompleted.TrySetResult();
            await allowReattach.Task;
            events.Enqueue("reattach");
            reattachCompleted.TrySetResult();
            await allowRetry.Task;
            events.Enqueue("retry-render");
        }

        async Task TrimTransactionAsync()
        {
            using WorkerDocumentStateGate.Lease lease = await gate.EnterAsync(CancellationToken.None);
            events.Enqueue("trim");
        }

        Task render = RenderTransactionAsync();
        await probeCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task trim = TrimTransactionAsync();
        Assert.False(trim.IsCompleted);

        allowReattach.TrySetResult();
        await reattachCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(trim.IsCompleted);
        Assert.Equal(["render-probe", "reattach"], events.ToArray());

        allowRetry.TrySetResult();
        await Task.WhenAll(render, trim).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(["render-probe", "reattach", "retry-render", "trim"], events.ToArray());
    }

    [Fact]
    public async Task CancelingAWaitingRenderOrTrimLeavesTheGateReusable()
    {
        var gate = new WorkerDocumentStateGate();
        using WorkerDocumentStateGate.Lease held = await gate.EnterAsync(CancellationToken.None);

        using (var renderCancellation = new CancellationTokenSource())
        {
            Task<WorkerDocumentStateGate.Lease> waitingRender = gate.EnterAsync(renderCancellation.Token).AsTask();
            Assert.False(waitingRender.IsCompleted);
            renderCancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await waitingRender);
        }

        using (var trimCancellation = new CancellationTokenSource())
        {
            Task<WorkerDocumentStateGate.Lease> waitingTrim = gate.EnterAsync(trimCancellation.Token).AsTask();
            Assert.False(waitingTrim.IsCompleted);
            trimCancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await waitingTrim);
        }

        held.Dispose();
        using WorkerDocumentStateGate.Lease next = await gate.EnterAsync(CancellationToken.None);
    }

    [Fact]
    public async Task LeaseDisposalIsIdempotentAndGateIsReusableAfterTransactionFailure()
    {
        var gate = new WorkerDocumentStateGate();
        WorkerDocumentStateGate.Lease lease = await gate.EnterAsync(CancellationToken.None);
        lease.Dispose();
        lease.Dispose();

        using (WorkerDocumentStateGate.Lease reusable = await gate.EnterAsync(CancellationToken.None))
        {
        }

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            using WorkerDocumentStateGate.Lease faulted = await gate.EnterAsync(CancellationToken.None);
            throw new InvalidOperationException("Injected document-state transaction failure.");
        });

        using WorkerDocumentStateGate.Lease afterFault = await gate.EnterAsync(CancellationToken.None);
    }
}
