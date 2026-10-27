using MarkdownRenderer.Layout;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

public sealed class ImageRelayoutBatchTests
{
    [Fact]
    public void OptedInStormIsCapturedInBoundedSortedBatchesWithoutLossOrDuplication()
    {
        var pending = Enumerable.Range(1, 72).Select(static value => value * 3).ToHashSet();
        var observed = new HashSet<int>();
        int[] batchSizes = [32, 32, 8];

        foreach (int expectedSize in batchSizes)
        {
            int[] captured = ImageRelayoutBatch.CapturePendingIndices(pending, optedIn: true);

            Assert.Equal(expectedSize, captured.Length);
            Assert.Equal(captured.Order().ToArray(), captured);
            Assert.All(captured, value => Assert.True(observed.Add(value)));

            // The work has not been accepted yet. Capture is only a snapshot;
            // it must not consume indices on cancellation or dispatch failure.
            Assert.Equal(72 - observed.Count + captured.Length, pending.Count);
            Assert.All(captured, value => Assert.Contains(value, pending));

            foreach (int value in captured)
                Assert.True(pending.Remove(value));
        }

        Assert.Empty(pending);
        Assert.Equal(72, observed.Count);
    }

    [Fact]
    public void CancellationAndDispatchRejectionLeaveCapturedIndicesAvailableForRetry()
    {
        var pending = Enumerable.Range(100, 72).ToHashSet();
        int[] beforeCancellation = ImageRelayoutBatch.CapturePendingIndices(pending, optedIn: true);
        HashSet<int> originalPending = [.. pending];

        // Model cancellation before worker acceptance, then a rejected UI
        // dispatch. Neither path has permission to remove any dirty index.
        Assert.True(originalPending.SetEquals(pending));
        int[] afterCancellation = ImageRelayoutBatch.CapturePendingIndices(pending, optedIn: true);
        Assert.Equal(beforeCancellation, afterCancellation);

        Assert.True(originalPending.SetEquals(pending));
        int[] afterDispatchRejection = ImageRelayoutBatch.CapturePendingIndices(pending, optedIn: true);
        Assert.Equal(beforeCancellation, afterDispatchRejection);
        Assert.True(originalPending.SetEquals(pending));
    }

    [Fact]
    public void LegacySessionlessCapturePreservesAllPendingIndices()
    {
        var pending = Enumerable.Range(-7, 72).ToHashSet();

        int[] captured = ImageRelayoutBatch.CapturePendingIndices(pending, optedIn: false);

        Assert.Equal(pending.Count, captured.Length);
        Assert.True(pending.SetEquals(captured));
        Assert.Equal(72, pending.Count);
    }

    [Fact]
    public void DrainedLargeStormCapturesOnlyLiveRootsAndRequeuesEarlierRootsFirst()
    {
        var pending = new SortedSet<int>(Enumerable.Range(1, 100_000));
        for (int index = 1; index <= 99_968; index++)
            Assert.True(pending.Remove(index));

        int[] tail = ImageRelayoutBatch.CapturePendingIndices(pending, optedIn: true);
        Assert.Equal(Enumerable.Range(99_969, 32), tail);
        Assert.Equal(32, pending.Count);

        Assert.True(pending.Add(7));
        Assert.False(pending.Add(7));
        int[] retry = ImageRelayoutBatch.CapturePendingIndices(pending, optedIn: true);
        Assert.Equal(7, retry[0]);
        Assert.Equal(Enumerable.Range(99_969, 31), retry.Skip(1));
        Assert.Equal(33, pending.Count);
        foreach (int index in retry)
            Assert.True(pending.Remove(index));
        Assert.Equal([100_000], ImageRelayoutBatch.CapturePendingIndices(pending, optedIn: true));
    }
}
