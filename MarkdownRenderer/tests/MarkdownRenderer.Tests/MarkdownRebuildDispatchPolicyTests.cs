using MarkdownRenderer.Controls;
using Xunit;

namespace MarkdownRenderer.Tests;

public sealed class MarkdownRebuildDispatchPolicyTests
{
    [Fact]
    public void SelectPriority_UsesNormalOnlyForFullRebuilds()
    {
        Assert.Equal(
            MarkdownRebuildDispatchPriority.Low,
            MarkdownRebuildDispatchPolicy.SelectPriority(requiresFullRebuild: false));
        Assert.Equal(
            MarkdownRebuildDispatchPriority.Normal,
            MarkdownRebuildDispatchPolicy.SelectPriority(requiresFullRebuild: true));
    }

    [Fact]
    public void QueuedRestylesCoalesceBehindExistingLowPriorityCallback()
    {
        var queued = default(MarkdownRebuildDispatchState).AfterEnqueueAttempt(
            ticket: 1,
            MarkdownRebuildDispatchPriority.Low,
            succeeded: true);

        Assert.True(queued.IsQueued);
        Assert.False(queued.ShouldSchedule(MarkdownRebuildDispatchPriority.Low));
        Assert.True(queued.IsCurrent(1));
    }

    [Fact]
    public void FullRebuildPromotesQueuedRestyleAndInvalidatesItsTicket()
    {
        var queuedLow = default(MarkdownRebuildDispatchState).AfterEnqueueAttempt(
            ticket: 11,
            MarkdownRebuildDispatchPriority.Low,
            succeeded: true);

        Assert.True(queuedLow.ShouldSchedule(MarkdownRebuildDispatchPriority.Normal));

        MarkdownRebuildDispatchState promoted = queuedLow.AfterEnqueueAttempt(
            ticket: 12,
            MarkdownRebuildDispatchPriority.Normal,
            succeeded: true);

        Assert.False(promoted.IsCurrent(11));
        Assert.True(promoted.IsCurrent(12));
        Assert.False(promoted.ShouldSchedule(MarkdownRebuildDispatchPriority.Low));
        Assert.False(promoted.ShouldSchedule(MarkdownRebuildDispatchPriority.Normal));
    }

    [Fact]
    public void FailedPromotionLeavesQueuedRestyleAuthoritative()
    {
        var queuedLow = default(MarkdownRebuildDispatchState).AfterEnqueueAttempt(
            ticket: 21,
            MarkdownRebuildDispatchPriority.Low,
            succeeded: true);

        MarkdownRebuildDispatchState afterFailure = queuedLow.AfterEnqueueAttempt(
            ticket: 22,
            MarkdownRebuildDispatchPriority.Normal,
            succeeded: false);

        Assert.Equal(queuedLow, afterFailure);
        Assert.True(afterFailure.IsCurrent(21));
        Assert.False(afterFailure.IsCurrent(22));
    }

    [Fact]
    public void LowerPriorityAttemptCannotReplaceQueuedFullRebuild()
    {
        var queuedNormal = default(MarkdownRebuildDispatchState).AfterEnqueueAttempt(
            ticket: 31,
            MarkdownRebuildDispatchPriority.Normal,
            succeeded: true);

        MarkdownRebuildDispatchState afterLowAttempt = queuedNormal.AfterEnqueueAttempt(
            ticket: 32,
            MarkdownRebuildDispatchPriority.Low,
            succeeded: true);

        Assert.Equal(queuedNormal, afterLowAttempt);
        Assert.True(afterLowAttempt.IsCurrent(31));
        Assert.False(afterLowAttempt.IsCurrent(32));
    }

    [Fact]
    public void SuccessfulEnqueueRejectsZeroTicket()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            default(MarkdownRebuildDispatchState).AfterEnqueueAttempt(
                ticket: 0,
                MarkdownRebuildDispatchPriority.Low,
                succeeded: true));
    }
}
