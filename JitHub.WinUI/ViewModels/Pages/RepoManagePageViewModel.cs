using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using JitHub.Models.GitHub;
using JitHub.Services;
using JitHub.WinUI.Helpers;
using JitHub.WinUI.ViewModels.Common;
using Microsoft.UI.Dispatching;

namespace JitHub.WinUI.ViewModels.Pages;

[WinRT.GeneratedBindableCustomProperty]
public sealed partial class RepoManagePageViewModel : ViewModelBase
{
    private readonly IAuthService _authService;
    private readonly IAccountService _accountService;
    private readonly IGitHubClientService _gitHubClientService;
    private readonly IGitHubRepositoryIndexService _repositoryIndexService;
    private readonly ShellPageViewModel _shell;
    private readonly ITelemetryService _telemetry;
    private readonly DispatcherQueue _dispatcherQueue;
    private IReadOnlyList<GitHubRepository> _indexedRepositories = [];
    private CancellationTokenSource? _activeSession;
    private string _accessToken = string.Empty;
    private string _userId = string.Empty;
    private bool _initialized;
    private bool _cacheInitialized;
    private bool _isActive;
    private bool _isIndexSubscribed;
    private AccountRepositoryIndexSnapshot? _lastAppliedSnapshot;
    private bool _openedTracked;
    private bool _usesAutomationRepositories;
    private bool _projectionReady;
    private bool _isPublicProfilePreview;
    private bool _isIndexComplete;
    private bool _disposed;
    private Task? _synchronizationTask;

    public RepoManagePageViewModel()
    {
        _authService = GetService<IAuthService>();
        _accountService = GetService<IAccountService>();
        _gitHubClientService = GetService<IGitHubClientService>();
        _repositoryIndexService = GetService<IGitHubRepositoryIndexService>();
        _shell = GetService<ShellPageViewModel>();
        _telemetry = SafeTelemetryService.Wrap(GetService<ITelemetryService>());
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();

        FilterOptions =
        [
            new(RepositoryLibraryFilter.All, GetString("RepoManage.FilterAll", "All")),
            new(RepositoryLibraryFilter.Public, GetString("RepoManage.FilterPublic", "Public")),
            new(RepositoryLibraryFilter.Private, GetString("RepoManage.FilterPrivate", "Private")),
            new(RepositoryLibraryFilter.Forked, GetString("RepoManage.FilterForked", "Forked")),
            new(RepositoryLibraryFilter.Archived, GetString("RepoManage.FilterArchived", "Archived"))
        ];
        SortOptions =
        [
            new(RepositoryLibrarySort.RecentlyUpdated, GetString("RepoManage.SortRecentlyUpdated", "Last updated")),
            new(RepositoryLibrarySort.Name, GetString("RepoManage.SortName", "Name")),
            new(RepositoryLibrarySort.MostStars, GetString("RepoManage.SortMostStars", "Most stars"))
        ];
        SelectedFilterOption = FilterOptions[0];
        SelectedSortOption = SortOptions[0];
        EmptyStateTitle = GetString("RepoManage.EmptyStateTitle", "No account repositories");
        EmptyStateDescription = GetString(
            "RepoManage.EmptyStateDescription",
            "Repositories this account owns or can access will appear here.");
        _projectionReady = true;
    }

    public KeyedObservableCollection<RepositoryLibraryViewItem, GitHubRepository> Repositories { get; } = [];

    public event EventHandler? ProjectionChanging;

    public event EventHandler? ProjectionChanged;

    public event EventHandler? SelectionStateChanged;

    public ObservableCollection<RepositoryFilterOption> FilterOptions { get; }

    public ObservableCollection<RepositorySortOption> SortOptions { get; }

    public string PageTitle => GetString("RepoManage.PageTitle", "Repositories");

    public string NewRepositoryButtonText => GetString("RepoManage.NewRepository", "New");

    public string SelectButtonText => IsSelectionMode
        ? GetString("Common.DoneButton", "Done")
        : GetString("RepoManage.SelectRepositories", "Select");

