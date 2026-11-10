using MarkdownRenderer.Images;
using MarkdownRenderer.Svg.Resvg.Internal.Workers;
using MarkdownRenderer.Svg.Resvg.Internal.Workers.Scheduling;
using Xunit;

namespace MarkdownRenderer.Svg.Resvg.Tests.Internal.Workers.Scheduling;

/// <summary>
/// Pins the scheduling queue's ordering: visible-first stable order, worker
/// affinity and its FIFO bypass bound, secondary-worker eligibility, and
/// atomic document-handle affinity updates.
/// </summary>
public sealed class WorkerSchedulingQueueTests
{
    [Fact]
    public void Queue_IsStableVisibleFirstAndRemovesCanceledStaleWork()
    {
        var queue = new WorkerSchedulingQueue<object>();
        object overscan1 = new();
        object canceledOverscan = new();
        object visible1 = new();
        object visible2 = new();
        queue.Enqueue(overscan1, MarkdownSvgRenderPriority.Overscan, isRender: true);
        queue.Enqueue(canceledOverscan, MarkdownSvgRenderPriority.Overscan, isRender: true);
        queue.Enqueue(visible1, MarkdownSvgRenderPriority.Visible, isRender: true);
        queue.Enqueue(visible2, MarkdownSvgRenderPriority.Visible, isRender: true);

        Assert.True(queue.Remove(canceledOverscan));
        Assert.Equal(3, queue.Count);
        Assert.True(queue.TryDequeuePrimary(out object? first));
        Assert.Same(visible1, first);
        Assert.True(queue.TryDequeuePrimary(out object? second));
        Assert.Same(visible2, second);
        Assert.True(queue.TryDequeuePrimary(out object? third));
        Assert.Same(overscan1, third);
    }

    [Fact]
    public void Queue_PrimaryPrefersMatchingWorkerWithinPriorityAndBoundsFifoBypass()
    {
        var queue = new WorkerSchedulingQueue<object>();
        object overscanMatch = new();
        object visibleFifoHead = new();
        object visibleAffinityMatch = new();
        queue.Enqueue(
            overscanMatch,
            MarkdownSvgRenderPriority.Overscan,
            isRender: true,
            preferredWorkerInstanceId: 42);
        queue.Enqueue(visibleFifoHead, MarkdownSvgRenderPriority.Visible, isRender: true);
        queue.Enqueue(
            visibleAffinityMatch,
            MarkdownSvgRenderPriority.Visible,
            isRender: true,
            preferredWorkerInstanceId: 42);

        Assert.True(queue.TryDequeuePrimary(42, out object? first));
        Assert.Same(visibleAffinityMatch, first);

        // A matching stream can bypass the FIFO head only once. This avoids
        // affinity pinning a worker at the expense of older same-priority work.
        Assert.True(queue.TryDequeuePrimary(42, out object? second));
        Assert.Same(visibleFifoHead, second);
        Assert.True(queue.TryDequeuePrimary(42, out object? third));
        Assert.Same(overscanMatch, third);
    }

    [Fact]
    public void Queue_StaleWorkerAffinityFallsBackToFifo()
    {
        var queue = new WorkerSchedulingQueue<object>();
        object first = new();
        object second = new();
        queue.Enqueue(
            first,
            MarkdownSvgRenderPriority.Visible,
            isRender: true,
            preferredWorkerInstanceId: 17);
        queue.Enqueue(
            second,
            MarkdownSvgRenderPriority.Visible,
            isRender: true,
            preferredWorkerInstanceId: 42);

        Assert.True(queue.TryDequeuePrimary(999, out object? selected));
        Assert.Same(first, selected);
    }

    [Fact]
    public void Queue_IdleSecondaryAffinityGetsOldestVisibleHeadBeforePrimary()
    {
        var queue = new WorkerSchedulingQueue<object>();
        object secondaryPreferredHead = new();
        object primaryPreferredNext = new();
        object secondaryOverscan = new();
        queue.Enqueue(
            secondaryPreferredHead,
            MarkdownSvgRenderPriority.Visible,
            isRender: true,
            preferredWorkerInstanceId: 42);
        queue.Enqueue(
            primaryPreferredNext,
            MarkdownSvgRenderPriority.Visible,
            isRender: true,
            preferredWorkerInstanceId: 17);
        queue.Enqueue(
            secondaryOverscan,
            MarkdownSvgRenderPriority.Overscan,
            isRender: true,
            preferredWorkerInstanceId: 42);

        Assert.True(queue.TryDequeueOldestVisibleForSecondaryAffinity(
            secondaryWorkerInstanceId: 42,
            fontCatalogReady: true,
            out object? secondary));
        Assert.Same(secondaryPreferredHead, secondary);

        Assert.True(queue.TryDequeuePrimary(17, out object? primary));
        Assert.Same(primaryPreferredNext, primary);
        Assert.False(queue.TryDequeueSecondary(42, fontCatalogReady: true, out _));
        Assert.True(queue.TryDequeuePrimary(17, out object? last));
        Assert.Same(secondaryOverscan, last);
    }

