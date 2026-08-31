using System;
using JitHub.Models.GitHub;
using JitHub.Services;
using Xunit;

namespace JitHub.WinUI.Tests.Services;

public sealed class NotificationInboxStateTests
{
    [Fact]
    public void HomePreview_ReportsPartialCount()
    {
        NotificationInboxState state = new();
        GitHubNotificationThread[] threads = CreateThreads(10, unread: true);

        state.ApplySnapshot("1", threads, true, DateTimeOffset.UtcNow, NotificationCountSource.HomePreview);

        Assert.Equal(10, state.UnreadCount);
        Assert.True(state.IsPartial);
        Assert.Equal("10+", state.BadgeText);
    }

    [Fact]
    public void WorkspaceSnapshot_ReplacesPreviewWithExactCount()
    {
        NotificationInboxState state = new();
        DateTimeOffset timestamp = DateTimeOffset.UtcNow;
        state.ApplySnapshot("1", CreateThreads(10, true), true, timestamp, NotificationCountSource.HomePreview);

        state.ApplySnapshot("1", CreateThreads(4, true), false, timestamp.AddSeconds(1), NotificationCountSource.AccountWideWorkspace);

        Assert.Equal(4, state.UnreadCount);
        Assert.False(state.IsPartial);
        Assert.Equal("4", state.BadgeText);
    }

    [Fact]
    public void NewerPartialHomePreview_CannotDowngradeExactWorkspaceCount()
    {
        NotificationInboxState state = new();
        DateTimeOffset timestamp = DateTimeOffset.UtcNow;
        state.ApplySnapshot("1", CreateThreads(37, true), false, timestamp, NotificationCountSource.AccountWideWorkspace);

        bool applied = state.ApplySnapshot(
            "1",
            CreateThreads(10, true),
            true,
            timestamp.AddMinutes(5),
            NotificationCountSource.HomePreview);

        Assert.False(applied);
        Assert.Equal(37, state.UnreadCount);
        Assert.False(state.IsPartial);
        Assert.Equal("37", state.BadgeText);
    }

    [Fact]
    public void ExactWorkspaceCount_UpgradesNewerPartialHomePreviewRegardlessOfOrdering()
    {
        NotificationInboxState state = new();
        DateTimeOffset timestamp = DateTimeOffset.UtcNow;
        state.ApplySnapshot("1", CreateThreads(10, true), true, timestamp.AddMinutes(5), NotificationCountSource.HomePreview);

        bool applied = state.ApplySnapshot(
            "1",
            CreateThreads(37, true),
            false,
            timestamp,
            NotificationCountSource.AccountWideWorkspace);

        Assert.True(applied);
        Assert.Equal(37, state.UnreadCount);
        Assert.False(state.IsPartial);
    }

    [Fact]
    public void CurrentGenerationPartialHomePreview_CannotUndoExactCountAfterReadMutation()
    {
        NotificationInboxState state = new();
        DateTimeOffset timestamp = DateTimeOffset.UtcNow;
        state.ApplySnapshot("1", CreateThreads(37, true), false, timestamp, NotificationCountSource.AccountWideWorkspace);
        NotificationMutationLease lease = state.BeginReadStateMutation("1", "0", wasUnread: true, isUnread: false);
        state.CompleteMutation(lease);
        long currentGeneration = state.CaptureMutationGeneration("1");

        bool applied = state.ApplySnapshot(
            "1",
            CreateThreads(10, true),
            true,
            timestamp.AddMinutes(5),
            NotificationCountSource.HomePreview,
            currentGeneration);

        Assert.False(applied);
        Assert.Equal(36, state.UnreadCount);
        Assert.False(state.IsPartial);
    }