    public string DeleteSelectedButtonText => GetString("RepoManage.DeleteSelectedButton", "Delete selected");

    public string ClearSelectionButtonText => GetString("RepoManage.ClearSelectionButton", "Clear selection");

    public string OpenRepositoryMenuText => GetString("RepoManage.ContextOpen", "Open repository");

    public string OpenOwnerMenuText => GetString("RepoManage.ContextOwner", "Open owner profile");

    public string CopyRepositoryLinkMenuText => GetString("RepoManage.ContextCopy", "Copy repository link");

    public string DeleteRepositoryMenuText => GetString("RepoManage.ContextDelete", "Delete repository");

    public string RetryButtonText => GetString("Common.RetryButton", "Retry");

    public string DeleteDialogTitle => GetString("RepoManage.DeleteDialogTitle", "Delete repositories");

    public string DeleteDialogConfirmButtonText => GetString("RepoManage.DeleteDialogConfirmButton", "Delete");

    public string DeleteDialogCloseButtonText => GetString("cancel.Content", "Cancel");

    public string DeleteFailureDialogTitle => GetString("RepoManage.DeleteFailureDialogTitle", "Some repositories were not deleted");

    public string CloseButtonText => GetString("Common.CloseButton", "Close");

    [ObservableProperty]
    public partial string SearchText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial RepositoryFilterOption SelectedFilterOption { get; set; }

    [ObservableProperty]
    public partial RepositorySortOption SelectedSortOption { get; set; }

    [ObservableProperty]
    public partial string ResultCountText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string StatusText { get; set; } = string.Empty;

    public bool IsStatusVisible => !string.IsNullOrWhiteSpace(StatusText);

    [ObservableProperty]
    public partial bool AreRepositoriesVisible { get; set; }

    [ObservableProperty]
    public partial bool IsEmptyStateVisible { get; set; }

    [ObservableProperty]
    public partial string EmptyStateTitle { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string EmptyStateDescription { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsDeletionProgressVisible { get; set; }

    [ObservableProperty]
    public partial bool IsLoadingRepositories { get; set; }

    [ObservableProperty]
    public partial bool IsSynchronizing { get; set; }

    [ObservableProperty]
    public partial bool IsRetryVisible { get; set; }

    [ObservableProperty]
    public partial double DeletionProgressValue { get; set; }

    [ObservableProperty]
    public partial double DeletionProgressMaximum { get; set; } = 1;

    [ObservableProperty]
    public partial bool IsInteractionEnabled { get; set; } = true;

    [ObservableProperty]
    public partial bool IsSelectionMode { get; set; }

    [ObservableProperty]
    public partial bool IsSelectionModeAvailable { get; set; }

    [ObservableProperty]
    public partial bool HasSelection { get; set; }

    [ObservableProperty]
    public partial int SelectedCount { get; set; }

    [ObservableProperty]
    public partial string SelectionText { get; set; } = string.Empty;

    public Task InitializeAsync() => ActivateAsync();

    public async Task ActivateAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_isActive)
        {
            return;
        }

        _isActive = true;
        CancellationTokenSource session = new();
        _activeSession = session;

        if (_usesAutomationRepositories)
        {
            ApplyProjection(isComplete: true);
            return;
        }

        if (!_initialized)
        {
            if (!TryGetActiveToken(out _accessToken))
            {
                Deactivate();
                return;
            }

            long accountId = _authService.AuthenticatedUser?.Id ?? _accountService.GetUser();
            _userId = GitHubAccountPartition.Resolve(_accessToken, accountId.ToString(CultureInfo.InvariantCulture));
            _isPublicProfilePreview = GitHubAuthenticationConstants.IsPublicAccessToken(_accessToken);
            _initialized = true;
        }

        SubscribeToRepositoryIndex();
        Stopwatch openDuration = Stopwatch.StartNew();
        IsLoadingRepositories = true;
        try
        {
            ApplySnapshot(_repositoryIndexService.GetSnapshot(_userId));
            if (!_cacheInitialized)
            {
                AccountRepositoryIndexSnapshot cached = await _repositoryIndexService.InitializeAsync(
                    _accessToken,
                    _userId,
                    session.Token);
                session.Token.ThrowIfCancellationRequested();
                _cacheInitialized = true;
                ApplySnapshot(cached);
            }
        }
        catch (OperationCanceledException) when (session.IsCancellationRequested)
        {
            return;
        }
        catch (GitHubAuthenticationException)
        {
            TrackOpened("auth_error", openDuration.Elapsed);
            _authService.SignOut();
            return;
        }
        catch (Exception ex)
        {
            ShowUnexpectedError(ex);
            TrackOpened("error", openDuration.Elapsed);
        }
        finally
        {
            if (IsCurrentSession(session))
            {
                IsLoadingRepositories = false;
                UpdateVisibility();
            }
        }

        if (!IsCurrentSession(session))
        {
            return;
        }

        if (!_openedTracked)
        {
            TrackOpened("success", openDuration.Elapsed);
        }

        _synchronizationTask = SynchronizeAndObserveAsync(session);
    }

