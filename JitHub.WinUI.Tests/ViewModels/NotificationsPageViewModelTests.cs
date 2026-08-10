using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using JitHub.Models.GitHub;
using JitHub.Services;
using JitHub.WinUI.Tests.TestDoubles;
using JitHub.WinUI.ViewModels.Pages;
using Xunit;

namespace JitHub.WinUI.Tests.ViewModels;

public sealed class NotificationsPageViewModelTests
{
    [Fact]
    public async Task LaterPageFailure_PreservesRowsAndReportsTruthfulPartialScope()
    {
        GitHubNotificationThread[] firstPage = Enumerable.Range(1, 50)
            .Select(index => Thread(index.ToString()))
            .ToArray();
        RecordingNotificationService service = new()
        {
            PageHandler = (_, page, _, _) => page == 1
                ? Task.FromResult(Result(firstPage))
                : Task.FromException<CachedResult<GitHubNotificationThread[]>>(
                    new HttpRequestException("page 2 unavailable"))
        };
        NotificationInboxState inbox = new();
        using NotificationsPageViewModel viewModel = CreateViewModel(service, inbox);

        await viewModel.InitializeAsync();
        await viewModel.LoadMoreCommand.ExecuteAsync(null);

        Assert.Equal(50, viewModel.Notifications.Count);
        Assert.Equal("50 notifications loaded (partial)", viewModel.ResultCountText);
        Assert.True(viewModel.IsErrorVisible);
        Assert.Contains("already loaded", viewModel.ErrorMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("saved", viewModel.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CompleteFirstPage_ReportsAuthoritativeLoadedScope()
    {
        RecordingNotificationService service = new()
        {
            PageHandler = (_, _, _, _) => Task.FromResult(Result([Thread("1"), Thread("2")]))
        };
        using NotificationsPageViewModel viewModel = CreateViewModel(service, new NotificationInboxState());

        await viewModel.InitializeAsync();

        Assert.Equal("2 notifications", viewModel.ResultCountText);
        Assert.False(viewModel.IsErrorVisible);
    }

    [Fact]
    public async Task ReachableLoad_EmitsOpenedAndTimedSuccessOrFailureOutcomes()
    {
        RecordingTelemetry successTelemetry = new();
        RecordingNotificationService successService = new()
        {
            PageHandler = (_, _, _, _) => Task.FromResult(Result([Thread("1")]))
        };
        using (NotificationsPageViewModel success = CreateViewModel(
                   successService,
                   new NotificationInboxState(),
                   successTelemetry))
        {
            await success.InitializeAsync();
        }

        Assert.Contains(successTelemetry.Events, static entry => entry.Name == "notifications.opened");
        RecordedNotificationTelemetry successLoad = Assert.Single(
            successTelemetry.Events,
            static entry => entry.Name == "notifications.list.loaded");
        Assert.Equal("success", successLoad.Properties["result"]);
        Assert.False(string.IsNullOrWhiteSpace(successLoad.Properties["duration_bucket"]));

        RecordingTelemetry failureTelemetry = new();
        RecordingNotificationService failureService = new()
        {
            PageHandler = (_, _, _, _) =>
                Task.FromException<CachedResult<GitHubNotificationThread[]>>(new HttpRequestException("offline"))
        };
        using (NotificationsPageViewModel failure = CreateViewModel(
                   failureService,
                   new NotificationInboxState(),
                   failureTelemetry))
        {
            await failure.InitializeAsync();
            Assert.True(failure.IsErrorVisible);
        }

        RecordedNotificationTelemetry failureLoad = Assert.Single(
            failureTelemetry.Events,
            static entry => entry.Name == "notifications.list.loaded");
        Assert.Equal("error", failureLoad.Properties["result"]);
        Assert.False(string.IsNullOrWhiteSpace(failureLoad.Properties["duration_bucket"]));
    }

    [Fact]
    public async Task CachedRefreshFailure_IsReportedAsCachedErrorInsteadOfSuccess()
    {
        RecordingTelemetry telemetry = new();
        RecordingNotificationService service = new()
        {
            PageHandler = (_, _, _, _) => Task.FromResult(new CachedResult<GitHubNotificationThread[]>(
                [Thread("cached")],
                CacheState.Stale,
                DateTimeOffset.UtcNow.AddMinutes(-10),
                DateTimeOffset.UtcNow.AddMinutes(-5),
                RefreshError: new HttpRequestException("offline")))
        };
        using NotificationsPageViewModel viewModel = CreateViewModel(
            service,
            new NotificationInboxState(),
            telemetry);

        await viewModel.InitializeAsync();

        RecordedNotificationTelemetry load = Assert.Single(
            telemetry.Events,
            static entry => entry.Name == "notifications.list.loaded");
        Assert.Equal(TelemetryTaxonomy.Results.CachedError, load.Properties["result"]);
        Assert.Equal("network", load.Properties["error_kind"]);
        Assert.Single(viewModel.Notifications);
    }

    [Fact]
    public async Task ThrowingTelemetry_DoesNotChangeNotificationLoadOutcome()
    {
        RecordingNotificationService service = new()
        {
            PageHandler = (_, _, _, _) => Task.FromResult(Result([Thread("1")]))
        };
        using NotificationsPageViewModel viewModel = CreateViewModel(
            service,
            new NotificationInboxState(),
            new ThrowingTelemetryService());

        await viewModel.InitializeAsync();

        Assert.Single(viewModel.Notifications);
        Assert.False(viewModel.IsErrorVisible);
    }

    [Fact]
    public async Task ParticipatingList_DoesNotReplaceAccountWideUnreadCount()
    {
        RecordingNotificationService service = new()
        {
            PageHandler = (filter, _, _, _) => Task.FromResult(Result(
                filter == NotificationListFilter.Unread
                    ? [Thread("1"), Thread("2"), Thread("3")]
                    : [Thread("participating")]))
        };
        NotificationInboxState inbox = new();
        using NotificationsPageViewModel viewModel = CreateViewModel(service, inbox);

        await viewModel.InitializeAsync();
        await viewModel.ChangeFilterAsync(NotificationListFilter.Participating);

        Assert.Single(viewModel.Notifications);
        Assert.Equal("participating", viewModel.Notifications[0].StableKey);
        Assert.Equal(3, inbox.UnreadCount);
        Assert.Equal("3", inbox.BadgeText);
    }

    [Fact]
    public async Task ParticipatingPoll_RefreshesBadgeFromSeparateAccountWideUnreadQuery()
    {
        bool polling = false;
        RecordingNotificationService service = new()
        {
            PageHandler = (filter, _, _, _) => Task.FromResult(Result(
                filter == NotificationListFilter.Unread
                    ? polling
                        ? [Thread("1"), Thread("2"), Thread("3"), Thread("4")]
                        : [Thread("1"), Thread("2")]
                    : [Thread("participating")]))
        };
        NotificationInboxState inbox = new();
        using NotificationsPageViewModel viewModel = CreateViewModel(service, inbox);
        await viewModel.InitializeAsync();
        await viewModel.ChangeFilterAsync(NotificationListFilter.Participating);

        polling = true;
        await viewModel.SynchronizeVisibleFirstPageAsync();

        Assert.Single(viewModel.Notifications);
        Assert.Equal("participating", viewModel.Notifications[0].StableKey);
        Assert.Equal(4, inbox.UnreadCount);
    }

    [Fact]
    public async Task PollStartedBeforeReadMutation_CannotRestoreOptimisticUnreadState()
    {
        TaskCompletionSource<CachedResult<GitHubNotificationThread[]>> poll =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource mutation = new(TaskCreationOptions.RunContinuationsAsynchronously);
        bool initialized = false;
        RecordingNotificationService service = new()
        {
            PageHandler = (_, _, policy, _) =>
            {
                if (!initialized)
                {
                    initialized = true;
                    return Task.FromResult(Result([Thread("1"), Thread("2")]));
                }

                Assert.Equal(QueryFetchPolicy.NetworkOnly, policy);
                return poll.Task;
            },
            MarkReadHandler = _ => mutation.Task
        };
        NotificationInboxState inbox = new();
        using NotificationsPageViewModel viewModel = CreateViewModel(service, inbox);
        await viewModel.InitializeAsync();

        Task polling = viewModel.SynchronizeVisibleFirstPageAsync();
        NotificationViewItem item = viewModel.Notifications.Single(entry => entry.StableKey == "1");
        Task markingRead = viewModel.MarkReadCommand.ExecuteAsync(item);

        Assert.False(item.IsUnread);
        Assert.Equal(1, inbox.UnreadCount);
        poll.SetResult(Result([Thread("1"), Thread("2")]));
        await polling;

        Assert.False(item.IsUnread);
        Assert.Equal(1, inbox.UnreadCount);

        mutation.SetResult();
        await markingRead;

        Assert.False(item.IsUnread);
        Assert.Equal(1, inbox.UnreadCount);
        Assert.False(inbox.HasActiveMutations);
    }

    [Fact]
    public async Task FilterChangeDuringReadMutation_RetriesAfterMutationSettles()
    {
        TaskCompletionSource mutation = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource firstParticipatingRequest = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int participatingRequests = 0;
        RecordingNotificationService service = new()
        {
            PageHandler = (filter, _, _, _) =>
            {
                if (filter == NotificationListFilter.Unread)
                {
                    return Task.FromResult(Result([Thread("1"), Thread("2")]));
                }

                int request = Interlocked.Increment(ref participatingRequests);
                firstParticipatingRequest.TrySetResult();
                return Task.FromResult(Result(
                    [Thread(request == 1 ? "stale-participating" : "current-participating")]));
            },
            MarkReadHandler = _ => mutation.Task
        };
        NotificationInboxState inbox = new();
        using NotificationsPageViewModel viewModel = CreateViewModel(service, inbox);
        await viewModel.InitializeAsync();

        NotificationViewItem item = viewModel.Notifications.Single(entry => entry.StableKey == "1");
        Task markingRead = viewModel.MarkReadCommand.ExecuteAsync(item);
        Task changingFilter = viewModel.ChangeFilterAsync(NotificationListFilter.Participating);
        await firstParticipatingRequest.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(changingFilter.IsCompleted);
        Assert.DoesNotContain(viewModel.Notifications, static entry => entry.StableKey == "stale-participating");

        mutation.SetResult();
        await Task.WhenAll(markingRead, changingFilter).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(2, participatingRequests);
        Assert.Equal(NotificationListFilter.Participating, viewModel.SelectedFilter);
        Assert.Single(viewModel.Notifications);
        Assert.Equal("current-participating", viewModel.Notifications[0].StableKey);
        Assert.False(inbox.HasActiveMutations);
    }

    [Fact]
    public async Task FilterChangeDuringFailedMuteMutation_RetriesAfterRollback()
    {
        TaskCompletionSource<GitHubNotificationSubscription> mutation =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource firstParticipatingRequest = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int participatingRequests = 0;
        RecordingNotificationService service = new()
        {
            PageHandler = (filter, _, _, _) =>
            {
                if (filter == NotificationListFilter.Unread)
                {
                    return Task.FromResult(Result([Thread("1")]));
                }

                int request = Interlocked.Increment(ref participatingRequests);
                firstParticipatingRequest.TrySetResult();
                return Task.FromResult(Result(
                    [Thread(request == 1 ? "stale-participating" : "current-participating")]));
            },
            Subscription = new GitHubNotificationSubscription { Subscribed = true, Ignored = false },
            MuteHandler = _ => mutation.Task
        };
        NotificationInboxState inbox = new();
        using NotificationsPageViewModel viewModel = CreateViewModel(service, inbox);
        await viewModel.InitializeAsync();
        NotificationViewItem item = viewModel.Notifications[0];
        await viewModel.EnsureSubscriptionStateAsync(item);

        Task muting = viewModel.ToggleMuteCommand.ExecuteAsync(item);
        Task changingFilter = viewModel.ChangeFilterAsync(NotificationListFilter.Participating);
        await firstParticipatingRequest.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(changingFilter.IsCompleted);
        mutation.SetException(new InvalidOperationException("offline"));
        await Task.WhenAll(muting, changingFilter).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(2, participatingRequests);
        Assert.Equal(NotificationListFilter.Participating, viewModel.SelectedFilter);
        Assert.Single(viewModel.Notifications);
        Assert.Equal("current-participating", viewModel.Notifications[0].StableKey);
        Assert.True(viewModel.IsErrorVisible);
        Assert.False(inbox.HasActiveMutations);
    }

    [Fact]
    public async Task FailedReadMutation_RestoresRowAndAccountWideCount()
    {
        RecordingNotificationService service = new()
        {
            PageHandler = (_, _, _, _) => Task.FromResult(Result([Thread("1"), Thread("2")])),
            MarkReadHandler = _ => Task.FromException(new InvalidOperationException("offline"))
        };
        NotificationInboxState inbox = new();
        using NotificationsPageViewModel viewModel = CreateViewModel(service, inbox);
        await viewModel.InitializeAsync();
        NotificationViewItem item = viewModel.Notifications[0];

        await viewModel.MarkReadCommand.ExecuteAsync(item);

        Assert.True(item.IsUnread);
        Assert.Equal(2, inbox.UnreadCount);
        Assert.True(viewModel.IsErrorVisible);
        Assert.False(inbox.HasActiveMutations);
    }

    [Fact]
    public async Task ReadNotification_DoesNotExposeOrInvokeUnsupportedUnreadMutation()
    {
        RecordingNotificationService service = new()
        {
            PageHandler = (_, _, _, _) => Task.FromResult(Result([Thread("1", unread: false)]))
        };
        NotificationInboxState inbox = new();
        using NotificationsPageViewModel viewModel = CreateViewModel(service, inbox);
        await viewModel.InitializeAsync();
        await viewModel.ChangeFilterAsync(NotificationListFilter.All);
        NotificationViewItem item = viewModel.Notifications.Single();

        await viewModel.MarkReadCommand.ExecuteAsync(item);

        Assert.False(item.IsUnread);
        Assert.Equal("Mark as read", item.ReadActionLabel);
        Assert.Equal(0, service.MarkReadCalls);
    }

    [Fact]
    public async Task MarkAllRead_BlocksStalePollAndClearsRowsAfterSuccess()
    {
        TaskCompletionSource<CachedResult<GitHubNotificationThread[]>> poll =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource mutation = new(TaskCreationOptions.RunContinuationsAsynchronously);
        bool initialized = false;
        RecordingNotificationService service = new()
        {
            PageHandler = (_, _, _, _) =>
            {
                if (!initialized)
                {
                    initialized = true;
                    return Task.FromResult(Result([Thread("1"), Thread("2")]));
                }

                return poll.Task;
            },
            MarkAllReadHandler = _ => mutation.Task
        };
        NotificationInboxState inbox = new();
        using NotificationsPageViewModel viewModel = CreateViewModel(service, inbox);
        await viewModel.InitializeAsync();

        Task polling = viewModel.SynchronizeVisibleFirstPageAsync();
        Task markingAll = viewModel.MarkAllReadCommand.ExecuteAsync(null);
        Assert.Equal(0, inbox.UnreadCount);
        Assert.All(viewModel.Notifications, static item => Assert.False(item.IsUnread));

        poll.SetResult(Result([Thread("1"), Thread("2")]));
        await polling;
        Assert.All(viewModel.Notifications, static item => Assert.False(item.IsUnread));

        mutation.SetResult();
        await markingAll;

        Assert.Empty(viewModel.Notifications);
        Assert.Equal(0, inbox.UnreadCount);
        Assert.False(inbox.HasActiveMutations);
    }

    [Fact]
    public async Task FollowUnsubscribeMuteAndUnmute_ExposeTruthfulReturnedState()
    {
        RecordingNotificationService service = new()
        {
            PageHandler = (_, _, _, _) => Task.FromResult(Result([Thread("1")])),
            Subscription = new GitHubNotificationSubscription { Subscribed = false, Ignored = false }
        };
        NotificationInboxState inbox = new();
        RecordingTelemetry telemetry = new();
        using NotificationsPageViewModel viewModel = CreateViewModel(service, inbox, telemetry);
        await viewModel.InitializeAsync();
        NotificationViewItem item = viewModel.Notifications[0];
        await viewModel.EnsureSubscriptionStateAsync(item);

        Assert.Equal("Follow thread", item.SubscriptionActionLabel);
        await viewModel.ToggleSubscriptionCommand.ExecuteAsync(item);
        Assert.True(item.IsSubscribed);
        Assert.False(item.IsMuted);
        Assert.Equal("Unsubscribe from thread", item.SubscriptionActionLabel);

        await viewModel.ToggleSubscriptionCommand.ExecuteAsync(item);
        Assert.False(item.IsSubscribed);
        Assert.False(item.IsMuted);

        await viewModel.ToggleMuteCommand.ExecuteAsync(item);
        Assert.True(item.IsMuted);
        Assert.False(item.IsSubscribed);
        Assert.Equal("Unmute thread", item.MuteActionLabel);

        await viewModel.ToggleMuteCommand.ExecuteAsync(item);
        Assert.False(item.IsMuted);
        Assert.True(item.IsSubscribed);

        Assert.Equal(1, service.SubscribeCalls);
        Assert.Equal(1, service.UnsubscribeCalls);
        Assert.Equal(1, service.MuteCalls);
        Assert.Equal(1, service.UnmuteCalls);
        Assert.Contains(telemetry.Actions, static action => action == "follow");
        Assert.Contains(telemetry.Actions, static action => action == "unsubscribe");
        Assert.Contains(telemetry.Actions, static action => action == "mute");
        Assert.Contains(telemetry.Actions, static action => action == "unmute");
    }

    [Fact]
    public async Task FailedMuteMutation_RollsBackTruthfulStateAndReleasesPollBarrier()
    {
        RecordingNotificationService service = new()
        {
            PageHandler = (_, _, _, _) => Task.FromResult(Result([Thread("1")])),
            Subscription = new GitHubNotificationSubscription { Subscribed = true, Ignored = false },
            MuteHandler = _ => Task.FromException<GitHubNotificationSubscription>(new InvalidOperationException("offline"))
        };
        NotificationInboxState inbox = new();
        using NotificationsPageViewModel viewModel = CreateViewModel(service, inbox);
        await viewModel.InitializeAsync();
        NotificationViewItem item = viewModel.Notifications[0];
        await viewModel.EnsureSubscriptionStateAsync(item);

        await viewModel.ToggleMuteCommand.ExecuteAsync(item);

        Assert.True(item.IsSubscribed);
        Assert.False(item.IsMuted);
        Assert.True(item.IsSubscriptionStateKnown);
        Assert.False(inbox.HasActiveMutations);
        Assert.True(viewModel.IsErrorVisible);
    }

    [Fact]
    public async Task Polling_IsTrackedAndDrainsDuringApplicationShutdown()
    {
        RecordingNotificationService service = new()
        {
            PageHandler = (_, _, _, _) => Task.FromResult(Result([Thread("1")]))
        };
        NotificationInboxState inbox = new();
        using ApplicationTaskCoordinator coordinator = new();
        using NotificationsPageViewModel viewModel = CreateViewModel(
            service,
            inbox,
            taskCoordinator: coordinator);

        await viewModel.InitializeAsync();

        Assert.Equal(1, coordinator.ActiveTaskCount);
        viewModel.Dispose();
        ApplicationTaskShutdownResult shutdown = await coordinator.ShutdownAsync(TimeSpan.FromSeconds(2));

        Assert.True(shutdown.Completed);
        Assert.Equal(0, shutdown.PendingTaskCount);
        Assert.Equal(0, coordinator.ActiveTaskCount);
    }

    [Fact]
    public async Task AutomaticMarkRead_UsesCoordinatorAccountCancellation()
    {
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        bool cancellationObserved = false;
        RecordingNotificationService service = new()
        {
            PageHandler = (_, _, _, _) => Task.FromResult(Result([Thread("1")])),
            MarkReadHandler = async cancellationToken =>
            {
                started.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    cancellationObserved = true;
                    throw;
                }
            }
        };
        NotificationInboxState inbox = new();
        using ApplicationTaskCoordinator coordinator = new();
        using NotificationsPageViewModel viewModel = CreateViewModel(
            service,
            inbox,
            taskCoordinator: coordinator);
        await viewModel.InitializeAsync();

        viewModel.OpenNotificationCommand.Execute(viewModel.Notifications[0]);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await coordinator.CancelAccountAsync("42").WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(cancellationObserved);
        Assert.Equal(1, service.MarkReadCalls);
        Assert.Equal(0, coordinator.ActiveTaskCount);
    }

    [Fact]
    public async Task PrefetchDestination_ForwardsHoveredNotification()
    {
        GitHubNotificationThread? prefetched = null;
        using NotificationsPageViewModel viewModel = CreateViewModel(
            new RecordingNotificationService(),
            new NotificationInboxState(),
            prefetchNotification: (thread, _) =>
            {
                prefetched = thread;
                return Task.CompletedTask;
            });
        NotificationViewItem item = NotificationViewItem.Create(Thread("prefetch"));

        await viewModel.PrefetchDestinationAsync(item);

        Assert.Same(item.Thread, prefetched);
    }

    [Fact]
    public async Task PrefetchDestination_FailureDoesNotAlterInboxState()
    {
        using NotificationsPageViewModel viewModel = CreateViewModel(
            new RecordingNotificationService(),
            new NotificationInboxState(),
            prefetchNotification: static (_, _) => Task.FromException(new InvalidOperationException("prefetch failed")));
        NotificationViewItem item = NotificationViewItem.Create(Thread("failure"));

        await viewModel.PrefetchDestinationAsync(item);

        Assert.Equal(string.Empty, viewModel.ErrorMessage);
        Assert.False(item.IsBusy);
    }

    [Fact]
    public async Task PrefetchDestination_CancelledBeforeDwellDoesNotStartWork()
    {
        int calls = 0;
        using NotificationsPageViewModel viewModel = CreateViewModel(
            new RecordingNotificationService(),
            new NotificationInboxState(),
            prefetchNotification: (_, _) =>
            {
                Interlocked.Increment(ref calls);
                return Task.CompletedTask;
            });
        NotificationViewItem item = NotificationViewItem.Create(Thread("cancel"));

        Task prediction = viewModel.PrefetchDestinationAsync(item);
        viewModel.CancelDestinationPrefetch();
        await prediction;

        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task PromoteDestinationPrefetch_StartsImmediatelyAndSurvivesPageDisposal()
    {
        int calls = 0;
        CancellationToken observedToken = new(canceled: true);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        NotificationsPageViewModel viewModel = CreateViewModel(
            new RecordingNotificationService(),
            new NotificationInboxState(),
            prefetchNotification: async (_, token) =>
            {
                observedToken = token;
                Interlocked.Increment(ref calls);
                await release.Task;
            });
        NotificationViewItem item = NotificationViewItem.Create(Thread("promote"));

        Task hover = viewModel.PrefetchDestinationAsync(item);
        Task promoted = viewModel.PromoteDestinationPrefetchAsync(item);
        viewModel.Dispose();
        release.SetResult();
        await Task.WhenAll(hover, promoted);

        Assert.Equal(1, calls);
        Assert.False(observedToken.CanBeCanceled);
    }

    [Fact]
    public async Task PromoteDestinationPrefetch_JoinsPredictionThatAlreadyPassedIntentThreshold()
    {
        int calls = 0;
        CancellationToken observedToken = new(canceled: true);
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using NotificationsPageViewModel viewModel = CreateViewModel(
            new RecordingNotificationService(),
            new NotificationInboxState(),
            prefetchNotification: async (_, token) =>
            {
                observedToken = token;
                Interlocked.Increment(ref calls);
                started.SetResult();
                await release.Task;
            });
        NotificationViewItem item = NotificationViewItem.Create(Thread("join-started"));

        Task hover = viewModel.PrefetchDestinationAsync(item);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Task promoted = viewModel.PromoteDestinationPrefetchAsync(item);
        release.SetResult();
        await Task.WhenAll(hover, promoted);

        Assert.Equal(1, calls);
        Assert.False(observedToken.CanBeCanceled);
    }

    private static NotificationsPageViewModel CreateViewModel(
        IGitHubNotificationQueryService service,
        NotificationInboxState inbox,
        ITelemetryService? telemetry = null,
        IApplicationTaskCoordinator? taskCoordinator = null,
        Func<GitHubNotificationThread, CancellationToken, Task>? prefetchNotification = null) =>
        new(
            service,
            new TestAuthService(),
            new TestAccountService(),
            telemetry ?? new RecordingTelemetry(),
            inbox,
            static (_, _) => { },
            taskCoordinator,
            prefetchNotification);

    private static GitHubNotificationThread Thread(string id, bool unread = true) => new()
    {
        Id = id,
        Unread = unread,
        Subject = new GitHubNotificationSubject { Title = $"Notification {id}", Type = "Issue" },
        Repository = new GitHubRepository { FullName = "owner/repository" },
        UpdatedAt = DateTimeOffset.UtcNow
    };

    private static CachedResult<GitHubNotificationThread[]> Result(GitHubNotificationThread[] threads) =>
        new(
            threads,
            CacheState.Fresh,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddMinutes(5));

    private sealed class RecordingNotificationService : IGitHubNotificationQueryService
    {
        public Func<NotificationListFilter, int, QueryFetchPolicy, CancellationToken, Task<CachedResult<GitHubNotificationThread[]>>> PageHandler { get; set; } =
            static (_, _, _, _) => Task.FromResult(Result([]));

        public Func<CancellationToken, Task> MarkReadHandler { get; set; } = static _ => Task.CompletedTask;

        public Func<CancellationToken, Task> MarkAllReadHandler { get; set; } = static _ => Task.CompletedTask;

        public Func<CancellationToken, Task<GitHubNotificationSubscription>> MuteHandler { get; set; } =
            static _ => Task.FromResult(new GitHubNotificationSubscription { Subscribed = false, Ignored = true });

        public GitHubNotificationSubscription Subscription { get; set; } = new() { Subscribed = true };

        public int SubscribeCalls { get; private set; }

        public int UnsubscribeCalls { get; private set; }

        public int MuteCalls { get; private set; }

        public int UnmuteCalls { get; private set; }

        public int MarkReadCalls { get; private set; }

        public Task<CachedResult<GitHubNotificationThread[]>> GetPageAsync(
            string accessToken,
            string userId,
            NotificationListFilter filter,
            int page,
            int pageSize,
            QueryFetchPolicy fetchPolicy = QueryFetchPolicy.StaleFirst,
            CancellationToken cancellationToken = default) =>
            PageHandler(filter, page, fetchPolicy, cancellationToken);

        public Task<CachedResult<GitHubNotificationSubscription>> GetSubscriptionAsync(
            string accessToken,
            string userId,
            string threadId,
            QueryFetchPolicy fetchPolicy = QueryFetchPolicy.StaleFirst,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new CachedResult<GitHubNotificationSubscription>(
                Subscription,
                CacheState.Fresh,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow.AddMinutes(5)));

        public Task MarkAllReadAsync(string accessToken, string userId, CancellationToken cancellationToken = default) =>
            MarkAllReadHandler(cancellationToken);

        public Task MarkThreadReadAsync(string accessToken, string userId, string threadId, CancellationToken cancellationToken = default)
        {
            MarkReadCalls++;
            return MarkReadHandler(cancellationToken);
        }

        public Task<GitHubNotificationSubscription> SubscribeThreadAsync(
            string accessToken,
            string userId,
            string threadId,
            CancellationToken cancellationToken = default)
        {
            SubscribeCalls++;
            return Task.FromResult(new GitHubNotificationSubscription { Subscribed = true, Ignored = false });
        }

        public Task UnsubscribeThreadAsync(
            string accessToken,
            string userId,
            string threadId,
            CancellationToken cancellationToken = default)
        {
            UnsubscribeCalls++;
            return Task.CompletedTask;
        }

        public Task<GitHubNotificationSubscription> MuteThreadAsync(
            string accessToken,
            string userId,
            string threadId,
            CancellationToken cancellationToken = default)
        {
            MuteCalls++;
            return MuteHandler(cancellationToken);
        }

        public Task<GitHubNotificationSubscription> UnmuteThreadAsync(
            string accessToken,
            string userId,
            string threadId,
            CancellationToken cancellationToken = default)
        {
            UnmuteCalls++;
            return Task.FromResult(new GitHubNotificationSubscription { Subscribed = true, Ignored = false });
        }
    }

    private sealed class TestAccountService : IAccountService
    {
        public void RemoveUser() { }

        public void SaveUser(long userId) { }

        public long GetUser() => 42;
    }

    private sealed class TestAuthService : IAuthService
    {
        public bool Authenticated { get; set; } = true;

        public GitHubUser? AuthenticatedUser { get; set; } = new() { Id = 42, Login = "viewer" };

        public AuthSessionRecoveryState RecoveryState => AuthSessionRecoveryState.None;

        public Task InitializeAsync() => Task.CompletedTask;

        public Task Authenticate() => Task.CompletedTask;

        public Task<bool> EnsureScopesAsync(params string[] scopes) => Task.FromResult(true);

        public Task<bool> Authorize(string response) => Task.FromResult(true);

        public Task<GitHubUser?> RefreshAuthenticatedUserAsync() => Task.FromResult(AuthenticatedUser);

        public string? GetToken(long userId) => "token";

        public bool CheckAuth(long userId) => true;

        public void SignOut() { }
    }

    private sealed class RecordingTelemetry : ITelemetryService
    {
        public List<RecordedNotificationTelemetry> Events { get; } = [];

        public List<string> Actions { get; } = [];

        public void TrackEvent(string name, IReadOnlyDictionary<string, string?>? properties = null)
        {
            Events.Add(new RecordedNotificationTelemetry(
                name,
                new Dictionary<string, string?>(properties ?? new Dictionary<string, string?>(), StringComparer.Ordinal)));
            if (string.Equals(name, "notifications.action.executed", StringComparison.Ordinal) &&
                properties?.TryGetValue("action", out string? action) == true &&
                !string.IsNullOrWhiteSpace(action))
            {
                Actions.Add(action);
            }
        }

        public void TrackMetric(string name, double value, IReadOnlyDictionary<string, string?>? properties = null) { }

        public IPerformanceTrace StartTrace(string name, IReadOnlyDictionary<string, string?>? properties = null) =>
            new NoopTrace();
    }

    private sealed record RecordedNotificationTelemetry(
        string Name,
        IReadOnlyDictionary<string, string?> Properties);

    private sealed class NoopTrace : IPerformanceTrace
    {
        public void Dispose() { }

        public void SetProperty(string key, string? value) { }
    }
}
