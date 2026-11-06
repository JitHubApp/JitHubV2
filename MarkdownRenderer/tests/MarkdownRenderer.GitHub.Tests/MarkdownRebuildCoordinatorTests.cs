using System;
using System.Threading;
using System.Threading.Tasks;
using MarkdownRenderer.Controls;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

public sealed class MarkdownRebuildCoordinatorTests
{
    [Fact]
    public void HasPendingOrBuildingCoversIdlePendingAndActiveReplacementStates()
    {
        var coordinator = new MarkdownRebuildCoordinator();

        Assert.False(coordinator.HasPendingOrBuilding);
        Assert.Equal(
            MarkdownRebuildRequestDisposition.Supersede,
            coordinator.Request(MarkdownRebuildCoordinatorReason.Full));
        Assert.True(coordinator.HasPendingOrBuilding);

        Assert.True(coordinator.TryBeginPipeline(11, out _));
        Assert.True(coordinator.HasPendingOrBuilding);

        // CompletePipeline is outcome-agnostic. Success, failure, and
        // cancellation all end the active-build barrier at this same seam.
        Assert.True(coordinator.CompletePipeline(11));
        Assert.False(coordinator.HasPendingOrBuilding);
    }

    [Fact]
    public void HasPendingOrBuildingRemainsSetForDeferredSceneFollowup()
    {
        var coordinator = new MarkdownRebuildCoordinator();
        coordinator.Request(MarkdownRebuildCoordinatorReason.Full);
        Assert.True(coordinator.TryBeginPipeline(12, out _));
        Assert.True(coordinator.HasPendingOrBuilding);

        Assert.Equal(
            MarkdownRebuildRequestDisposition.Deferred,
            coordinator.Request(MarkdownRebuildCoordinatorReason.SceneMaterialized));
        Assert.True(coordinator.HasPendingOrBuilding);

        Assert.True(coordinator.CompletePipeline(12));
        Assert.True(coordinator.HasPendingOrBuilding);
        Assert.True(coordinator.TryGetDispatchReason(out var reason));
        Assert.Equal(MarkdownRebuildCoordinatorReason.SceneMaterialized, reason);

        Assert.True(coordinator.TryBeginPipeline(13, out _));
        Assert.True(coordinator.HasPendingOrBuilding);
        Assert.True(coordinator.CompletePipeline(13));
        Assert.False(coordinator.HasPendingOrBuilding);
    }

    [Fact]
    public void StaleCompletionCannotClearReplacementBuildBarrier()
    {
        var coordinator = new MarkdownRebuildCoordinator();
        coordinator.Request(MarkdownRebuildCoordinatorReason.Full);
        Assert.True(coordinator.TryBeginPipeline(14, out _));

        Assert.Equal(
            MarkdownRebuildRequestDisposition.Supersede,
            coordinator.Request(MarkdownRebuildCoordinatorReason.Restyle));
        Assert.True(coordinator.TryBeginPipeline(15, out _));
        Assert.True(coordinator.HasPendingOrBuilding);

        Assert.False(coordinator.CompletePipeline(14));
        Assert.True(coordinator.HasPendingOrBuilding);
        Assert.True(coordinator.CompletePipeline(15));
        Assert.False(coordinator.HasPendingOrBuilding);
    }

    [Fact]
    public void SceneRequestsUseNeutralPendingStateAndReasonPrecedence()
    {
        var coordinator = new MarkdownRebuildCoordinator();

        Assert.Equal(
            MarkdownRebuildRequestDisposition.Queue,
            coordinator.Request(MarkdownRebuildCoordinatorReason.SceneMaterialized));
        Assert.True(coordinator.TryGetDispatchReason(out var firstPending));
        Assert.Equal(MarkdownRebuildCoordinatorReason.SceneMaterialized, firstPending);
        Assert.Equal(
            MarkdownRebuildRequestDisposition.Queue,
            coordinator.Request(MarkdownRebuildCoordinatorReason.SceneMaterialized));
        Assert.True(coordinator.TryGetDispatchReason(out var secondPending));
        Assert.Equal(MarkdownRebuildCoordinatorReason.SceneMaterialized, secondPending);

        Assert.Equal(
            MarkdownRebuildRequestDisposition.Supersede,
            coordinator.Request(MarkdownRebuildCoordinatorReason.Restyle));
        Assert.True(coordinator.TryGetDispatchReason(out var restylePending));
        Assert.Equal(MarkdownRebuildCoordinatorReason.Restyle, restylePending);
        Assert.Equal(
            MarkdownRebuildRequestDisposition.Supersede,
            coordinator.Request(MarkdownRebuildCoordinatorReason.Full));
        Assert.True(coordinator.TryGetDispatchReason(out var fullPending));
        Assert.Equal(MarkdownRebuildCoordinatorReason.Full, fullPending);
        Assert.Equal(
            MarkdownRebuildCoordinatorReason.Full,
            MarkdownRebuildCoordinator.Merge(
                MarkdownRebuildCoordinatorReason.Full,
                MarkdownRebuildCoordinatorReason.SceneMaterialized));

        MarkdownRebuildCoordinatorReason[] reasons =
        [
            MarkdownRebuildCoordinatorReason.SceneMaterialized,
            MarkdownRebuildCoordinatorReason.Restyle,
            MarkdownRebuildCoordinatorReason.Full,
        ];
        foreach (MarkdownRebuildCoordinatorReason left in reasons)
        foreach (MarkdownRebuildCoordinatorReason right in reasons)
        {
            Assert.Equal(
                (int)left >= (int)right ? left : right,
                MarkdownRebuildCoordinator.Merge(left, right));
        }
    }

