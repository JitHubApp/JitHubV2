using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using JitHub.Models.GitHub;
using JitHub.Models.NavArgs;
using JitHub.Services;
using Microsoft.UI.Xaml.Controls;
using Windows.System;

namespace JitHub.WinUI.ViewModels.Pages;

public sealed partial class RepoDetailPageViewModel : ViewModelBase
{
    private readonly IAuthService _authService;
    private readonly IAccountService _accountService;
    private readonly IGitHubClientService _gitHubClientService;
    private GitHubRepository? _repository;
    private bool _isStarred;
    private bool _isWatching;
    private bool _repositoryActionInProgress;

    public RepoDetailPageViewModel()
    {
        _authService = GetService<IAuthService>();
        _accountService = GetService<IAccountService>();
        _gitHubClientService = GetService<IGitHubClientService>();

        RepositoryName = GetString("RepoDetail.DefaultTitle", "Repository");
        RepositoryDescription = GetString("RepoDetail.LoadingStatus", "Loading repository details...");
        ApplyRepositoryActionState();
    }

    public ObservableCollection<GitHubBranch> Branches { get; } = [];

    public string RepositoryFullName => _repository?.FullName ?? RepositoryName;

    public GitHubRepository? Repository => _repository;

    public string BranchLabel => GetString("RepoDetail.BranchLabel", "Branch");

    public string OpenOnGitHubButtonText => GetString("RepoDetail.OpenOnGitHubButton", "Open on GitHub");

    public string CodeTabText => GetString("RepoDetail.CodeTab", "Code");

    public string IssuesTabText => GetString("RepoDetail.IssuesTab", "Issues");

    public string PullRequestsTabText => GetString("RepoDetail.PullRequestsTab", "Pull Requests");

    public string CommitsTabText => GetString("RepoDetail.CommitsTab", "Commits");