    internal void SetAutomationRepositories(IReadOnlyList<GitHubRepository> repositories)
    {
        ArgumentNullException.ThrowIfNull(repositories);
        _initialized = true;
        _cacheInitialized = true;
        _usesAutomationRepositories = true;
        _userId = "automation";
        _accessToken = GitHubAuthenticationConstants.PublicAccessToken;
        _isPublicProfilePreview = true;
        _isIndexComplete = true;
        _indexedRepositories = repositories;
        StatusText = string.Empty;
        IsLoadingRepositories = false;
        IsSynchronizing = false;
        IsRetryVisible = false;
        ApplyProjection(isComplete: true);
    }

    public async Task RetryAsync()
    {
        CancellationTokenSource? session = _activeSession;
        if (session is null || string.IsNullOrWhiteSpace(_accessToken) || IsSynchronizing)
        {
            return;
        }

        _synchronizationTask = SynchronizeAndObserveAsync(session);
        await _synchronizationTask;
    }

    public void Deactivate()
    {
        if (!_isActive)
        {
            return;
        }

        _isActive = false;
        UnsubscribeFromRepositoryIndex();
        CancellationTokenSource? session = _activeSession;
        _activeSession = null;
        session?.Cancel();
        session?.Dispose();
        IsLoadingRepositories = false;
        IsSynchronizing = false;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Deactivate();
    }

    public void ActivateRepository(RepositoryLibraryViewItem? item)
    {
        if (item is null)
        {
            return;
        }

        if (IsSelectionMode)
        {
            item.Selected = !item.Selected;
            UpdateSelectionState();
            return;
        }

        _shell.OpenRepository(item.Repository);
        TrackAction("open_repository", "success");
    }

    public void OpenRepository(RepositoryLibraryViewItem? item)
    {
        if (item is not null)
        {
            _shell.OpenRepository(item.Repository);
            TrackAction("open_repository", "success");
        }
    }

    public Task PrefetchRepositoryAsync(RepositoryLibraryViewItem? item) =>
        item is null
            ? Task.CompletedTask
            : _shell.PrefetchRepositoryCodeAsync(
                item.Repository,
                _activeSession?.Token ?? CancellationToken.None);

    public Task PrefetchLikelyRepositoriesAsync(int count = 1)
    {
        CancellationToken cancellationToken = _activeSession?.Token ?? CancellationToken.None;
        return Task.WhenAll(Repositories
            .Take(Math.Max(0, count))
            .Select(item => _shell.PrefetchRepositoryCodeAsync(item.Repository, cancellationToken)));
    }

