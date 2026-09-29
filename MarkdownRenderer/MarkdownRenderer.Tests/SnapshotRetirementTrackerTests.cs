using MarkdownRenderer.Controls;
using Xunit;

namespace MarkdownRenderer.Tests;

public sealed class SnapshotRetirementTrackerTests
{
    [Fact]
    public async Task AFailedDrainDoesNotPoisonLaterRetirementsOrDrains()
    {
        var tracker = new SnapshotRetirementTracker();
        var firstFailure = new InvalidOperationException("first retirement");
        tracker.Track(Task.FromException(firstFailure));

        InvalidOperationException first = await Assert.ThrowsAsync<InvalidOperationException>(
            () => tracker.DrainAsync());
        Assert.Same(firstFailure, first);
        await tracker.DrainAsync();

        var secondFailure = new NotSupportedException("second retirement");
        tracker.Track(Task.FromException(secondFailure));
        NotSupportedException second = await Assert.ThrowsAsync<NotSupportedException>(
            () => tracker.DrainAsync());
        Assert.Same(secondFailure, second);
        await tracker.DrainAsync();

        Assert.Equal(0, tracker.RetainedFailureDetailCount);
        Assert.Equal(0, tracker.OmittedFailureCount);
    }

    [Fact]
    public async Task RepeatedFaultsWithoutDrainRetainBoundedDetailAndReportOmissions()
    {
        int observed = 0;
        var tracker = new SnapshotRetirementTracker(_ => Interlocked.Increment(ref observed));
        int total = SnapshotRetirementTracker.MaximumRetainedFailureDetails + 17;

        for (int index = 0; index < total; index++)
        {
            tracker.Track(Task.FromException(
                new InvalidOperationException($"retirement {index}")));
        }

        Assert.Equal(total, Volatile.Read(ref observed));
        Assert.Equal(
            SnapshotRetirementTracker.MaximumRetainedFailureDetails,
            tracker.RetainedFailureDetailCount);
        Assert.Equal(17, tracker.OmittedFailureCount);

        AggregateException failure = await Assert.ThrowsAsync<AggregateException>(
            () => tracker.DrainAsync());
        Assert.Equal(
            SnapshotRetirementTracker.MaximumRetainedFailureDetails + 1,
            failure.InnerExceptions.Count);
        for (int index = 0;
             index < SnapshotRetirementTracker.MaximumRetainedFailureDetails;
             index++)
        {
            Assert.Equal(
                $"retirement {index}",
                failure.InnerExceptions[index].Message);
        }
        Assert.Contains("17 additional", failure.InnerExceptions[^1].Message);
        Assert.Equal(0, tracker.RetainedFailureDetailCount);
        Assert.Equal(0, tracker.OmittedFailureCount);
        await tracker.DrainAsync();
    }

    [Fact]
    public async Task ConcurrentRetirementFaultsAreReportedInRegistrationOrder()
    {
        var tracker = new SnapshotRetirementTracker();
        var first = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        tracker.Track(first.Task);
        tracker.Track(second.Task);
        Task drain = tracker.DrainAsync();

        second.TrySetException(new NotSupportedException("second"));
        first.TrySetException(new InvalidOperationException("first"));

        AggregateException failure = await Assert.ThrowsAsync<AggregateException>(() => drain);
        Assert.Collection(
            failure.InnerExceptions,
            exception => Assert.Equal("first", exception.Message),
            exception => Assert.Equal("second", exception.Message));
    }
}