    [Fact]
    public void FailedMutation_RestoresExactAuthorityBeforeNewerHomePreviewArrives()
    {
        NotificationInboxState state = new();
        DateTimeOffset timestamp = DateTimeOffset.UtcNow;
        state.ApplySnapshot("1", CreateThreads(37, true), false, timestamp, NotificationCountSource.AccountWideWorkspace);
        NotificationMutationLease lease = state.BeginMarkAllReadMutation("1");

        bool appliedWhileActive = state.ApplySnapshot(
            "1",
            CreateThreads(10, true),
            true,
            timestamp.AddMinutes(1),
            NotificationCountSource.HomePreview,
            state.CaptureMutationGeneration("1"));
        state.RollbackMutation(lease);
        bool appliedAfterRollback = state.ApplySnapshot(
            "1",
            CreateThreads(10, true),
            true,
            timestamp.AddMinutes(2),
            NotificationCountSource.HomePreview,
            state.CaptureMutationGeneration("1"));

        Assert.False(appliedWhileActive);
        Assert.False(appliedAfterRollback);
        Assert.Equal(37, state.UnreadCount);
        Assert.False(state.IsPartial);
    }

    [Fact]
    public void StaleSnapshot_DoesNotUndoReadMutation()
    {
        NotificationInboxState state = new();
        DateTimeOffset fetchedAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        state.ApplySnapshot("1", CreateThreads(3, true), false, fetchedAt, NotificationCountSource.AccountWideWorkspace);
        state.MarkAllRead("1");

        state.ApplySnapshot("1", CreateThreads(3, true), false, fetchedAt, NotificationCountSource.HomePreview);

        Assert.Equal(0, state.UnreadCount);
        Assert.Equal(string.Empty, state.BadgeText);
    }

    [Fact]
    public void RequestStartedBeforeMutation_CannotRestoreUnreadCountEvenWithNewerFetchedAt()
    {
        NotificationInboxState state = new();
        state.ApplySnapshot("1", CreateThreads(3, true), false, DateTimeOffset.UtcNow, NotificationCountSource.AccountWideWorkspace);
        long requestGeneration = state.CaptureMutationGeneration("1");

        state.MarkAllRead("1");

        bool applied = state.ApplySnapshot(
            "1",
            CreateThreads(3, true),
            false,
            DateTimeOffset.UtcNow.AddMinutes(1),
            NotificationCountSource.AccountWideWorkspace,
            requestGeneration);

        Assert.False(applied);
        Assert.Equal(0, state.UnreadCount);
    }

    [Fact]
    public void ChangingAccount_ResetsCount()
    {
        NotificationInboxState state = new();
        state.ApplySnapshot("1", CreateThreads(2, true), false, DateTimeOffset.UtcNow, NotificationCountSource.AccountWideWorkspace);

        state.ApplySnapshot("2", Array.Empty<GitHubNotificationThread>(), false, DateTimeOffset.UtcNow, NotificationCountSource.AccountWideWorkspace);

        Assert.Equal(0, state.UnreadCount);
    }

    [Fact]
    public void ReadMutation_AdvancesBeforeNetworkAndRejectsSnapshotsWhileActive()
    {
        NotificationInboxState state = new();
        state.ApplySnapshot("1", CreateThreads(3, true), false, DateTimeOffset.UtcNow, NotificationCountSource.AccountWideWorkspace);

        NotificationMutationLease lease = state.BeginReadStateMutation("1", "thread-1", wasUnread: true, isUnread: false);
        long generationAfterBegin = state.CaptureMutationGeneration("1");
        bool applied = state.ApplySnapshot(
            "1",
            CreateThreads(3, true),
            false,
            DateTimeOffset.UtcNow.AddMinutes(1),
            NotificationCountSource.AccountWideWorkspace,
            generationAfterBegin);

        Assert.False(applied);
        Assert.True(state.HasActiveMutations);
        Assert.Equal(2, state.UnreadCount);

        Assert.True(state.CompleteMutation(lease));
        Assert.False(state.HasActiveMutations);
        Assert.Equal(2, state.UnreadCount);
    }