    public void OpenOwner(RepositoryLibraryViewItem? item)
    {
        if (item is not null && !string.IsNullOrWhiteSpace(item.Repository.Owner.Login))
        {
            _shell.OpenUserProfile(item.Repository.Owner.Login, "repositories");
            TrackAction("open_owner", "success");
        }
    }

    public void CopyRepositoryLink(RepositoryLibraryViewItem? item)
    {
        if (item is null)
        {
            return;
        }

        string url = string.IsNullOrWhiteSpace(item.Repository.HtmlUrl)
            ? $"https://github.com/{item.Repository.FullName}"
            : item.Repository.HtmlUrl;
        bool succeeded = PlatformHelper.CopyString(url);
        StatusText = succeeded
            ? GetString("RepoManage.LinkCopiedStatus", "Repository link copied.")
            : GetString("RepoManage.LinkCopyFailedStatus", "The repository link could not be copied.");
        TrackAction(
            TelemetryTaxonomy.Actions.CopyLink,
            succeeded ? TelemetryTaxonomy.Results.Success : TelemetryTaxonomy.Results.Error);
    }

    public void OpenNewRepository()
    {
        _shell.OpenNewRepositoryModal();
        TrackAction("new_repository", "opened");
    }

    public void ToggleSelectionMode()
    {
        if (!IsSelectionMode && !IsSelectionModeAvailable)
        {
            StatusText = GetString(
                "RepoManage.SelectionUnavailable",
                "No repositories in this view can be deleted with the current account permissions.");
            return;
        }

        IsSelectionMode = !IsSelectionMode;
        foreach (RepositoryLibraryViewItem repository in Repositories)
        {
            repository.IsSelectionModeVisible = IsSelectionMode;
            if (!IsSelectionMode)
            {
                repository.Selected = false;
            }
        }

        UpdateSelectionState();
        OnPropertyChanged(nameof(SelectButtonText));
        TrackAction(
            TelemetryTaxonomy.Actions.SelectionMode,
            IsSelectionMode ? TelemetryTaxonomy.Results.Enabled : TelemetryTaxonomy.Results.Disabled);
    }

    public void ClearSelection()
    {
        foreach (RepositoryLibraryViewItem repository in Repositories)
        {
            repository.Selected = false;
        }

        UpdateSelectionState();
    }

    public void SetRepositorySelected(RepositoryLibraryViewItem item, bool selected)
    {
        item.Selected = selected;
        UpdateSelectionState();
    }

    public IReadOnlyList<RepositoryLibraryViewItem> GetSelectedRepositories()
    {
        IReadOnlyList<RepositoryLibraryViewItem> selected = Repositories.Where(static repository => repository.Selected).ToArray();
        if (selected.Count == 0)
        {
            StatusText = GetString("RepoManage.SelectRepositoryPrompt", "Select at least one repository to delete.");
        }

        return selected;
    }

