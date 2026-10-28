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

    [Fact]
    public void SplitDrainHoldRehydratesItsPressEpochForLaterTap()
    {
        var queue = new DeferredInputQueue<DeferredTouchInput>(capacity: 4);
        var policy = new TouchTapSuppressionPolicy();
        var point = new Windows.Foundation.Point(24, 40);
        const uint pointerId = 31;

        // PointerPressed is queued before it can establish the primary epoch.
        Assert.True(queue.TryEnqueue(
            new DeferredTouchInput(DeferredTouchInputKind.Press, pointerId, point, IsInContact: true),
            pointerId,
            coalescible: false));
        Assert.True(queue.TryGetAt(0, out DeferredTouchInput queuedPress));
        var resolver = new DeferredTouchGestureOwnerResolver(point);
        resolver.ObserveTouchPress(queuedPress.PointerId, queuedPress.Position, queuedPress.IsInContact);
        Assert.True(resolver.TryGetOwnerPointerId(out uint deferredOwnerPointerId));
        Assert.Equal(pointerId, deferredOwnerPointerId);

        // Hold is recognized and dispatched before the retry timer drains the
        // press. The queued gesture carries the contact identity, not epoch 0.
        Assert.True(queue.TryEnqueue(
            new DeferredTouchInput(
                DeferredTouchInputKind.Hold,
                deferredOwnerPointerId,
                point,
                DeferredOwnerPointerId: deferredOwnerPointerId),
            pointerId,
            coalescible: false,
            blocksSnapshotInteraction: false));

        Assert.True(queue.TryDequeue(out DeferredTouchInput press));
        Assert.Equal(DeferredTouchInputKind.Press, press.Kind);
        Assert.True(policy.TryBeginContact(press.PointerId, out long holdEpoch));
        Assert.True(queue.TryDequeue(out DeferredTouchInput hold));
        Assert.Equal(DeferredTouchInputKind.Hold, hold.Kind);
        Assert.Equal(holdEpoch, policy.GetGestureEpochForPointerOrCompleted(hold.DeferredOwnerPointerId));
        policy.SuppressTouchTap(holdEpoch, point, nowTickMs: 1_000);

        // Release drains in a later batch. A later Tapped dispatch must recover
        // the completed epoch rather than lose suppression because its initial
        // event was observed after the hold's first batch completed.
        Assert.True(queue.TryEnqueue(
            new DeferredTouchInput(
                DeferredTouchInputKind.Release,
                pointerId,
                point,
                TouchTapGestureEpoch: holdEpoch),
            pointerId,
            coalescible: false));
        Assert.True(queue.TryDequeue(out DeferredTouchInput release));
        Assert.Equal(DeferredTouchInputKind.Release, release.Kind);
        Assert.Equal(holdEpoch, release.TouchTapGestureEpoch);
        Assert.True(policy.TryEndContact(release.PointerId, release.TouchTapGestureEpoch));
        Assert.True(policy.TryGetGestureEpochForRecognizedGesture(
            point,
            out long tapEpoch,
            out uint tapPointerId,
            out bool ambiguous));
        Assert.False(ambiguous);
        Assert.Equal(pointerId, tapPointerId);
        Assert.Equal(holdEpoch, tapEpoch);
        Assert.True(policy.ShouldSuppressTap(tapEpoch, nowTickMs: 1_200, point));
    }

    [Fact]
    public void NearbySecondaryRecognizedGestureCannotBorrowPrimaryOwner()
    {
        var policy = new TouchTapSuppressionPolicy();
        var primaryPoint = new Windows.Foundation.Point(8, 40);
        var secondaryPoint = new Windows.Foundation.Point(80, 40);
        Assert.True(policy.TryBeginContact(17, out long primaryEpoch));
        policy.SuppressTouchTap(primaryEpoch, primaryPoint, nowTickMs: 1_000);

        // This is the production branch where the touch selection session
        // rejects a second ID while retaining the first contact's selection.
        policy.TrackUnownedContact(18, secondaryPoint);
        Assert.False(policy.TryGetGestureEpochForRecognizedGesture(
            secondaryPoint,
            out long secondaryEpoch,
            out _,
            out bool secondaryAmbiguous));
        Assert.True(secondaryAmbiguous);
        Assert.Equal(0L, secondaryEpoch);

        // The primary's own recognized event remains attributable when its
        // location is outside the competing contact's spatial band.
        Assert.True(policy.TryGetGestureEpochForRecognizedGesture(
            primaryPoint,
            out long recognizedPrimaryEpoch,
            out uint recognizedPrimaryId,
            out bool primaryAmbiguous));
        Assert.False(primaryAmbiguous);
        Assert.Equal(primaryEpoch, recognizedPrimaryEpoch);
        Assert.Equal(17u, recognizedPrimaryId);

        // PointerReleased leaves the secondary origin available for the
        // ID-less trailing Tapped event, then the recognized gesture is ignored
        // instead of being attributed to the still-completed primary owner.
        policy.EndContact(18);
        Assert.False(policy.TryGetGestureEpochForRecognizedGesture(
            secondaryPoint,
            out _,
            out _,
            out bool releasedSecondaryAmbiguous));
        Assert.True(releasedSecondaryAmbiguous);
    }

    private enum DeferredTouchInputKind
    {
        Press,
        Hold,
        Release,
    }

    private readonly record struct DeferredTouchInput(
        DeferredTouchInputKind Kind,
        uint PointerId,
        Windows.Foundation.Point Position,
        bool IsInContact = false,
        uint DeferredOwnerPointerId = 0,
        long TouchTapGestureEpoch = 0);
}
