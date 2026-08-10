using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using JitHub.Models.GitHub;
using JitHub.Services;
using Xunit;

namespace JitHub.WinUI.Tests.Services;

public sealed class NotificationOpenWorkflowTests
{
    [Fact]
    public async Task UnreadNotification_ProjectsRead_StartsOneMutation_ThenNavigates()
    {
        NotificationInboxState state = CreateUnreadState();
        RecordingNotificationService service = new();
        NotificationOpenWorkflow workflow = new(state, service);
        List<string> events = [];

        service.OnMarkRead = () => events.Add("remote");
        await workflow.ExecuteAsync(
            "token",
            "42",
            Thread(unread: true),
            () => events.Add("navigate"),
            () => events.Add("project"),
            () => events.Add("failure"));

        Assert.Equal(["project", "remote", "navigate"], events);
        Assert.Equal(1, service.MarkReadCalls);
        Assert.True(state.TryGetThreadUnreadState("42", "thread-1", out bool unread));
        Assert.False(unread);
        Assert.False(state.HasActiveMutations);
    }

    [Fact]
    public async Task RepeatedOpen_UsesSharedOptimisticStateAndDoesNotDuplicateRemoteMutation()
    {
        NotificationInboxState state = CreateUnreadState();
        TaskCompletionSource remoteCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        RecordingNotificationService service = new() { MarkReadTask = remoteCompletion.Task };
        NotificationOpenWorkflow workflow = new(state, service);
        int navigations = 0;

        Task firstOpen = workflow.ExecuteAsync("token", "42", Thread(true), () => navigations++, () => { }, () => { });
        await workflow.ExecuteAsync("token", "42", Thread(true), () => navigations++, () => { }, () => { });

        Assert.Equal(1, service.MarkReadCalls);
        Assert.Equal(2, navigations);
        Assert.True(state.HasActiveMutations);

        remoteCompletion.SetResult();
        await firstOpen;
        Assert.False(state.HasActiveMutations);
    }

    [Fact]
    public async Task TrueRemoteFailure_RollsBackSharedStateAndStillNavigates()
    {
        NotificationInboxState state = CreateUnreadState();
        RecordingNotificationService service = new() { MarkReadFailure = new InvalidOperationException("remote failure") };
        NotificationOpenWorkflow workflow = new(state, service);
        int projections = 0;
        int failures = 0;
        int navigations = 0;

        await workflow.ExecuteAsync(
            "token",
            "42",
            Thread(true),
            () => navigations++,
            () => projections++,
            () => failures++);

        Assert.Equal(1, service.MarkReadCalls);
        Assert.Equal(2, projections);
        Assert.Equal(1, failures);
        Assert.Equal(1, navigations);
        Assert.True(state.TryGetThreadUnreadState("42", "thread-1", out bool unread));
        Assert.True(unread);
        Assert.False(state.HasActiveMutations);
    }

    [Fact]
    public async Task CanceledRemoteOutcome_DoesNotRollBackIdempotentRead()
    {
        NotificationInboxState state = CreateUnreadState();
        RecordingNotificationService service = new() { CancelMarkRead = true };
        NotificationOpenWorkflow workflow = new(state, service);
        int failures = 0;

        await workflow.ExecuteAsync("token", "42", Thread(true), () => { }, () => { }, () => failures++);

        Assert.Equal(1, service.MarkReadCalls);
        Assert.Equal(0, failures);
        Assert.True(state.TryGetThreadUnreadState("42", "thread-1", out bool unread));
        Assert.False(unread);
        Assert.False(state.HasActiveMutations);
    }

    private static NotificationInboxState CreateUnreadState()
    {
        NotificationInboxState state = new();
        state.ApplySnapshot(
            "42",
            [Thread(unread: true)],
            isPartial: false,
            DateTimeOffset.UtcNow,
            NotificationCountSource.AccountWideWorkspace);
        return state;
    }

    private static GitHubNotificationThread Thread(bool unread) => new()
    {
        Id = "thread-1",
        Unread = unread,
        Repository = new GitHubRepository { FullName = "owner/repo" },
        Subject = new GitHubNotificationSubject { Type = "Issue", Url = "https://api.github.com/repos/owner/repo/issues/1" }
    };

    private sealed class RecordingNotificationService : IGitHubNotificationQueryService
    {
        public int MarkReadCalls { get; private set; }

        public Action? OnMarkRead { get; set; }

        public Exception? MarkReadFailure { get; set; }

        public bool CancelMarkRead { get; set; }

        public Task? MarkReadTask { get; set; }

        public Task MarkThreadReadAsync(string accessToken, string userId, string threadId, CancellationToken cancellationToken = default)
        {
            MarkReadCalls++;
            OnMarkRead?.Invoke();
            if (CancelMarkRead)
            {
                return Task.FromCanceled(new CancellationToken(canceled: true));
            }

            return MarkReadTask ?? (MarkReadFailure is null ? Task.CompletedTask : Task.FromException(MarkReadFailure));
        }

        public Task<CachedResult<GitHubNotificationThread[]>> GetPageAsync(string accessToken, string userId, NotificationListFilter filter, int page, int pageSize, QueryFetchPolicy fetchPolicy = QueryFetchPolicy.StaleFirst, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<CachedResult<GitHubNotificationSubscription>> GetSubscriptionAsync(string accessToken, string userId, string threadId, QueryFetchPolicy fetchPolicy = QueryFetchPolicy.StaleFirst, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task MarkAllReadAsync(string accessToken, string userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<GitHubNotificationSubscription> SubscribeThreadAsync(string accessToken, string userId, string threadId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task UnsubscribeThreadAsync(string accessToken, string userId, string threadId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<GitHubNotificationSubscription> MuteThreadAsync(string accessToken, string userId, string threadId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<GitHubNotificationSubscription> UnmuteThreadAsync(string accessToken, string userId, string threadId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
