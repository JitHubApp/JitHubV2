using MarkdownRenderer.Controls;
using Xunit;

namespace MarkdownRenderer.Tests;

public sealed class DeferredInputQueueTests
{
    [Fact]
    public void CoalescesMovesWithoutDroppingPressOrRelease()
    {
        var queue = new DeferredInputQueue<string>(3);
        Assert.True(queue.TryEnqueue("press", 1, false));
        Assert.True(queue.TryEnqueue("move-old", 1, true));
        Assert.True(queue.TryEnqueue("move-latest", 1, true));
        Assert.True(queue.TryEnqueue("release", 1, false));
        Assert.Equal(3, queue.Count);
        Assert.True(queue.TryDequeue(out var first));
        Assert.Equal("press", first);
        Assert.True(queue.TryDequeue(out var second));
        Assert.Equal("move-latest", second);
        Assert.True(queue.TryDequeue(out var third));
        Assert.Equal("release", third);
        Assert.False(queue.TryDequeue(out _));
    }

    [Fact]
    public void DifferentPointersAndTransitionsNeverCoalesce()
    {
        var queue = new DeferredInputQueue<int>(3);
        Assert.True(queue.TryEnqueue(1, 1, true));
        Assert.True(queue.TryEnqueue(2, 2, true));
        Assert.True(queue.TryEnqueue(3, 2, false));
        Assert.False(queue.TryEnqueue(4, 2, true));
        Assert.Equal(3, queue.Count);
        queue.Clear();
        Assert.Equal(0, queue.Count);
        Assert.True(queue.TryEnqueue(5, 3, false));
        Assert.True(queue.TryDequeue(out int value));
        Assert.Equal(5, value);
    }

    [Fact]
    public void ReusesRingSlotsAndReleasesPayloads()
    {
        var queue = new DeferredInputQueue<int>(2);
        for (int index = 0; index < 1_000; index++)
        {
            Assert.True(queue.TryEnqueue(index, 1, false));
            Assert.True(queue.TryDequeue(out int value));
            Assert.Equal(index, value);
        }
        Assert.Equal(0, queue.Count);
    }

    [Fact]
    public void GestureWaitersArePeekedInFifoOrderWithoutBlockingSnapshotMutation()
    {
        var queue = new DeferredInputQueue<string>(4);

        Assert.True(queue.TryEnqueue("gesture-first", 0, false, blocksSnapshotInteraction: false));
        Assert.True(queue.TryEnqueue("physical-press", 7, false));
        Assert.True(queue.TryEnqueue("gesture-second", 0, false, blocksSnapshotInteraction: false));

        Assert.Equal(3, queue.Count);
        Assert.Equal(1, queue.BlockingSnapshotInteractionCount);
        Assert.True(queue.HasBlockingSnapshotInteractions);

        // A lazy-geometry waiter is intentionally not an owner of the read
        // side of the snapshot lock: layout publication must be able to make
        // the missing geometry available while it stays queued.
        Assert.True(queue.TryPeek(out string first, out bool firstBlocksSnapshot));
        Assert.Equal("gesture-first", first);
        Assert.False(firstBlocksSnapshot);
        Assert.Equal(3, queue.Count);
        Assert.Equal(1, queue.BlockingSnapshotInteractionCount);

        Assert.True(queue.TryPeek(out string stillFirst, out bool stillFirstBlocksSnapshot));
        Assert.Equal("gesture-first", stillFirst);
        Assert.False(stillFirstBlocksSnapshot);
        Assert.Equal(3, queue.Count);

        Assert.True(queue.TryDequeue(out string dequeuedGesture));
        Assert.Equal("gesture-first", dequeuedGesture);
        Assert.Equal(2, queue.Count);
        Assert.Equal(1, queue.BlockingSnapshotInteractionCount);
        Assert.True(queue.TryPeek(out string physical, out bool physicalBlocksSnapshot));
        Assert.Equal("physical-press", physical);
        Assert.True(physicalBlocksSnapshot);

        Assert.True(queue.TryDequeue(out string dequeuedPhysical));
        Assert.Equal("physical-press", dequeuedPhysical);
        Assert.Equal(0, queue.BlockingSnapshotInteractionCount);
        Assert.False(queue.HasBlockingSnapshotInteractions);

        Assert.True(queue.TryPeek(out string last, out bool lastBlocksSnapshot));
        Assert.Equal("gesture-second", last);
        Assert.False(lastBlocksSnapshot);
        Assert.True(queue.TryDequeue(out string dequeuedLast));
        Assert.Equal("gesture-second", dequeuedLast);
        Assert.Equal(0, queue.Count);
        Assert.Equal(0, queue.BlockingSnapshotInteractionCount);
        Assert.False(queue.HasBlockingSnapshotInteractions);
        Assert.False(queue.TryPeek(out _, out _));
    }