    [Fact]
    public void MultipleMaterializationsDuringBuildProduceOneFollowUp()
    {
        var coordinator = new MarkdownRebuildCoordinator();
        Assert.Equal(
            MarkdownRebuildRequestDisposition.Supersede,
            coordinator.Request(MarkdownRebuildCoordinatorReason.Full));
        Assert.True(coordinator.TryBeginPipeline(21, out var startedReason));
        Assert.Equal(MarkdownRebuildCoordinatorReason.Full, startedReason);

        for (int i = 0; i < 8; i++)
        {
            Assert.Equal(
                MarkdownRebuildRequestDisposition.Deferred,
                coordinator.Request(MarkdownRebuildCoordinatorReason.SceneMaterialized));
        }

        Assert.False(coordinator.TryGetDispatchReason(out _));
        Assert.False(coordinator.TryBeginPipeline(22, out _));
        Assert.True(coordinator.CompletePipeline(21));
        Assert.True(coordinator.TryGetDispatchReason(out var pendingReason));
        Assert.Equal(MarkdownRebuildCoordinatorReason.SceneMaterialized, pendingReason);

        Assert.True(coordinator.TryBeginPipeline(22, out var followUpReason));
        Assert.Equal(MarkdownRebuildCoordinatorReason.SceneMaterialized, followUpReason);
        Assert.False(coordinator.HasPending);
        Assert.True(coordinator.CompletePipeline(22));
        Assert.False(coordinator.HasPending);
    }

    [Fact]
    public void SceneRequestReenteredDuringSynchronousCompletionIsNotLost()
    {
        var coordinator = new MarkdownRebuildCoordinator();
        coordinator.Request(MarkdownRebuildCoordinatorReason.Full);
        Assert.True(coordinator.TryBeginPipeline(25, out _));

        // RebuildAsync may raise RenderCompleted synchronously before its Task
        // is returned to the caller. The coordinator is already marked active,
        // so a subscriber's scene request is retained as the next batch.
        Assert.Equal(
            MarkdownRebuildRequestDisposition.Deferred,
            coordinator.Request(MarkdownRebuildCoordinatorReason.SceneMaterialized));
        Assert.True(coordinator.CompletePipeline(25));
        Assert.True(coordinator.TryBeginPipeline(26, out var followUpReason));
        Assert.Equal(MarkdownRebuildCoordinatorReason.SceneMaterialized, followUpReason);
    }

    [Fact]
    public void GenuineRestyleSupersessionAbsorbsDeferredSceneAndFencesOldCompletion()
    {
        var coordinator = new MarkdownRebuildCoordinator();
        coordinator.Request(MarkdownRebuildCoordinatorReason.Full);
        Assert.True(coordinator.TryBeginPipeline(31, out _));
        Assert.Equal(
            MarkdownRebuildRequestDisposition.Deferred,
            coordinator.Request(MarkdownRebuildCoordinatorReason.SceneMaterialized));

        Assert.Equal(
            MarkdownRebuildRequestDisposition.Supersede,
            coordinator.Request(MarkdownRebuildCoordinatorReason.Restyle));
        Assert.True(coordinator.TryGetDispatchReason(out var pendingReason));
        Assert.Equal(MarkdownRebuildCoordinatorReason.Restyle, pendingReason);
        Assert.True(coordinator.TryBeginPipeline(32, out var replacementReason));
        Assert.Equal(MarkdownRebuildCoordinatorReason.Restyle, replacementReason);

        Assert.False(coordinator.CompletePipeline(31));
        Assert.True(coordinator.CompletePipeline(32));
        Assert.False(coordinator.HasPending);
    }