    [Fact]
    public void Queue_PrePrimaryHandoffDoesNotSkipVisibleOpenOrFontIneligibleHead()
    {
        var openQueue = new WorkerSchedulingQueue<object>();
        object visibleOpen = new();
        object laterSecondaryRender = new();
        openQueue.Enqueue(
            visibleOpen,
            MarkdownSvgRenderPriority.Visible,
            isRender: false,
            isOpen: true,
            preferredWorkerInstanceId: 42);
        openQueue.Enqueue(
            laterSecondaryRender,
            MarkdownSvgRenderPriority.Visible,
            isRender: true,
            preferredWorkerInstanceId: 42);

        Assert.False(openQueue.TryDequeueOldestVisibleForSecondaryAffinity(42, true, out _));
        Assert.True(openQueue.TryDequeuePrimary(17, out object? primary));
        Assert.Same(visibleOpen, primary);
        Assert.True(openQueue.TryDequeueOldestVisibleForSecondaryAffinity(42, true, out object? secondary));
        Assert.Same(laterSecondaryRender, secondary);

        var coldQueue = new WorkerSchedulingQueue<object>();
        object visibleText = new();
        object laterNonText = new();
        coldQueue.Enqueue(
            visibleText,
            MarkdownSvgRenderPriority.Visible,
            isRender: true,
            requiresFontCatalog: true,
            preferredWorkerInstanceId: 17);
        coldQueue.Enqueue(
            laterNonText,
            MarkdownSvgRenderPriority.Visible,
            isRender: true,
            preferredWorkerInstanceId: 42);

        Assert.False(coldQueue.TryDequeueOldestVisibleForSecondaryAffinity(42, false, out _));
        Assert.True(coldQueue.TryDequeuePrimary(17, out primary));
        Assert.Same(visibleText, primary);
        Assert.True(coldQueue.TryDequeueOldestVisibleForSecondaryAffinity(42, false, out secondary));
        Assert.Same(laterNonText, secondary);
    }

    [Fact]
    public void Secondary_ConsumesOnlyVisibleRenderWork()
    {
        var queue = new WorkerSchedulingQueue<object>();
        object visibleOpen = new();
        object overscanRender = new();
        object visibleRender = new();
        queue.Enqueue(visibleOpen, MarkdownSvgRenderPriority.Visible, isRender: false);
        queue.Enqueue(overscanRender, MarkdownSvgRenderPriority.Overscan, isRender: true);
        queue.Enqueue(visibleRender, MarkdownSvgRenderPriority.Visible, isRender: true);

        Assert.Equal(1, queue.VisibleRenderCount);
        Assert.True(queue.TryDequeueSecondary(fontCatalogReady: true, out object? selected));
        Assert.Same(visibleRender, selected);
        Assert.Equal(2, queue.Count);
    }

    [Fact]
    public void Secondary_TextWaitsForFontsButVisibleNonTextRemainsEligible()
    {
        var queue = new WorkerSchedulingQueue<object>();
        object text = new();
        object nonText = new();
        queue.Enqueue(text, MarkdownSvgRenderPriority.Visible, isRender: true, requiresFontCatalog: true);
        queue.Enqueue(nonText, MarkdownSvgRenderPriority.Visible, isRender: true);

        Assert.Equal(1, queue.VisibleTextRenderCount);
        Assert.True(queue.TryDequeueSecondary(fontCatalogReady: false, out object? selected));
        Assert.Same(nonText, selected);
        Assert.False(queue.TryDequeueSecondary(fontCatalogReady: false, out _));
        Assert.True(queue.TryDequeueSecondary(fontCatalogReady: true, out selected));
        Assert.Same(text, selected);
    }

    [Fact]
    public void SecondaryAffinityPreservesFontAndRenderEligibility()
    {
        var coldQueue = new WorkerSchedulingQueue<object>();
        object matchingText = new();
        object eligibleNonText = new();
        coldQueue.Enqueue(
            matchingText,
            MarkdownSvgRenderPriority.Visible,
            isRender: true,
            requiresFontCatalog: true,
            preferredWorkerInstanceId: 42);
        coldQueue.Enqueue(eligibleNonText, MarkdownSvgRenderPriority.Visible, isRender: true);

        Assert.True(coldQueue.TryDequeueSecondary(42, fontCatalogReady: false, out object? coldSelected));
        Assert.Same(eligibleNonText, coldSelected);
        Assert.False(coldQueue.TryDequeueSecondary(42, fontCatalogReady: false, out _));

        var readyQueue = new WorkerSchedulingQueue<object>();
        object fifoText = new();
        object affinityText = new();
        object matchingOpen = new();
        object matchingOverscan = new();
        readyQueue.Enqueue(
            fifoText,
            MarkdownSvgRenderPriority.Visible,
            isRender: true,
            requiresFontCatalog: true,
            preferredWorkerInstanceId: 17);
        readyQueue.Enqueue(
            affinityText,
            MarkdownSvgRenderPriority.Visible,
            isRender: true,
            requiresFontCatalog: true,
            preferredWorkerInstanceId: 42);
        readyQueue.Enqueue(
            matchingOpen,
            MarkdownSvgRenderPriority.Visible,
            isRender: false,
            isOpen: true,
            preferredWorkerInstanceId: 42);
        readyQueue.Enqueue(
            matchingOverscan,
            MarkdownSvgRenderPriority.Overscan,
            isRender: true,
            preferredWorkerInstanceId: 42);

        Assert.True(readyQueue.TryDequeueSecondary(42, fontCatalogReady: true, out object? readySelected));
        Assert.Same(affinityText, readySelected);
        Assert.True(readyQueue.TryDequeueSecondary(42, fontCatalogReady: true, out readySelected));
        Assert.Same(fifoText, readySelected);
        Assert.False(readyQueue.TryDequeueSecondary(42, fontCatalogReady: true, out _));
        Assert.Equal(2, readyQueue.Count);
    }

    [Fact]
    public void DocumentHandleWorkerAffinityCanBeUpdatedAtomicallyAfterReattachment()
    {
        var document = new WorkerDocumentHandle(123, preferredWorkerInstanceId: 17);
        Assert.Equal(17, document.PreferredWorkerInstanceId);

        document.PreferWorker(42);
        document.PreferWorker(0);

        Assert.Equal(42, document.PreferredWorkerInstanceId);
    }
}