    [Fact]
    public void FailedReadMutation_RollsBackOnlyItsOptimisticDelta()
    {
        NotificationInboxState state = new();
        state.ApplySnapshot("1", CreateThreads(4, true), false, DateTimeOffset.UtcNow, NotificationCountSource.AccountWideWorkspace);
        NotificationMutationLease first = state.BeginReadStateMutation("1", "thread-1", wasUnread: true, isUnread: false);
        NotificationMutationLease second = state.BeginReadStateMutation("1", "thread-2", wasUnread: true, isUnread: false);

        state.CompleteMutation(first);
        state.RollbackMutation(second);

        Assert.Equal(3, state.UnreadCount);
        Assert.False(state.HasActiveMutations);
    }

    [Fact]
    public void FailedMarkAllMutation_RestoresPriorAccountWideCount()
    {
        NotificationInboxState state = new();
        state.ApplySnapshot("1", CreateThreads(7, true), true, DateTimeOffset.UtcNow, NotificationCountSource.AccountWideWorkspace);

        NotificationMutationLease lease = state.BeginMarkAllReadMutation("1");
        Assert.Equal(0, state.UnreadCount);
        Assert.False(state.IsPartial);

        state.RollbackMutation(lease);

        Assert.Equal(7, state.UnreadCount);
        Assert.True(state.IsPartial);
        Assert.Equal("7+", state.BadgeText);
    }

    [Fact]
    public void SubscriptionMutation_IsAGenerationBarrierWithoutChangingCount()
    {
        NotificationInboxState state = new();
        state.ApplySnapshot("1", CreateThreads(2, true), false, DateTimeOffset.UtcNow, NotificationCountSource.AccountWideWorkspace);

        NotificationMutationLease lease = state.BeginSubscriptionMutation("1");
        long generation = state.CaptureMutationGeneration("1");

        Assert.False(state.IsCurrentMutationGeneration("1", generation));
        Assert.Equal(2, state.UnreadCount);

        state.CompleteMutation(lease);

        Assert.False(state.HasActiveMutations);
        Assert.Equal(2, state.UnreadCount);
    }

    [Fact]
    public void ThreadReadMutation_ProjectsImmediatelyAndRollbackRestoresSharedState()
    {
        NotificationInboxState state = new();
        state.ApplySnapshot("1", [new GitHubNotificationThread { Id = "thread-1", Unread = true }], false, DateTimeOffset.UtcNow, NotificationCountSource.HomePreview);
        int initialVersion = state.ReadStateVersion;

        NotificationMutationLease lease = state.BeginReadStateMutation("1", "thread-1", wasUnread: true, isUnread: false);

        Assert.True(state.TryGetThreadUnreadState("1", "thread-1", out bool optimisticUnread));
        Assert.False(optimisticUnread);
        Assert.True(state.ReadStateVersion > initialVersion);

        state.RollbackMutation(lease);

        Assert.True(state.TryGetThreadUnreadState("1", "thread-1", out bool restoredUnread));
        Assert.True(restoredUnread);
    }

    [Fact]
    public void MarkAllRead_ProjectsToEveryObservedThreadAndRollbackRestoresThem()
    {
        NotificationInboxState state = new();
        state.ApplySnapshot("1", CreateThreads(3, unread: true), false, DateTimeOffset.UtcNow, NotificationCountSource.HomePreview);

        NotificationMutationLease lease = state.BeginMarkAllReadMutation("1");

        Assert.All(CreateThreads(3, true), thread =>
        {
            Assert.True(state.TryGetThreadUnreadState("1", thread.Id, out bool unread));
            Assert.False(unread);
        });

        state.RollbackMutation(lease);

        Assert.All(CreateThreads(3, true), thread =>
        {
            Assert.True(state.TryGetThreadUnreadState("1", thread.Id, out bool unread));
            Assert.True(unread);
        });
    }

    private static GitHubNotificationThread[] CreateThreads(int count, bool unread)
    {
        GitHubNotificationThread[] threads = new GitHubNotificationThread[count];
        for (int index = 0; index < count; index++)
        {
            threads[index] = new GitHubNotificationThread { Id = index.ToString(), Unread = unread };
        }

        return threads;
    }
}