    public async Task<RepositoryDeletionResult?> DeleteSelectedAsync(IReadOnlyList<RepositoryLibraryViewItem> selectedRepositories)
    {
        if (selectedRepositories.Count == 0)
        {
            return RepositoryDeletionResult.Empty;
        }

        if (!TryGetActiveToken(out string token))
        {
            return null;
        }

        try
        {
            IsInteractionEnabled = false;
            IsDeletionProgressVisible = true;
            DeletionProgressValue = 0;
            DeletionProgressMaximum = selectedRepositories.Count;

            List<string> failures = [];
            List<long> deletedIds = [];
            foreach (RepositoryLibraryViewItem repositoryItem in selectedRepositories)
            {
                try
                {
                    await _gitHubClientService.DeleteRepositoryAsync(
                        token,
                        repositoryItem.Repository.Owner.Login,
                        repositoryItem.Repository.Name);
                    deletedIds.Add(repositoryItem.Repository.Id);
                }
                catch (GitHubAuthenticationException)
                {
                    _authService.SignOut();
                    return null;
                }
                catch (GitHubApiException ex)
                {
                    failures.Add($"{repositoryItem.Repository.FullName}: {JitHub.WinUI.Helpers.UserFacingError.For(ex, JitHub.WinUI.Helpers.UserFacingErrorKind.Action, "repository-delete")}");
                }
                catch (HttpRequestException)
                {
                    failures.Add($"{repositoryItem.Repository.FullName}: {GetString("RepoManage.DeleteFailureNetworkErrorShort", "network error")}");
                }
                catch (Exception ex)
                {
                    failures.Add($"{repositoryItem.Repository.FullName}: {JitHub.WinUI.Helpers.UserFacingError.For(ex, JitHub.WinUI.Helpers.UserFacingErrorKind.Action, "repository-delete")}");
                }
                finally
                {
                    DeletionProgressValue += 1;
                }
            }

            if (deletedIds.Count > 0)
            {
                CancellationToken cancellationToken = _activeSession?.Token ?? CancellationToken.None;
                await _repositoryIndexService.RemoveRepositoriesAsync(_userId, deletedIds, cancellationToken);
            }

            StatusText = failures.Count == 0
                ? GetString("RepoManage.DeleteSuccess", "Selected repositories were deleted.")
                : FormatString("RepoManage.DeleteFailureStatus", "{0} repositories could not be deleted.", failures.Count);
            TrackAction("delete", failures.Count == 0 ? "success" : "partial");
            return new RepositoryDeletionResult(selectedRepositories.Count, deletedIds, failures);
        }
        finally
        {
            IsInteractionEnabled = true;
            IsDeletionProgressVisible = false;
            if (Repositories.Count == 0 || !Repositories.Any(static item => item.Selected))
            {
                IsSelectionMode = false;
                OnPropertyChanged(nameof(SelectButtonText));
            }

            UpdateSelectionState();
        }
    }

    public async Task<bool> EnsureDeletionScopeAsync()
    {
        try
        {
            if (await _authService.EnsureScopesAsync("delete_repo"))
            {
                return true;
            }

            StatusText = GetString(
                "RepoManage.DeleteAuthorizationOpened",
                "GitHub authorization opened. Grant repository deletion access, then retry.");
            return false;
        }
        catch (HttpRequestException)
        {
            StatusText = GetString(
                "RepoManage.DeleteAuthorizationNetworkError",
                "JitHub could not verify repository deletion access. Check your connection and try again.");
            return false;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Repository deletion authorization failed: {ex}");
            StatusText = GetString(
                "RepoManage.DeleteAuthorizationFailure",
                "JitHub could not request repository deletion access. Try again.");
            return false;
        }
    }

    public string FormatDeleteDialogContent(IReadOnlyList<RepositoryLibraryViewItem> selectedRepositories)
    {
        if (selectedRepositories.Count == 1)
        {
            return FormatString(
                "RepoManage.DeleteSingleDialogContent",
                "Delete {0}? This repository and all of its contents will be permanently removed. This cannot be undone.",
                selectedRepositories[0].Repository.FullName);
        }

        string names = string.Join(", ", selectedRepositories.Take(3).Select(static item => item.Repository.FullName));
        if (selectedRepositories.Count > 3)
        {
            names += FormatString("RepoManage.DeleteMoreNames", " and {0} more", selectedRepositories.Count - 3);
        }

        return FormatString(
            "RepoManage.DeleteDialogContent",
            "Delete {0} repositories ({1})? Their contents will be permanently removed. This cannot be undone.",
            selectedRepositories.Count,
            names);
    }

    public void ShowUnexpectedError(Exception exception)
    {
        Debug.WriteLine($"Repository management failed: {exception}");
        StatusText = GetString(
            "RepoManage.UnexpectedErrorStatus",
            "JitHub could not manage repositories. Try again.");
        IsInteractionEnabled = true;
        IsDeletionProgressVisible = false;
        IsLoadingRepositories = false;
        IsRetryVisible = true;
        UpdateVisibility();
    }

    partial void OnSearchTextChanged(string value)
    {
        if (_projectionReady)
        {
            ApplyProjection();
            TrackAction("filter_changed", "success");
        }
    }