    [Fact]
    public void TerminalPipelineCompletionDrainsDeferredSceneIndependentOfOutcome()
    {
        var coordinator = new MarkdownRebuildCoordinator();
        coordinator.Request(MarkdownRebuildCoordinatorReason.Full);
        Assert.True(coordinator.TryBeginPipeline(41, out _));
        Assert.Equal(
            MarkdownRebuildRequestDisposition.Deferred,
            coordinator.Request(MarkdownRebuildCoordinatorReason.SceneMaterialized));

        // The state machine intentionally treats completion outcome as
        // irrelevant: success, caught failure, and cancellation all reach the
        // same drain boundary. The production observer tests task faults but
        // still calls CompletePipeline for each terminal task.
        Assert.True(coordinator.CompletePipeline(41));
        Assert.True(coordinator.TryGetDispatchReason(out var pendingReason));
        Assert.Equal(MarkdownRebuildCoordinatorReason.SceneMaterialized, pendingReason);
    }

    [Fact]
    public void ResetDropsPendingAndActiveStateAndRejectsStaleCompletion()
    {
        var coordinator = new MarkdownRebuildCoordinator();
        coordinator.Request(MarkdownRebuildCoordinatorReason.Restyle);
        Assert.True(coordinator.TryBeginPipeline(51, out _));
        coordinator.Request(MarkdownRebuildCoordinatorReason.SceneMaterialized);

        coordinator.Reset();

        Assert.False(coordinator.HasPending);
        Assert.False(coordinator.HasPendingOrBuilding);
        Assert.False(coordinator.CompletePipeline(51));
        Assert.False(coordinator.TryBeginPipeline(52, out _));
        Assert.Equal(
            MarkdownRebuildRequestDisposition.Queue,
            coordinator.Request(MarkdownRebuildCoordinatorReason.SceneMaterialized));
        Assert.True(coordinator.TryBeginPipeline(52, out var newReason));
        Assert.Equal(MarkdownRebuildCoordinatorReason.SceneMaterialized, newReason);
    }

    [Fact]
    public void RejectedDispatchLeavesReasonPendingForLaterRetry()
    {
        var coordinator = new MarkdownRebuildCoordinator();
        coordinator.Request(MarkdownRebuildCoordinatorReason.SceneMaterialized);

        // A rejected DispatcherQueue enqueue does not consume coordinator
        // state; the next UI opportunity can ask for dispatch again.
        Assert.True(coordinator.TryGetDispatchReason(out var firstAttempt));
        Assert.Equal(MarkdownRebuildCoordinatorReason.SceneMaterialized, firstAttempt);
        Assert.True(coordinator.TryGetDispatchReason(out var retry));
        Assert.Equal(firstAttempt, retry);
        Assert.True(coordinator.TryBeginPipeline(61, out _));
    }

    [Fact]
    public async Task PipelineLifetimePreservesFaultedWorkerOutcome()
    {
        var lifetime = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failure = new InvalidOperationException("pipeline teardown failed");

        MarkdownRebuildCoordinator.CompletePipelineLifetime(Task.FromException(failure), lifetime);

        InvalidOperationException actual = await Assert.ThrowsAsync<InvalidOperationException>(
            () => lifetime.Task);
        Assert.Same(failure, actual);
    }

    [Fact]
    public async Task PipelineLifetimePreservesCanceledWorkerOutcome()
    {
        var lifetime = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        MarkdownRebuildCoordinator.CompletePipelineLifetime(
            Task.FromCanceled(cancellation.Token),
            lifetime);

        OperationCanceledException actual = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => lifetime.Task);
        Assert.Equal(cancellation.Token, actual.CancellationToken);
        Assert.True(lifetime.Task.IsCanceled);
    }

    [Fact]
    public async Task PipelineLifetimeCompletesSuccessfullyOnlyForSuccessfulWorker()
    {
        var lifetime = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        MarkdownRebuildCoordinator.CompletePipelineLifetime(Task.CompletedTask, lifetime);

        await lifetime.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(lifetime.Task.IsCompletedSuccessfully);
    }

    [Fact]
    public void UnfinishedWorkerCannotPublishSuccessfulRetirement()
    {
        var worker = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lifetime = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Assert.Throws<ArgumentException>(
            () => MarkdownRebuildCoordinator.CompletePipelineLifetime(worker.Task, lifetime));

        Assert.False(lifetime.Task.IsCompleted);
    }
}