    [ObservableProperty]
    public partial string RepositoryName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string RepositoryDescription { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string RepositoryMetaText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string RepositoryActivityText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial GitHubBranch? SelectedBranch { get; set; }

    [ObservableProperty]
    public partial string WatchButtonText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string StarButtonText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ForkButtonText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsRepositoryActionsEnabled { get; set; }

    [ObservableProperty]
    public partial bool IsOpenOnGitHubEnabled { get; set; }

    [ObservableProperty]
    public partial string StatusMessage { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsStatusOpen { get; set; }

    [ObservableProperty]
    public partial InfoBarSeverity StatusSeverity { get; set; } = InfoBarSeverity.Informational;

    public void ShowInvalidNavigation()
    {
        ShowStatus(
            GetString("RepoDetail.InvalidNavigation", "JitHub could not determine which repository to open."),
            InfoBarSeverity.Error);
    }

    public async Task<bool> LoadRepositoryAsync(RepoDetailPageArgs args)
    {
        if (args.Repo is null)
        {
            ShowInvalidNavigation();
            return false;
        }

        string? token = GetActiveToken();
        if (string.IsNullOrWhiteSpace(token))
        {
            _authService.SignOut();
            return false;
        }

        try
        {
            ShowStatus(
                GetString("RepoDetail.LoadingStatus", "Loading repository details..."),
                InfoBarSeverity.Informational);
            _repository = await _gitHubClientService.GetRepositoryAsync(token, args.Repo.Owner.Login, args.Repo.Name);
            PopulateRepositoryHeader(_repository);
            await LoadBranchesAsync(token, args);
            await LoadRepositoryActionsAsync(token);
            IsStatusOpen = false;
            return true;
        }
        catch (GitHubAuthenticationException)
        {
            _authService.SignOut();
        }
        catch (GitHubApiException ex)
        {
            ShowStatus(ex.Message, InfoBarSeverity.Error);
        }
        catch (HttpRequestException)
        {
            ShowStatus(
                GetString("RepoDetail.LoadNetworkError", "JitHub could not reach GitHub to load this repository."),
                InfoBarSeverity.Error);
        }

        return false;
    }

    public string GetSelectedBranch()
    {
        if (SelectedBranch is { Name.Length: > 0 } branch)
        {
            return branch.Name;
        }

        return _repository?.DefaultBranch ?? "main";
    }

    public async Task OpenOnGitHubAsync()
    {
        if (_repository is null || string.IsNullOrWhiteSpace(_repository.HtmlUrl))
        {
            return;
        }

        await Launcher.LaunchUriAsync(new Uri(_repository.HtmlUrl));
    }

    public async Task ToggleWatchAsync()
    {
        if (_repository is null || !TryGetActiveToken(out string token))
        {
            return;
        }

        try
        {
            _repositoryActionInProgress = true;
            ApplyRepositoryActionState();
            ShowStatus(
                _isWatching
                    ? GetString("RepoDetail.UnwatchingStatus", "Removing repository watch...")
                    : GetString("RepoDetail.WatchingStatus", "Watching repository..."),
                InfoBarSeverity.Informational);

            if (_isWatching)
            {
                await _gitHubClientService.UnwatchRepositoryAsync(token, _repository.Owner.Login, _repository.Name);
            }
            else
            {
                await _gitHubClientService.WatchRepositoryAsync(token, _repository.Owner.Login, _repository.Name);
            }

            _repository = await _gitHubClientService.GetRepositoryAsync(token, _repository.Owner.Login, _repository.Name);
            PopulateRepositoryHeader(_repository);
            await LoadRepositoryActionsAsync(token);
            ShowStatus(
                _isWatching
                    ? GetString("RepoDetail.WatchEnabledStatus", "JitHub is now watching this repository.")
                    : GetString("RepoDetail.WatchDisabledStatus", "JitHub is no longer watching this repository."),
                InfoBarSeverity.Success);
        }
        catch (GitHubAuthenticationException)
        {
            _authService.SignOut();
        }
        catch (GitHubApiException ex)
        {
            ShowStatus(ex.Message, InfoBarSeverity.Error);
        }
        catch (HttpRequestException)
        {
            ShowStatus(
                GetString("RepoDetail.WatchNetworkError", "JitHub could not reach GitHub to update the watch state."),
                InfoBarSeverity.Error);
        }
        finally
        {
            _repositoryActionInProgress = false;
            ApplyRepositoryActionState();
        }
    }

    public async Task ToggleStarAsync()
    {
        if (_repository is null || !TryGetActiveToken(out string token))
        {
            return;
        }

        try
        {
            _repositoryActionInProgress = true;
            ApplyRepositoryActionState();
            ShowStatus(
                _isStarred
                    ? GetString("RepoDetail.UnstarringStatus", "Removing repository star...")
                    : GetString("RepoDetail.StarringStatus", "Starring repository..."),
                InfoBarSeverity.Informational);

            if (_isStarred)
            {
                await _gitHubClientService.UnstarRepositoryAsync(token, _repository.Owner.Login, _repository.Name);
            }
            else
            {
                await _gitHubClientService.StarRepositoryAsync(token, _repository.Owner.Login, _repository.Name);
            }

            _repository = await _gitHubClientService.GetRepositoryAsync(token, _repository.Owner.Login, _repository.Name);
            PopulateRepositoryHeader(_repository);
            await LoadRepositoryActionsAsync(token);
            ShowStatus(
                _isStarred
                    ? GetString("RepoDetail.StarEnabledStatus", "JitHub starred this repository.")
                    : GetString("RepoDetail.StarDisabledStatus", "JitHub removed the repository star."),
                InfoBarSeverity.Success);
        }
        catch (GitHubAuthenticationException)
        {
            _authService.SignOut();
        }
        catch (GitHubApiException ex)
        {
            ShowStatus(ex.Message, InfoBarSeverity.Error);
        }
        catch (HttpRequestException)
        {
            ShowStatus(
                GetString("RepoDetail.StarNetworkError", "JitHub could not reach GitHub to update the star state."),
                InfoBarSeverity.Error);
        }
        finally
        {
            _repositoryActionInProgress = false;
            ApplyRepositoryActionState();
        }
    }

    public async Task<RepoDetailPageArgs?> ForkAsync()
    {
        if (_repository is null || !TryGetActiveToken(out string token))
        {
            return null;
        }

        try
        {
            _repositoryActionInProgress = true;
            ApplyRepositoryActionState();
            ShowStatus(
                GetString("RepoDetail.ForkingStatus", "Forking repository..."),
                InfoBarSeverity.Informational);
            GitHubRepository forkedRepository =
                await _gitHubClientService.ForkRepositoryAsync(token, _repository.Owner.Login, _repository.Name);
            GitHubRepository readyFork = await WaitForForkAsync(token, forkedRepository);
            ShowStatus(
                FormatString("RepoDetail.ForkOpenedStatus", "Opened fork {0}.", readyFork.FullName),
                InfoBarSeverity.Success);
            return new RepoDetailPageArgs(RepoPageType.CodePage, readyFork);
        }
        catch (GitHubAuthenticationException)
        {
            _authService.SignOut();
        }
        catch (GitHubApiException ex)
        {
            ShowStatus(ex.Message, InfoBarSeverity.Error);
        }
        catch (HttpRequestException)
        {
            ShowStatus(
                GetString("RepoDetail.ForkNetworkError", "JitHub could not reach GitHub to fork this repository."),
                InfoBarSeverity.Error);
        }
        finally
        {
            _repositoryActionInProgress = false;
            ApplyRepositoryActionState();
        }

        return null;
    }

    private async Task LoadBranchesAsync(string token, RepoDetailPageArgs args)
    {
        if (_repository is null)
        {
            return;
        }

        IReadOnlyList<GitHubBranch> branches =
            await _gitHubClientService.GetBranchesAsync(token, _repository.Owner.Login, _repository.Name);

        Branches.Clear();
        foreach (GitHubBranch branch in branches)
        {
            Branches.Add(branch);
        }

        string targetBranch = _repository.DefaultBranch;
        if (args.Ref is CodeViewerNavArg codeArg && codeArg.IsBranch && !string.IsNullOrWhiteSpace(codeArg.Branch))
        {
            targetBranch = codeArg.Branch;
        }
        else if (args.Ref is CommitPageNavArg commitArg && !string.IsNullOrWhiteSpace(commitArg.Branch))
        {
            targetBranch = commitArg.Branch!;
        }

        SelectedBranch = Branches.FirstOrDefault(branch => string.Equals(branch.Name, targetBranch, StringComparison.OrdinalIgnoreCase))
            ?? Branches.FirstOrDefault();
    }

    private async Task LoadRepositoryActionsAsync(string token)
    {
        if (_repository is null)
        {
            return;
        }

        Task<bool> starredTask = _gitHubClientService.IsRepositoryStarredAsync(token, _repository.Owner.Login, _repository.Name);
        Task<bool> watchedTask = _gitHubClientService.IsRepositoryWatchedAsync(token, _repository.Owner.Login, _repository.Name);
        await Task.WhenAll(starredTask, watchedTask);
        _isStarred = await starredTask;
        _isWatching = await watchedTask;
        ApplyRepositoryActionState();
    }

    private string? GetActiveToken()
    {
        long userId = _authService.AuthenticatedUser?.Id ?? _accountService.GetUser();
        return _authService.GetToken(userId);
    }

    private bool TryGetActiveToken(out string token)
    {
        token = GetActiveToken() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(token))
        {
            return true;
        }

        _authService.SignOut();
        return false;
    }

    private async Task<GitHubRepository> WaitForForkAsync(string token, GitHubRepository repository)
    {
        GitHubRepository currentRepository = repository;
        for (int attempt = 0; attempt < 20; attempt++)
        {
            currentRepository = await _gitHubClientService.GetRepositoryAsync(
                token,
                currentRepository.Owner.Login,
                currentRepository.Name);
            IReadOnlyList<GitHubBranch> branches = await _gitHubClientService.GetBranchesAsync(
                token,
                currentRepository.Owner.Login,
                currentRepository.Name);
            if (branches.Count > 0)
            {
                return currentRepository;
            }

            await Task.Delay(500);
        }

        return currentRepository;
    }

    private void PopulateRepositoryHeader(GitHubRepository repository)
    {
        RepositoryName = repository.FullName;
        RepositoryDescription = string.IsNullOrWhiteSpace(repository.Description)
            ? GetString("RepoDetail.NoDescription", "No repository description is available.")
            : repository.Description;

        string language = string.IsNullOrWhiteSpace(repository.Language)
            ? GetString("RepoDetail.UnknownLanguage", "Unknown language")
            : repository.Language;
        RepositoryMetaText = FormatString(
            "RepoDetail.RepositoryMetaFormat",
            "{0} stars  •  {1} forks  •  {2} open issues  •  {3}",
            repository.StargazersCount,
            repository.ForksCount,
            repository.OpenIssuesCount,
            language);
        RepositoryActivityText = repository.UpdatedAt is null
            ? string.Empty
            : FormatString(
                "RepoDetail.RepositoryUpdatedFormat",
                "Updated {0:g}",
                repository.UpdatedAt.Value.LocalDateTime);
        IsOpenOnGitHubEnabled = !string.IsNullOrWhiteSpace(repository.HtmlUrl);
        ApplyRepositoryActionState();
    }

    private void ApplyRepositoryActionState()
    {
        bool enabled = _repository is not null && !_repositoryActionInProgress;
        IsRepositoryActionsEnabled = enabled;

        if (_repository is null)
        {
            IsOpenOnGitHubEnabled = false;
            WatchButtonText = GetString("RepoDetail.WatchButton", "Watch");
            StarButtonText = GetString("RepoDetail.StarButton", "Star");
            ForkButtonText = GetString("RepoDetail.ForkButton", "Fork");
            return;
        }

        WatchButtonText = _isWatching
            ? FormatString("RepoDetail.UnwatchButtonFormat", "Unwatch ({0})", _repository.WatchersCount)
            : FormatString("RepoDetail.WatchButtonFormat", "Watch ({0})", _repository.WatchersCount);
        StarButtonText = _isStarred
            ? FormatString("RepoDetail.UnstarButtonFormat", "Unstar ({0})", _repository.StargazersCount)
            : FormatString("RepoDetail.StarButtonFormat", "Star ({0})", _repository.StargazersCount);
        ForkButtonText = FormatString("RepoDetail.ForkButtonFormat", "Fork ({0})", _repository.ForksCount);
    }

    private void ShowStatus(string message, InfoBarSeverity severity)
    {
        StatusMessage = message;
        StatusSeverity = severity;
        IsStatusOpen = true;
    }
}
