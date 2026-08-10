using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JitHub.Models.GitHub;
using JitHub.Services;
using JitHub.WinUI.ViewModels.Common;

namespace JitHub.WinUI.ViewModels.Pages;

[WinRT.GeneratedBindableCustomProperty]
public sealed partial class NotificationViewItem : ObservableObject
{
    public GitHubNotificationThread Thread { get; private set; } = new();

    public string StableKey => Thread.Id;

    public string Title => string.IsNullOrWhiteSpace(Thread.Subject.Title) ? "GitHub notification" : Thread.Subject.Title;

    public string RepositoryText => Thread.Repository.FullName;

    public string TypeText => string.IsNullOrWhiteSpace(Thread.Subject.Type) ? "Thread" : SplitPascalCase(Thread.Subject.Type);

    public string ReasonText => string.IsNullOrWhiteSpace(Thread.Reason)
        ? "Notification"
        : Thread.Reason.Replace('_', ' ');

    public string UpdatedText => FormatTimeAgo(Thread.UpdatedAt);

    public string Glyph => Thread.Subject.Type switch
    {
        "Issue" => "\uE8A5",
        "PullRequest" => "\uE8EE",
        "Commit" => "\uE7C1",
        "Release" => "\uE896",
        "Discussion" => "\uE90A",
        _ => "\uEA8F"
    };

    [ObservableProperty]
    public partial bool IsUnread { get; set; }

    [ObservableProperty]
    public partial bool IsMuted { get; set; }

    [ObservableProperty]
    public partial bool IsSubscribed { get; set; }

    [ObservableProperty]
    public partial bool IsSubscriptionStateKnown { get; set; }

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    public bool IsActionEnabled => !IsBusy;

    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(IsActionEnabled));
    }

    public string ReadActionLabel => "Mark as read";

    public string SubscriptionActionLabel => !IsSubscriptionStateKnown
        ? "Manage following"
        : IsSubscribed ? "Unsubscribe from thread" : "Follow thread";

    public string MuteActionLabel => !IsSubscriptionStateKnown
        ? "Manage muting"
        : IsMuted ? "Unmute thread" : "Mute thread";

    public string SubscriptionGlyph => IsSubscribed ? "\uE735" : "\uE734";

    public string AutomationName => $"{Title}, {RepositoryText}, {(IsUnread ? "unread" : "read")}";

    public string ReadAutomationId => $"NotificationRead_{SanitizeAutomationId(StableKey)}";

    public string MuteAutomationId => $"NotificationMute_{SanitizeAutomationId(StableKey)}";

    public string SubscriptionAutomationId => $"NotificationSubscription_{SanitizeAutomationId(StableKey)}";

    public string OpenMenuAutomationId => $"NotificationOpenMenu_{SanitizeAutomationId(StableKey)}";

    public string ReadMenuAutomationId => $"NotificationReadMenu_{SanitizeAutomationId(StableKey)}";

    public string SubscriptionMenuAutomationId => $"NotificationSubscriptionMenu_{SanitizeAutomationId(StableKey)}";

    public string MuteMenuAutomationId => $"NotificationMuteMenu_{SanitizeAutomationId(StableKey)}";

    public ICommand? OpenCommand { get; set; }

    public ICommand? MarkReadCommand { get; set; }

    public ICommand? ToggleMuteCommand { get; set; }

    public ICommand? ToggleSubscriptionCommand { get; set; }

    public bool ApplyThread(GitHubNotificationThread thread)
    {
        bool changed = !HasSameProjection(Thread, thread);
        Thread = thread;
        IsUnread = thread.Unread;
        if (changed)
        {
            NotifyProjectionChanged();
        }

        return changed;
    }

    public static NotificationViewItem Create(GitHubNotificationThread thread)
    {
        NotificationViewItem item = new();
        item.ApplyThread(thread);
        return item;
    }

    public void ApplySubscription(GitHubNotificationSubscription subscription)
    {
        ArgumentNullException.ThrowIfNull(subscription);
        IsSubscribed = subscription.Subscribed;
        IsMuted = subscription.Ignored;
        IsSubscriptionStateKnown = true;
    }

    partial void OnIsUnreadChanged(bool value)
    {
        Thread.Unread = value;
        OnPropertyChanged(nameof(AutomationName));
    }

    partial void OnIsMutedChanged(bool value) => OnPropertyChanged(nameof(MuteActionLabel));

    partial void OnIsSubscribedChanged(bool value)
    {
        OnPropertyChanged(nameof(SubscriptionActionLabel));
        OnPropertyChanged(nameof(SubscriptionGlyph));
    }

    partial void OnIsSubscriptionStateKnownChanged(bool value)
    {
        OnPropertyChanged(nameof(SubscriptionActionLabel));
        OnPropertyChanged(nameof(MuteActionLabel));
    }

    private void NotifyProjectionChanged()
    {
        OnPropertyChanged(nameof(Thread));
        OnPropertyChanged(nameof(StableKey));
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(RepositoryText));
        OnPropertyChanged(nameof(TypeText));
        OnPropertyChanged(nameof(ReasonText));
        OnPropertyChanged(nameof(UpdatedText));
        OnPropertyChanged(nameof(Glyph));
        OnPropertyChanged(nameof(AutomationName));
        OnPropertyChanged(nameof(ReadAutomationId));
        OnPropertyChanged(nameof(MuteAutomationId));
        OnPropertyChanged(nameof(SubscriptionAutomationId));
        OnPropertyChanged(nameof(OpenMenuAutomationId));
        OnPropertyChanged(nameof(ReadMenuAutomationId));
        OnPropertyChanged(nameof(SubscriptionMenuAutomationId));
        OnPropertyChanged(nameof(MuteMenuAutomationId));
    }

    private static bool HasSameProjection(GitHubNotificationThread left, GitHubNotificationThread right) =>
        string.Equals(left.Id, right.Id, StringComparison.Ordinal)
        && left.Unread == right.Unread
        && left.UpdatedAt == right.UpdatedAt
        && string.Equals(left.Reason, right.Reason, StringComparison.Ordinal)
        && string.Equals(left.Subject.Title, right.Subject.Title, StringComparison.Ordinal)
        && string.Equals(left.Subject.Type, right.Subject.Type, StringComparison.Ordinal)
        && string.Equals(left.Subject.Url, right.Subject.Url, StringComparison.Ordinal)
        && string.Equals(left.Repository.FullName, right.Repository.FullName, StringComparison.Ordinal);

    private static string SplitPascalCase(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        return string.Concat(value.Select((character, index) =>
            index > 0 && char.IsUpper(character) ? $" {character}" : character.ToString()));
    }

    private static string FormatTimeAgo(DateTimeOffset? value)
    {
        if (value is null)
        {
            return string.Empty;
        }

        TimeSpan age = DateTimeOffset.Now - value.Value.ToLocalTime();
        if (age.TotalMinutes < 1)
        {
            return "just now";
        }

        if (age.TotalHours < 1)
        {
            return $"{(int)Math.Max(1, age.TotalMinutes)}m ago";
        }

        if (age.TotalDays < 1)
        {
            return $"{(int)Math.Max(1, age.TotalHours)}h ago";
        }

        return age.TotalDays < 30
            ? $"{(int)Math.Max(1, age.TotalDays)}d ago"
            : value.Value.ToLocalTime().ToString("MMM d, yyyy", CultureInfo.CurrentCulture);
    }

    private static string SanitizeAutomationId(string value) =>
        string.Concat(value.Select(character => char.IsLetterOrDigit(character) ? character : '_'));
}