    partial void OnStatusTextChanged(string value) => OnPropertyChanged(nameof(IsStatusVisible));

    partial void OnSelectedFilterOptionChanged(RepositoryFilterOption value)
    {
        if (_projectionReady)
        {
            ApplyProjection();
            TrackAction("filter_changed", "success");
        }
    }

    partial void OnSelectedSortOptionChanged(RepositorySortOption value)
    {
        if (_projectionReady)
        {
            ApplyProjection();
            TrackAction("sort_changed", "success");
        }
    }

    private async Task SynchronizeAndObserveAsync(CancellationTokenSource session)
    {
        try
        {
            IsSynchronizing = true;
            IsRetryVisible = false;
            await _repositoryIndexService.SynchronizeAsync(_accessToken, _userId, session.Token);
        }
        catch (OperationCanceledException) when (session.IsCancellationRequested)
        {
        }
        catch (GitHubAuthenticationException)
        {
            _authService.SignOut();
        }
        catch (Exception ex)
        {
            StatusText = Repositories.Count > 0
                ? GetString("RepoManage.SyncFailureCachedStatus", "Some repositories may be out of date.")
                : JitHub.WinUI.Helpers.UserFacingError.For(
                    ex,
                    JitHub.WinUI.Helpers.UserFacingErrorKind.Loading,
                    "repository-library");
            IsRetryVisible = true;
            UpdateVisibility();
        }
        finally
        {
            if (IsCurrentSession(session))
            {
                IsSynchronizing = false;
            }
        }
    }

    private bool IsCurrentSession(CancellationTokenSource session) =>
        _isActive && ReferenceEquals(_activeSession, session) && !session.IsCancellationRequested;

    private void SubscribeToRepositoryIndex()
    {
        if (_isIndexSubscribed)
        {
            return;
        }

        _repositoryIndexService.Changed += RepositoryIndexService_Changed;
        _isIndexSubscribed = true;
    }

    private void UnsubscribeFromRepositoryIndex()
    {
        if (!_isIndexSubscribed)
        {
            return;
        }

        _repositoryIndexService.Changed -= RepositoryIndexService_Changed;
        _isIndexSubscribed = false;
    }

    private void RepositoryIndexService_Changed(object? sender, AccountRepositoryIndexChangedEventArgs e)
    {
        if (!string.Equals(e.Snapshot.UserId, _userId, StringComparison.Ordinal))
        {
            return;
        }

        if (_dispatcherQueue.HasThreadAccess)
        {
            ApplySnapshot(e.Snapshot);
        }
        else
        {
            _ = _dispatcherQueue.TryEnqueue(() => ApplySnapshot(e.Snapshot));
        }
    }

    private void ApplySnapshot(AccountRepositoryIndexSnapshot snapshot)
    {
        if (Equals(_lastAppliedSnapshot, snapshot))
        {
            return;
        }

        _lastAppliedSnapshot = snapshot;
        _indexedRepositories = snapshot.Repositories;
        _isIndexComplete = snapshot.IsComplete;
        IsSynchronizing = snapshot.IsSynchronizing;
        IsRetryVisible = !string.IsNullOrWhiteSpace(snapshot.ErrorMessage);
        StatusText = snapshot.ErrorMessage is null
            ? string.Empty
            : Repositories.Count > 0
                ? GetString("RepoManage.SyncFailureCachedStatus", "Some repositories may be out of date.")
                : snapshot.ErrorMessage;
        ApplyProjection(snapshot.IsComplete);
    }