    [Fact]
    public void MixedCoalescingKeepsBlockingClassAndCountsAcrossRingWrapAndClear()
    {
        var queue = new DeferredInputQueue<string>(3);

        Assert.True(queue.TryEnqueue("gesture-move-old", 12, true, blocksSnapshotInteraction: false));
        Assert.True(queue.TryEnqueue("gesture-move-latest", 12, true, blocksSnapshotInteraction: false));
        Assert.Equal(1, queue.Count);
        Assert.Equal(0, queue.BlockingSnapshotInteractionCount);
        Assert.True(queue.TryPeek(out string gesture, out bool gestureBlocks));
        Assert.Equal("gesture-move-latest", gesture);
        Assert.False(gestureBlocks);

        // Same pointer and coalescing flag is insufficient when the ownership
        // class differs: physical input remains blocking and cannot replace a
        // nonblocking synthetic waiter (or vice versa).
        Assert.True(queue.TryEnqueue("physical-move-old", 12, true));
        Assert.True(queue.TryEnqueue("physical-move-latest", 12, true));
        Assert.Equal(2, queue.Count);
        Assert.Equal(1, queue.BlockingSnapshotInteractionCount);

        Assert.True(queue.TryDequeue(out string first));
        Assert.Equal("gesture-move-latest", first);
        Assert.Equal(1, queue.BlockingSnapshotInteractionCount);
        Assert.True(queue.TryEnqueue("next-gesture", 0, false, blocksSnapshotInteraction: false));
        Assert.True(queue.TryEnqueue("overflow", 0, false, blocksSnapshotInteraction: false));
        Assert.Equal(3, queue.Count);
        Assert.Equal(1, queue.BlockingSnapshotInteractionCount);
        Assert.False(queue.TryEnqueue("rejected-physical", 99, false));
        Assert.Equal(3, queue.Count);
        Assert.Equal(1, queue.BlockingSnapshotInteractionCount);

        Assert.True(queue.TryDequeue(out string physical));
        Assert.Equal("physical-move-latest", physical);
        Assert.Equal(0, queue.BlockingSnapshotInteractionCount);
        Assert.True(queue.TryDequeue(out string nextGesture));
        Assert.Equal("next-gesture", nextGesture);
        Assert.True(queue.TryDequeue(out string overflow));
        Assert.Equal("overflow", overflow);
        Assert.False(queue.HasBlockingSnapshotInteractions);

        Assert.True(queue.TryEnqueue("wrap-physical", 4, false));
        Assert.Equal(1, queue.BlockingSnapshotInteractionCount);
        queue.Clear();
        Assert.Equal(0, queue.Count);
        Assert.Equal(0, queue.BlockingSnapshotInteractionCount);
        Assert.False(queue.HasBlockingSnapshotInteractions);
        Assert.False(queue.TryPeek(out _, out _));
        Assert.True(queue.TryEnqueue("after-clear-gesture", 0, false, blocksSnapshotInteraction: false));
        Assert.True(queue.TryPeek(out string afterClear, out bool afterClearBlocks));
        Assert.Equal("after-clear-gesture", afterClear);
        Assert.False(afterClearBlocks);
        Assert.Equal(0, queue.BlockingSnapshotInteractionCount);
    }

    [Fact]
    public void HeadReplacementChangesOnlySnapshotOwnershipAndKeepsFifoCoalescingMetadata()
    {
        var queue = new DeferredInputQueue<string>(3);
        Assert.False(queue.TryReplaceHead("missing", blocksSnapshotInteraction: false));
        Assert.Equal(0, queue.Count);
        Assert.Equal(0, queue.BlockingSnapshotInteractionCount);

        Assert.True(queue.TryEnqueue("physical-head", 8, coalescible: true));
        Assert.True(queue.TryEnqueue("following-press", 9, coalescible: false));
        Assert.Equal(2, queue.BlockingSnapshotInteractionCount);

        Assert.True(queue.TryReplaceHead("head-waits-for-layout", blocksSnapshotInteraction: false));
        Assert.Equal(2, queue.Count);
        Assert.Equal(1, queue.BlockingSnapshotInteractionCount);
        Assert.True(queue.TryPeek(out string waitingHead, out bool waitingBlocks));
        Assert.Equal("head-waits-for-layout", waitingHead);
        Assert.False(waitingBlocks);

        Assert.True(queue.TryReplaceHead("head-resumes-physical-ownership", blocksSnapshotInteraction: true));
        Assert.Equal(2, queue.BlockingSnapshotInteractionCount);
        Assert.True(queue.TryReplaceHead("head-waits-again", blocksSnapshotInteraction: false));
        Assert.Equal(1, queue.BlockingSnapshotInteractionCount);
        Assert.True(queue.TryDequeue(out string first));
        Assert.Equal("head-waits-again", first);
        Assert.True(queue.TryPeek(out string following, out bool followingBlocks));
        Assert.Equal("following-press", following);
        Assert.True(followingBlocks);
        Assert.True(queue.TryDequeue(out string second));
        Assert.Equal("following-press", second);
        Assert.Equal(0, queue.BlockingSnapshotInteractionCount);

        var coalescingQueue = new DeferredInputQueue<string>(2);
        Assert.True(coalescingQueue.TryEnqueue("move-before-wait", 17, coalescible: true));
        Assert.True(coalescingQueue.TryReplaceHead("deferred-move", blocksSnapshotInteraction: false));
        Assert.Equal(0, coalescingQueue.BlockingSnapshotInteractionCount);
        Assert.True(coalescingQueue.TryEnqueue(
            "latest-deferred-move",
            17,
            coalescible: true,
            blocksSnapshotInteraction: false));
        Assert.Equal(1, coalescingQueue.Count);
        Assert.Equal(0, coalescingQueue.BlockingSnapshotInteractionCount);
        Assert.True(coalescingQueue.TryPeek(out string coalesced, out bool coalescedBlocks));
        Assert.Equal("latest-deferred-move", coalesced);
        Assert.False(coalescedBlocks);
    }
}