public sealed partial class NotificationsPageViewModel : ObservableObject, IDisposable
{
    private enum NotificationPageLoadOutcome
    {
        Finished,
        RetryAfterMutation
    }

    private const int PageSize = 50;
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(1);
    private readonly IGitHubNotificationQueryService _queryService;
    private readonly IAuthService _authService;
    private readonly IAccountService _accountService;
    private readonly Action<GitHubNotificationThread, string> _openNotification;
    private readonly Func<GitHubNotificationThread, CancellationToken, Task>? _prefetchNotification;
    private readonly ITelemetryService _telemetry;
    private readonly IApplicationTaskCoordinator _taskCoordinator;
    private readonly NotificationInboxState _inboxState;
    private readonly List<GitHubNotificationThread> _loadedThreads = [];
    private CancellationTokenSource? _lifetimeCancellationTokenSource;
    private CancellationTokenSource? _loadCancellationTokenSource;
    private bool _initialized;
    private int _page;
    private int _loadVersion;
    private int _activeItemMutations;
    private PagedDataCompleteness _resultCompleteness = PagedDataCompleteness.Loading;
    private Task _pollTask = Task.CompletedTask;
    private Task _automaticMutationTask = Task.CompletedTask;
    private readonly object _destinationPrefetchSync = new();
    private CancellationTokenSource? _destinationPrefetchCancellation;
    private string _destinationPrefetchKey = string.Empty;
    private Task? _destinationPrefetchTask;
    private bool _destinationPrefetchFetchStarted;

    internal NotificationsPageViewModel(
        IGitHubNotificationQueryService queryService,
        IAuthService authService,
        IAccountService accountService,
        ITelemetryService telemetry,
        NotificationInboxState inboxState,
        Action<GitHubNotificationThread, string> openNotification,
        IApplicationTaskCoordinator? taskCoordinator = null,
        Func<GitHubNotificationThread, CancellationToken, Task>? prefetchNotification = null)
    {
        _queryService = queryService;
        _authService = authService;
        _accountService = accountService;
        _telemetry = SafeTelemetryService.Wrap(telemetry);
        _taskCoordinator = taskCoordinator ?? new ApplicationTaskCoordinator();
        _inboxState = inboxState;
        _openNotification = openNotification;
        _prefetchNotification = prefetchNotification;
    }

    public KeyedObservableCollection<NotificationViewItem, GitHubNotificationThread> Notifications { get; } = [];

    public IReadOnlyList<string> FilterOptions { get; } = ["Unread", "All", "Participating"];

    [ObservableProperty]
    public partial string SearchText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial NotificationListFilter SelectedFilter { get; set; } = NotificationListFilter.Unread;

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    public partial bool IsLoadingMore { get; set; }

    [ObservableProperty]
    public partial bool HasMore { get; set; }

    [ObservableProperty]
    public partial bool IsEmpty { get; set; }