    private void ApplyProjection(bool? isComplete = null)
    {
        RepositoryLibraryFilter filter = SelectedFilterOption?.Value ?? RepositoryLibraryFilter.All;
        RepositoryLibrarySort sort = SelectedSortOption?.Value ?? RepositoryLibrarySort.RecentlyUpdated;
        IReadOnlyList<GitHubRepository> projected = RepositoryLibraryProjection.Apply(
            _indexedRepositories,
            SearchText,
            filter,
            sort);
        ProjectionChanging?.Invoke(this, EventArgs.Empty);
        Repositories.ApplySnapshot(
            projected,
            RepositoryLibraryProjection.RepositoryKey,
            static item => item.Key,
            repository => new RepositoryLibraryViewItem(repository)
            {
                IsSelectionModeVisible = IsSelectionMode
            },
            static (item, repository) => item.Update(repository));
        ProjectionChanged?.Invoke(this, EventArgs.Empty);

        bool complete = isComplete ?? _isIndexComplete;
        _isIndexComplete = complete;
        bool isFiltered = filter != RepositoryLibraryFilter.All || !string.IsNullOrWhiteSpace(SearchText);
        string scope = _isPublicProfilePreview
            ? GetString("RepoManage.PublicProfileScope", "preview repositories")
            : complete
                ? GetString("RepoManage.AccountScope", "account repositories")
                : GetString("RepoManage.IndexedScope", "indexed repositories");
        ResultCountText = isFiltered
            ? FormatString("RepoManage.FilteredCount", "{0} of {1} {2}", Repositories.Count, _indexedRepositories.Count, scope)
            : FormatString("RepoManage.ScopedCount", "{0} {1}", _indexedRepositories.Count, scope);
        IsSelectionModeAvailable = Repositories.Any(static repository => repository.CanDeleteRepository);
        if (IsSelectionMode && !IsSelectionModeAvailable)
        {
            IsSelectionMode = false;
            foreach (RepositoryLibraryViewItem repository in Repositories)
            {
                repository.IsSelectionModeVisible = false;
                repository.Selected = false;
            }

            OnPropertyChanged(nameof(SelectButtonText));
        }
        UpdateSelectionState();
        UpdateVisibility();
    }

    private void UpdateSelectionState()
    {
        SelectedCount = Repositories.Count(static repository => repository.Selected);
        HasSelection = SelectedCount > 0;
        SelectionText = SelectedCount == 1
            ? GetString("RepoManage.OneSelected", "1 selected")
            : FormatString("RepoManage.ManySelected", "{0} selected", SelectedCount);
        SelectionStateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void UpdateVisibility()
    {
        AreRepositoriesVisible = Repositories.Count > 0;
        AccountRepositoryIndexSnapshot snapshot = string.IsNullOrWhiteSpace(_userId)
            ? AccountRepositoryIndexSnapshot.Empty("pending")
            : _repositoryIndexService.GetSnapshot(_userId);
        IsEmptyStateVisible = Repositories.Count == 0 &&
            !IsLoadingRepositories &&
            (snapshot.IsComplete || !string.IsNullOrWhiteSpace(snapshot.ErrorMessage));
        if (IsEmptyStateVisible && _indexedRepositories.Count > 0)
        {
            EmptyStateTitle = GetString("RepoManage.NoMatchesTitle", "No repositories match");
            EmptyStateDescription = GetString("RepoManage.NoMatchesDescription", "Try a different search or filter.");
        }
        else
        {
            EmptyStateTitle = GetString("RepoManage.EmptyStateTitle", "No account repositories");
            EmptyStateDescription = GetString(
                "RepoManage.EmptyStateDescription",
                "Repositories this account owns or can access will appear here.");
        }
    }

    private bool TryGetActiveToken(out string token)
    {
        try
        {
            long userId = _authService.AuthenticatedUser?.Id ?? _accountService.GetUser();
            token = _authService.GetToken(userId) ?? string.Empty;
        }
        catch (Exception ex)
        {
            token = string.Empty;
            ShowUnexpectedError(ex);
            return false;
        }

        if (!string.IsNullOrWhiteSpace(token))
        {
            return true;
        }

        _authService.SignOut();
        return false;
    }

    private void TrackAction(string action, string result)
    {
        _telemetry.TrackEvent("repositories.action.executed", new Dictionary<string, string?>
        {
            ["action"] = action,
            ["result"] = result,
            ["source"] = "repository_library"
        });
    }

    private void TrackOpened(string result, TimeSpan duration)
    {
        if (_openedTracked)
        {
            return;
        }

        _openedTracked = true;
        _telemetry.TrackEvent("repositories.opened", new Dictionary<string, string?>
        {
            ["source"] = "shell",
            ["cache_state"] = Repositories.Count > 0 ? "cached" : "miss",
            ["result"] = result,
            ["duration_bucket"] = TelemetrySanitizer.CreateDurationBucket(duration)
        });
    }
}

public sealed class RepositoryFilterOption
{
    public RepositoryFilterOption()
    {
    }