    [ObservableProperty]
    public partial bool IsErrorVisible { get; set; }

    [ObservableProperty]
    public partial string ErrorMessage { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ResultCountText { get; set; } = "0 notifications";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(MarkAllReadCommand))]
    public partial bool IsMarkAllReadInProgress { get; set; }

    public async Task InitializeAsync()
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        _lifetimeCancellationTokenSource = new CancellationTokenSource();
        _telemetry.TrackEvent(
            "notifications.opened",
            new Dictionary<string, string?> { ["source"] = TelemetryTaxonomy.Sources.Shell });
        await LoadFirstPageAsync(QueryFetchPolicy.StaleFirst, _lifetimeCancellationTokenSource.Token);
        string? accountPartition = TryGetSession(out _, out string userId) ? userId : null;
        _pollTask = _taskCoordinator.RunAsync(
            PollAsync,
            new ApplicationTaskOptions("notifications.poll", accountPartition),
            _lifetimeCancellationTokenSource.Token);
    }

    public async Task ChangeFilterAsync(NotificationListFilter filter)
    {
        if (SelectedFilter == filter && _page > 0)
        {
            return;
        }

        SelectedFilter = filter;
        _telemetry.TrackEvent(
            "notifications.filter.changed",
            new Dictionary<string, string?> { ["filter_type"] = FilterTelemetryValue(filter) });
        await LoadFirstPageAsync(QueryFetchPolicy.StaleFirst, _lifetimeCancellationTokenSource?.Token ?? CancellationToken.None);
    }

    [RelayCommand]
    private async Task LoadMoreAsync()
    {
        if (!HasMore || IsLoadingMore)
        {
            return;
        }

        IsLoadingMore = true;
        try
        {
            await LoadPageAsync(
                _page + 1,
                QueryFetchPolicy.StaleFirst,
                replace: false,
                _lifetimeCancellationTokenSource?.Token ?? CancellationToken.None);
        }
        finally
        {
            IsLoadingMore = false;
        }
    }

    [RelayCommand]
    private void OpenNotification(NotificationViewItem? item)
    {
        if (item is null)
        {
            return;
        }

        _openNotification(item.Thread, "notifications");
        if (item.IsUnread)
        {
            string? accountPartition = TryGetSession(out _, out string userId) ? userId : null;
            _automaticMutationTask = _taskCoordinator.RunAsync(
                token => MarkReadAfterOpenAsync(item, token),
                new ApplicationTaskOptions("notifications.mark_read_after_open", accountPartition),
                LifetimeToken);
        }
    }

    private async Task MarkReadAfterOpenAsync(
        NotificationViewItem item,
        CancellationToken cancellationToken)
    {
        // Opening the destination is the primary interaction. The notification
        // transport can perform synchronous setup before its first await, so give
        // navigation and its first frame an uncontested turn before starting this
        // best-effort secondary mutation.
        await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
        await MarkReadCoreAsync(item, cancellationToken);
    }

    [RelayCommand]
    private Task MarkReadAsync(NotificationViewItem? item) =>
        item is null ? Task.CompletedTask : MarkReadCoreAsync(item, LifetimeToken);

    [RelayCommand]
    private async Task ToggleSubscriptionAsync(NotificationViewItem? item)
    {
        if (item is null || item.IsBusy)
        {
            return;
        }

        await EnsureSubscriptionStateAsync(item);
        if (!item.IsSubscriptionStateKnown || item.IsBusy || !TryGetSession(out string token, out string userId))
        {
            return;
        }

        bool previousSubscribed = item.IsSubscribed;
        bool previousMuted = item.IsMuted;
        bool shouldSubscribe = !previousSubscribed;
        NotificationMutationLease lease = _inboxState.BeginSubscriptionMutation(userId);
        _activeItemMutations++;
        MarkAllReadCommand.NotifyCanExecuteChanged();
        item.IsBusy = true;
        item.IsSubscribed = shouldSubscribe;
        if (shouldSubscribe)
        {
            item.IsMuted = false;
        }

        try
        {
            if (shouldSubscribe)
            {
                GitHubNotificationSubscription result = await _queryService.SubscribeThreadAsync(
                    token,
                    userId,
                    item.StableKey,
                    LifetimeToken);
                item.ApplySubscription(result);
            }
            else
            {
                await _queryService.UnsubscribeThreadAsync(token, userId, item.StableKey, LifetimeToken);
                item.ApplySubscription(new GitHubNotificationSubscription());
            }

            _inboxState.CompleteMutation(lease);
            TrackAction(
                shouldSubscribe ? TelemetryTaxonomy.Actions.Follow : TelemetryTaxonomy.Actions.Unsubscribe,
                TelemetryTaxonomy.Results.Success);
        }
        catch (OperationCanceledException)
        {
            RestoreSubscriptionState(item, previousSubscribed, previousMuted);
            _inboxState.RollbackMutation(lease);
            TrackAction(
                shouldSubscribe ? TelemetryTaxonomy.Actions.Follow : TelemetryTaxonomy.Actions.Unsubscribe,
                TelemetryTaxonomy.Results.Cancelled);
        }
        catch (Exception)
        {
            RestoreSubscriptionState(item, previousSubscribed, previousMuted);
            _inboxState.RollbackMutation(lease);
            ShowError("The thread following state could not be updated.");
            TrackAction(
                shouldSubscribe ? TelemetryTaxonomy.Actions.Follow : TelemetryTaxonomy.Actions.Unsubscribe,
                TelemetryTaxonomy.Results.Error);
        }
        finally
        {
            item.IsBusy = false;
            _activeItemMutations--;
            MarkAllReadCommand.NotifyCanExecuteChanged();
        }
    }

    [RelayCommand]
    private async Task ToggleMuteAsync(NotificationViewItem? item)
    {
        if (item is null || item.IsBusy)
        {
            return;
        }

        await EnsureSubscriptionStateAsync(item);
        if (!item.IsSubscriptionStateKnown || item.IsBusy || !TryGetSession(out string token, out string userId))
        {
            return;
        }

        bool previousSubscribed = item.IsSubscribed;
        bool previousMuted = item.IsMuted;
        bool shouldMute = !previousMuted;
        NotificationMutationLease lease = _inboxState.BeginSubscriptionMutation(userId);
        _activeItemMutations++;
        MarkAllReadCommand.NotifyCanExecuteChanged();
        item.IsBusy = true;
        item.IsMuted = shouldMute;
        if (!shouldMute)
        {
            item.IsSubscribed = true;
        }

        try
        {
            GitHubNotificationSubscription result = shouldMute
                ? await _queryService.MuteThreadAsync(token, userId, item.StableKey, LifetimeToken)
                : await _queryService.UnmuteThreadAsync(token, userId, item.StableKey, LifetimeToken);
            item.ApplySubscription(result);

            _inboxState.CompleteMutation(lease);
            TrackAction(
                shouldMute ? TelemetryTaxonomy.Actions.Mute : TelemetryTaxonomy.Actions.Unmute,
                TelemetryTaxonomy.Results.Success);
        }
        catch (OperationCanceledException)
        {
            RestoreSubscriptionState(item, previousSubscribed, previousMuted);
            _inboxState.RollbackMutation(lease);
            TrackAction(
                shouldMute ? TelemetryTaxonomy.Actions.Mute : TelemetryTaxonomy.Actions.Unmute,
                TelemetryTaxonomy.Results.Cancelled);
        }
        catch (Exception)
        {
            RestoreSubscriptionState(item, previousSubscribed, previousMuted);
            _inboxState.RollbackMutation(lease);
            ShowError("The notification subscription could not be updated.");
            TrackAction(
                shouldMute ? TelemetryTaxonomy.Actions.Mute : TelemetryTaxonomy.Actions.Unmute,
                TelemetryTaxonomy.Results.Error);
        }
        finally
        {
            item.IsBusy = false;
            _activeItemMutations--;
            MarkAllReadCommand.NotifyCanExecuteChanged();
        }
    }

    public async Task EnsureSubscriptionStateAsync(NotificationViewItem? item)
    {
        if (item is null || item.IsSubscriptionStateKnown || item.IsBusy || !TryGetSession(out string token, out string userId))
        {
            return;
        }

        item.IsBusy = true;
        try
        {
            CachedResult<GitHubNotificationSubscription> result = await _queryService.GetSubscriptionAsync(
                token,
                userId,
                item.StableKey,
                QueryFetchPolicy.StaleFirst,
                LifetimeToken);
            if (result.Value is not null)
            {
                item.ApplySubscription(result.Value);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
            ShowError("Thread notification settings are temporarily unavailable.");
        }
        finally
        {
            item.IsBusy = false;
        }
    }

    public Task PrefetchDestinationAsync(NotificationViewItem? item)
    {
        if (item is null || _prefetchNotification is null)
        {
            return Task.CompletedTask;
        }

        CancellationTokenSource prediction;
        CancellationTokenSource? previous;
        Task predictionTask;
        lock (_destinationPrefetchSync)
        {
            if (string.Equals(_destinationPrefetchKey, item.StableKey, StringComparison.Ordinal) &&
                _destinationPrefetchTask is not null)
            {
                return _destinationPrefetchTask;
            }

            prediction = new CancellationTokenSource();
            previous = _destinationPrefetchCancellation;
            _destinationPrefetchCancellation = prediction;
            _destinationPrefetchKey = item.StableKey;
            _destinationPrefetchFetchStarted = false;
            predictionTask = RunDestinationPrefetchAsync(item, prediction);
            _destinationPrefetchTask = predictionTask;
        }

        TryCancel(previous);
        return predictionTask;
    }

    private async Task RunDestinationPrefetchAsync(
        NotificationViewItem item,
        CancellationTokenSource prediction)
    {
        try
        {
            // A short pointer pass is not useful intent and can make a click slower by
            // starting cache persistence immediately before the navigation frame.
            await Task.Delay(TimeSpan.FromMilliseconds(150), prediction.Token).ConfigureAwait(false);
            lock (_destinationPrefetchSync)
            {
                if (!ReferenceEquals(_destinationPrefetchCancellation, prediction) ||
                    !string.Equals(_destinationPrefetchKey, item.StableKey, StringComparison.Ordinal))
                {
                    return;
                }

                _destinationPrefetchFetchStarted = true;
            }

            // Once prediction passes the intent threshold, let the request finish. A click can
            // then join the same Phase 0 request even after this page unloads.
            await _prefetchNotification!(item.Thread, CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            // Prediction must never alter inbox availability or error state.
        }
        finally
        {
            lock (_destinationPrefetchSync)
            {
                if (ReferenceEquals(_destinationPrefetchCancellation, prediction))
                {
                    _destinationPrefetchCancellation = null;
                }
            }

            prediction.Dispose();
        }
    }

    public void CancelDestinationPrefetch()
    {
        CancellationTokenSource? prediction;
        lock (_destinationPrefetchSync)
        {
            prediction = _destinationPrefetchCancellation;
            _destinationPrefetchCancellation = null;
            _destinationPrefetchKey = string.Empty;
            _destinationPrefetchTask = null;
            _destinationPrefetchFetchStarted = false;
        }

        TryCancel(prediction);
    }

    public async Task PromoteDestinationPrefetchAsync(NotificationViewItem? item)
    {
        if (item is null || _prefetchNotification is null)
        {
            return;
        }

        CancellationTokenSource? pendingPrediction = null;
        Task prefetchTask;
        lock (_destinationPrefetchSync)
        {
            bool canJoinStartedPrediction =
                string.Equals(_destinationPrefetchKey, item.StableKey, StringComparison.Ordinal) &&
                _destinationPrefetchFetchStarted &&
                _destinationPrefetchTask is not null;
            if (canJoinStartedPrediction)
            {
                prefetchTask = _destinationPrefetchTask!;
            }
            else
            {
                pendingPrediction = _destinationPrefetchCancellation;
                _destinationPrefetchCancellation = null;
                _destinationPrefetchKey = item.StableKey;
                _destinationPrefetchFetchStarted = true;
                prefetchTask = _prefetchNotification(item.Thread, CancellationToken.None);
                _destinationPrefetchTask = prefetchTask;
            }
        }

        TryCancel(pendingPrediction);
        try
        {
            // Navigation owns this promoted request. It intentionally survives the inbox page
            // unloading so the destination can join the Phase 0 in-flight query immediately.
            await prefetchTask.ConfigureAwait(false);
        }
        catch
        {
            // Prediction is best-effort and must never block or alter navigation state.
        }
    }

    private static void TryCancel(CancellationTokenSource? source)
    {
        try
        {
            source?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    [RelayCommand(CanExecute = nameof(CanMarkAllRead))]
    private async Task MarkAllReadAsync()
    {
        if (!CanMarkAllRead() || !TryGetSession(out string token, out string userId))
        {
            return;
        }

        (GitHubNotificationThread Thread, bool WasUnread)[] priorStates = _loadedThreads
            .Select(static thread => (thread, thread.Unread))
            .ToArray();
        NotificationMutationLease lease = _inboxState.BeginMarkAllReadMutation(userId);
        IsMarkAllReadInProgress = true;
        foreach ((GitHubNotificationThread thread, _) in priorStates)
        {
            thread.Unread = false;
        }

        foreach (NotificationViewItem item in Notifications)
        {
            item.IsBusy = true;
        }

        ApplyVisibleRows();

        try
        {
            await _queryService.MarkAllReadAsync(token, userId, LifetimeToken);
            if (SelectedFilter == NotificationListFilter.Unread)
            {
                _loadedThreads.Clear();
                ApplyVisibleRows();
            }

            _inboxState.CompleteMutation(lease);
            TrackAction(TelemetryTaxonomy.Actions.MarkAllRead, TelemetryTaxonomy.Results.Success);
        }
        catch (OperationCanceledException)
        {
            RestoreReadStates(priorStates);
            _inboxState.RollbackMutation(lease);
            TrackAction(TelemetryTaxonomy.Actions.MarkAllRead, TelemetryTaxonomy.Results.Cancelled);
        }
        catch (Exception)
        {
            RestoreReadStates(priorStates);
            _inboxState.RollbackMutation(lease);

            ShowError("Notifications could not be marked as read.");
            TrackAction(TelemetryTaxonomy.Actions.MarkAllRead, TelemetryTaxonomy.Results.Error);
        }
        finally
        {
            foreach (NotificationViewItem item in Notifications)
            {
                item.IsBusy = false;
            }

            IsMarkAllReadInProgress = false;
            MarkAllReadCommand.NotifyCanExecuteChanged();
        }
    }

    public void Dispose()
    {
        CancelDestinationPrefetch();
        _loadCancellationTokenSource?.Cancel();
        _loadCancellationTokenSource?.Dispose();
        _loadCancellationTokenSource = null;
        _lifetimeCancellationTokenSource?.Cancel();
        _lifetimeCancellationTokenSource?.Dispose();
        _lifetimeCancellationTokenSource = null;
        GC.KeepAlive(_pollTask);
        GC.KeepAlive(_automaticMutationTask);
        _initialized = false;
    }

    partial void OnSearchTextChanged(string value) => ApplyVisibleRows();

    private async Task MarkReadCoreAsync(NotificationViewItem item, CancellationToken cancellationToken)
    {
        if (IsMarkAllReadInProgress || item.IsBusy || !item.IsUnread || !TryGetSession(out string token, out string userId))
        {
            return;
        }

        bool previous = item.IsUnread;
        NotificationMutationLease lease = _inboxState.BeginReadStateMutation(userId, item.StableKey, previous, isUnread: false);
        _activeItemMutations++;
        MarkAllReadCommand.NotifyCanExecuteChanged();
        item.IsBusy = true;
        item.IsUnread = false;
        try
        {
            await _queryService.MarkThreadReadAsync(token, userId, item.StableKey, cancellationToken);

            if (SelectedFilter == NotificationListFilter.Unread)
            {
                _loadedThreads.RemoveAll(thread => string.Equals(thread.Id, item.StableKey, StringComparison.Ordinal));
                ApplyVisibleRows();
            }

            _inboxState.CompleteMutation(lease);
            TrackAction(TelemetryTaxonomy.Actions.MarkRead, TelemetryTaxonomy.Results.Success);
        }
        catch (OperationCanceledException)
        {
            item.IsUnread = previous;
            _inboxState.RollbackMutation(lease);
            TrackAction(TelemetryTaxonomy.Actions.MarkRead, TelemetryTaxonomy.Results.Cancelled);
        }
        catch (Exception)
        {
            item.IsUnread = previous;
            _inboxState.RollbackMutation(lease);
            ShowError("The notification read state could not be updated.");
            TrackAction(TelemetryTaxonomy.Actions.MarkRead, TelemetryTaxonomy.Results.Error);
        }
        finally
        {
            item.IsBusy = false;
            _activeItemMutations--;
            MarkAllReadCommand.NotifyCanExecuteChanged();
        }
    }

    private async Task LoadFirstPageAsync(QueryFetchPolicy fetchPolicy, CancellationToken cancellationToken)
    {
        _loadCancellationTokenSource?.Cancel();
        _loadCancellationTokenSource?.Dispose();
        _loadCancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        int version = ++_loadVersion;
        IsLoading = Notifications.Count == 0;
        IsErrorVisible = false;
        try
        {
            while (version == _loadVersion)
            {
                NotificationPageLoadOutcome outcome = await LoadPageAsync(
                    1,
                    fetchPolicy,
                    replace: true,
                    _loadCancellationTokenSource.Token,
                    version);
                if (outcome != NotificationPageLoadOutcome.RetryAfterMutation)
                {
                    break;
                }

                await WaitForInboxMutationsToSettleAsync(_loadCancellationTokenSource.Token);
            }
        }
        finally
        {
            if (version == _loadVersion)
            {
                IsLoading = false;
                UpdateEmptyAndCount();
            }
        }
    }

    private async Task<NotificationPageLoadOutcome> LoadPageAsync(
        int page,
        QueryFetchPolicy fetchPolicy,
        bool replace,
        CancellationToken cancellationToken,
        int? expectedVersion = null)
    {
        Stopwatch loadDuration = Stopwatch.StartNew();
        int version = expectedVersion ?? _loadVersion;
        if (!TryGetSession(out string token, out string userId))
        {
            ShowError("Sign in to view notifications.");
            return NotificationPageLoadOutcome.Finished;
        }

        NotificationListFilter requestedFilter = SelectedFilter;
        long inboxGeneration = _inboxState.CaptureMutationGeneration(userId);
        try
        {
            CachedResult<GitHubNotificationThread[]> result = await _queryService.GetPageAsync(
                token,
                userId,
                requestedFilter,
                page,
                PageSize,
                fetchPolicy,
                cancellationToken);
            if (version != _loadVersion ||
                requestedFilter != SelectedFilter ||
                result.Value is null)
            {
                return NotificationPageLoadOutcome.Finished;
            }

            if (!_inboxState.IsCurrentMutationGeneration(userId, inboxGeneration))
            {
                return NotificationPageLoadOutcome.RetryAfterMutation;
            }

            if (replace && result.RefreshError is null)
            {
                _loadedThreads.Clear();
            }

            HashSet<string> existing = _loadedThreads.Select(static thread => thread.Id).ToHashSet(StringComparer.Ordinal);
            foreach (GitHubNotificationThread thread in result.Value)
            {
                int index = _loadedThreads.FindIndex(existingThread => string.Equals(existingThread.Id, thread.Id, StringComparison.Ordinal));
                if (index >= 0)
                {
                    _loadedThreads[index] = thread;
                }
                else if (existing.Add(thread.Id))
                {
                    _loadedThreads.Add(thread);
                }
            }

            _page = page;
            HasMore = result.Value.Length == PageSize;
            _resultCompleteness = HasMore || result.RefreshError is not null
                ? PagedDataCompleteness.Partial
                : PagedDataCompleteness.Complete;
            ApplyVisibleRows();
            if (result.RefreshError is not null)
            {
                ShowError(BuildRefreshFailureMessage("Sync is incomplete."));
            }

            if (requestedFilter == NotificationListFilter.Unread)
            {
                UpdateAccountWideInboxState(
                    userId,
                    _loadedThreads,
                    HasMore,
                    result.FetchedAt,
                    inboxGeneration);
            }

            _telemetry.TrackEvent(
                "notifications.list.loaded",
                new Dictionary<string, string?>
                {
                    ["cache_state"] = result.CacheState.ToString(),
                    ["result"] = result.RefreshError is null
                        ? TelemetryTaxonomy.Results.Success
                        : TelemetryTaxonomy.Results.CachedError,
                    ["error_kind"] = result.RefreshError is null
                        ? null
                        : GetTelemetryErrorKind(result.RefreshError),
                    ["count_bucket"] = CountBucket(_loadedThreads.Count),
                    ["duration_bucket"] = TelemetrySanitizer.CreateDurationBucket(loadDuration.Elapsed)
                });
            return NotificationPageLoadOutcome.Finished;
        }
        catch (OperationCanceledException)
        {
            _telemetry.TrackEvent(
                "notifications.list.loaded",
                new Dictionary<string, string?>
                {
                    ["result"] = TelemetryTaxonomy.Results.Cancelled,
                    ["duration_bucket"] = TelemetrySanitizer.CreateDurationBucket(loadDuration.Elapsed)
                });
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _resultCompleteness = PagedDataCompleteness.Partial;
            UpdateEmptyAndCount();
            ShowError(Notifications.Count == 0
                ? "Notifications could not be loaded."
                : BuildRefreshFailureMessage("More notifications could not be loaded."));
            _telemetry.TrackEvent(
                "notifications.list.loaded",
                new Dictionary<string, string?>
                {
                    ["result"] = TelemetryTaxonomy.Results.Error,
                    ["error_kind"] = GetTelemetryErrorKind(ex),
                    ["duration_bucket"] = TelemetrySanitizer.CreateDurationBucket(loadDuration.Elapsed)
                });
            return NotificationPageLoadOutcome.Finished;
        }
    }

    private async Task WaitForInboxMutationsToSettleAsync(CancellationToken cancellationToken)
    {
        while (_inboxState.HasActiveMutations)
        {
            TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            PropertyChangedEventHandler? handler = null;
            handler = (_, args) =>
            {
                if ((string.IsNullOrEmpty(args.PropertyName) ||
                     args.PropertyName == nameof(NotificationInboxState.HasActiveMutations)) &&
                    !_inboxState.HasActiveMutations)
                {
                    completion.TrySetResult();
                }
            };

            _inboxState.PropertyChanged += handler;
            try
            {
                if (!_inboxState.HasActiveMutations)
                {
                    return;
                }

                await completion.Task.WaitAsync(cancellationToken);
            }
            finally
            {
                _inboxState.PropertyChanged -= handler;
            }
        }
    }

    private async Task PollAsync(CancellationToken cancellationToken)
    {
        using PeriodicTimer timer = new(PollInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                await SynchronizeVisibleFirstPageAsync(cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    internal async Task SynchronizeVisibleFirstPageAsync(CancellationToken cancellationToken = default)
    {
        if (!TryGetSession(out string token, out string userId))
        {
            return;
        }

        NotificationListFilter requestedFilter = SelectedFilter;
        int requestedVersion = _loadVersion;
        long inboxGeneration = _inboxState.CaptureMutationGeneration(userId);
        try
        {
            CachedResult<GitHubNotificationThread[]> result = await _queryService.GetPageAsync(
                token,
                userId,
                requestedFilter,
                1,
                PageSize,
                QueryFetchPolicy.NetworkOnly,
                cancellationToken);
            if (result.Value is null ||
                requestedVersion != _loadVersion ||
                requestedFilter != SelectedFilter ||
                !_inboxState.IsCurrentMutationGeneration(userId, inboxGeneration))
            {
                return;
            }

            HashSet<string> firstPageIds = result.Value.Select(static thread => thread.Id).ToHashSet(StringComparer.Ordinal);
            List<GitHubNotificationThread> merged = [.. result.Value];
            merged.AddRange(_loadedThreads.Where(thread => !firstPageIds.Contains(thread.Id)));
            _loadedThreads.Clear();
            _loadedThreads.AddRange(merged);
            ApplyVisibleRows();
            HasMore = result.Value.Length == PageSize;
            _resultCompleteness = HasMore || result.RefreshError is not null
                ? PagedDataCompleteness.Partial
                : PagedDataCompleteness.Complete;
            if (requestedFilter == NotificationListFilter.Unread)
            {
                UpdateAccountWideInboxState(
                    userId,
                    result.Value,
                    HasMore,
                    result.FetchedAt,
                    inboxGeneration);
            }
            else
            {
                await RefreshAccountWideUnreadCountAsync(token, userId, cancellationToken);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ShowError(BuildRefreshFailureMessage("Background sync is unavailable."));
        }
    }

    private void ApplyVisibleRows()
    {
        IEnumerable<GitHubNotificationThread> visible = _loadedThreads;
        string search = SearchText.Trim();
        if (!string.IsNullOrWhiteSpace(search))
        {
            visible = visible.Where(thread =>
                thread.Subject.Title.Contains(search, StringComparison.OrdinalIgnoreCase)
                || thread.Repository.FullName.Contains(search, StringComparison.OrdinalIgnoreCase)
                || thread.Subject.Type.Contains(search, StringComparison.OrdinalIgnoreCase)
                || thread.Reason.Contains(search, StringComparison.OrdinalIgnoreCase));
        }

        Notifications.ApplySnapshot(
            visible,
            static thread => thread.Id,
            static item => item.StableKey,
            CreateNotificationItem,
            static (item, thread) => item.ApplyThread(thread));
        UpdateEmptyAndCount();
    }

    private NotificationViewItem CreateNotificationItem(GitHubNotificationThread thread)
    {
        NotificationViewItem item = NotificationViewItem.Create(thread);
        item.OpenCommand = new RelayCommand(() => OpenNotification(item));
        item.MarkReadCommand = new AsyncRelayCommand(() => MarkReadAsync(item));
        item.ToggleSubscriptionCommand = new AsyncRelayCommand(() => ToggleSubscriptionAsync(item));
        item.ToggleMuteCommand = new AsyncRelayCommand(() => ToggleMuteAsync(item));
        return item;
    }

    private void UpdateEmptyAndCount()
    {
        IsEmpty = !IsLoading && Notifications.Count == 0;
        string loadedScope = _resultCompleteness == PagedDataCompleteness.Complete
            ? FormatNotificationCount(_loadedThreads.Count)
            : $"{FormatNotificationCount(_loadedThreads.Count)} loaded (partial)";
        ResultCountText = Notifications.Count == _loadedThreads.Count
            ? loadedScope
            : $"{Notifications.Count} shown | {loadedScope}";
        MarkAllReadCommand.NotifyCanExecuteChanged();
    }

    private string BuildRefreshFailureMessage(string detail) =>
        _loadedThreads.Count == 0
            ? detail
            : $"Showing {FormatNotificationCount(_loadedThreads.Count)} already loaded. {detail}";

    private static string FormatNotificationCount(int count) =>
        count == 1 ? "1 notification" : $"{count} notifications";

    private bool CanMarkAllRead() =>
        !IsMarkAllReadInProgress &&
        _activeItemMutations == 0 &&
        (_inboxState.UnreadCount > 0 || _loadedThreads.Any(static thread => thread.Unread));

    private void RestoreReadStates(IEnumerable<(GitHubNotificationThread Thread, bool WasUnread)> priorStates)
    {
        foreach ((GitHubNotificationThread thread, bool wasUnread) in priorStates)
        {
            thread.Unread = wasUnread;
        }

        ApplyVisibleRows();
    }

    private static void RestoreSubscriptionState(
        NotificationViewItem item,
        bool wasSubscribed,
        bool wasMuted)
    {
        item.IsSubscribed = wasSubscribed;
        item.IsMuted = wasMuted;
        item.IsSubscriptionStateKnown = true;
    }

    private async Task RefreshAccountWideUnreadCountAsync(
        string token,
        string userId,
        CancellationToken cancellationToken)
    {
        long generation = _inboxState.CaptureMutationGeneration(userId);
        CachedResult<GitHubNotificationThread[]> result = await _queryService.GetPageAsync(
            token,
            userId,
            NotificationListFilter.Unread,
            1,
            PageSize,
            QueryFetchPolicy.NetworkOnly,
            cancellationToken);
        if (result.Value is null || !_inboxState.IsCurrentMutationGeneration(userId, generation))
        {
            return;
        }

        UpdateAccountWideInboxState(
            userId,
            result.Value,
            result.Value.Length == PageSize,
            result.FetchedAt,
            generation);
    }

    private void UpdateAccountWideInboxState(
        string userId,
        IEnumerable<GitHubNotificationThread> threads,
        bool isPartial,
        DateTimeOffset? fetchedAt,
        long mutationGeneration)
    {
        _inboxState.ApplySnapshot(
            userId,
            threads,
            isPartial,
            fetchedAt,
            NotificationCountSource.AccountWideWorkspace,
            mutationGeneration);
    }

    private CancellationToken LifetimeToken =>
        _lifetimeCancellationTokenSource?.Token ?? CancellationToken.None;

    private bool TryGetSession(out string token, out string userId)
    {
        long accountId = _authService.AuthenticatedUser?.Id ?? _accountService.GetUser();
        token = _authService.GetToken(accountId) ?? string.Empty;
        if (string.IsNullOrWhiteSpace(token))
        {
            userId = string.Empty;
            return false;
        }

        userId = GitHubAuthenticationConstants.IsPublicAccessToken(token)
            ? "public"
            : accountId > 0 ? accountId.ToString(CultureInfo.InvariantCulture) : "current";
        return true;
    }

    private void ShowError(string message)
    {
        ErrorMessage = message;
        IsErrorVisible = true;
    }

    private void TrackAction(string action, string result) =>
        _telemetry.TrackEvent(
            "notifications.action.executed",
            new Dictionary<string, string?> { ["action"] = action, ["result"] = result });

    private static string FilterTelemetryValue(NotificationListFilter filter) => filter switch
    {
        NotificationListFilter.Unread => TelemetryTaxonomy.FilterTypes.Unread,
        NotificationListFilter.Participating => TelemetryTaxonomy.FilterTypes.Participating,
        _ => TelemetryTaxonomy.FilterTypes.All
    };

    private static string CountBucket(int count) => TelemetryTaxonomy.CountBucket(count);

    private static string GetTelemetryErrorKind(Exception exception) => exception switch
    {
        GitHubAuthenticationException => "authentication",
        GitHubApiException => "api",
        HttpRequestException => "network",
        OperationCanceledException => "cancelled",
        _ => "unexpected"
    };
}