    public RepositoryFilterOption(RepositoryLibraryFilter value, string label)
    {
        Value = value;
        Label = label;
    }

    public RepositoryLibraryFilter Value { get; set; }

    public string Label { get; set; } = string.Empty;

    public override string ToString() => Label;
}

public sealed class RepositorySortOption
{
    public RepositorySortOption()
    {
    }

    public RepositorySortOption(RepositoryLibrarySort value, string label)
    {
        Value = value;
        Label = label;
    }

    public RepositoryLibrarySort Value { get; set; }

    public string Label { get; set; } = string.Empty;

    public override string ToString() => Label;
}

[WinRT.GeneratedBindableCustomProperty]
public sealed partial class RepositoryLibraryViewItem : ObservableObject
{
    public RepositoryLibraryViewItem(GitHubRepository repository)
    {
        Repository = repository;
    }

    [ObservableProperty]
    public partial GitHubRepository Repository { get; set; }

    [ObservableProperty]
    public partial bool Selected { get; set; }

    [ObservableProperty]
    public partial bool IsSelectionModeVisible { get; set; }

    public string Key => RepositoryLibraryProjection.RepositoryKey(Repository);

    public string AutomationId => $"RepositoryLibraryRow_{Repository.Id}";

    public string AutomationName => $"{Repository.FullName}, {KindText}";

    public string KindText => Repository.Archived
        ? "Archived"
        : Repository.Fork
            ? "Fork"
            : Repository.Private
                ? "Private"
                : "Public";

    public string LanguageText => string.IsNullOrWhiteSpace(Repository.Language) ? "No language" : Repository.Language;

    public string UpdatedText => Repository.UpdatedAt is { } updated
        ? $"Updated {updated.LocalDateTime:d}"
        : string.Empty;

    public string StarText => Repository.StargazersCount.ToString("N0", CultureInfo.CurrentCulture);

    public string SelectionAutomationId => $"RepositoryLibrarySelect_{Repository.Id}";

    public string SelectionAutomationName => $"Select {Repository.FullName}";

    public bool CanDeleteRepository => Repository.Permissions?.Admin == true;

    public bool Update(GitHubRepository repository)
    {
        if (ReferenceEquals(Repository, repository))
        {
            return false;
        }

        Repository = repository;
        return true;
    }

    partial void OnRepositoryChanged(GitHubRepository value)
    {
        OnPropertyChanged(nameof(Key));
        OnPropertyChanged(nameof(AutomationId));
        OnPropertyChanged(nameof(AutomationName));
        OnPropertyChanged(nameof(KindText));
        OnPropertyChanged(nameof(LanguageText));
        OnPropertyChanged(nameof(UpdatedText));
        OnPropertyChanged(nameof(StarText));
        OnPropertyChanged(nameof(SelectionAutomationId));
        OnPropertyChanged(nameof(SelectionAutomationName));
        OnPropertyChanged(nameof(CanDeleteRepository));
    }
}

public sealed record RepositoryDeletionResult(
    int AttemptedCount,
    IReadOnlyList<long> DeletedRepositoryIds,
    IReadOnlyList<string> Failures)
{
    public static RepositoryDeletionResult Empty { get; } = new(0, [], []);

    public bool HasFailures => Failures.Count > 0;
}
