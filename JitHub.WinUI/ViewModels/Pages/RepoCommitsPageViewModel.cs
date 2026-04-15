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

namespace JitHub.WinUI.ViewModels.Pages;

public sealed partial class RepoCommitsPageViewModel : ViewModelBase
{
    private readonly IAuthService _authService;
    private readonly IAccountService _accountService;
    private readonly IGitHubClientService _gitHubClientService;
    private CommitPageNavArg? _navArg;
    private int _commitDetailsLoadVersion;

    public RepoCommitsPageViewModel()
    {
        _authService = GetService<IAuthService>();
        _accountService = GetService<IAccountService>();
        _gitHubClientService = GetService<IGitHubClientService>();
        ResetCommitDetails();
    }

    public ObservableCollection<GitHubCommit> Commits { get; } = [];

    public ObservableCollection<GitHubCommitFile> CommitFiles { get; } = [];

    public string ReloadButtonText => GetString("Common.ReloadButton", "Reload");

    public string OpenOnGitHubButtonText => GetString("RepoCommits.OpenOnGitHubButton", "Open on GitHub");

    public string OpenCodeButtonText => GetString("RepoCommits.OpenCodeButton", "Open code");

    public CommitPageNavArg? NavigationArgs => _navArg;

    [ObservableProperty]
    public partial string StatusText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string CommitTitleText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string CommitMetaText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string CommitStatsText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string CommitFilesText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string CommitBodyText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsCommitDetailsLoading { get; set; }

    [ObservableProperty]
    public partial bool AreCommitActionsEnabled { get; set; }

    [ObservableProperty]
    public partial GitHubCommit? SelectedCommit { get; set; }

    public async Task InitializeAsync(CommitPageNavArg? navArg)
    {
        _navArg = navArg;
        Commits.Clear();
        SelectedCommit = null;

        if (navArg is null)
        {
            StatusText = GetString(
                "RepoCommits.InvalidNavigation",
                "JitHub could not determine which repository commits to load.");
            ResetCommitDetails();
            return;
        }

        await LoadCommitsAsync();
    }

    public async Task LoadCommitsAsync()
    {
        if (_navArg is null)
        {
            StatusText = GetString(
                "RepoCommits.InvalidNavigation",
                "JitHub could not determine which repository commits to load.");
            return;
        }

        string? token = GetActiveToken();
        if (string.IsNullOrWhiteSpace(token))
        {
            _authService.SignOut();
            return;
        }

        string gitRef = !_navArg.NoBranch
            ? _navArg.Branch!
            : !_navArg.NoRef
                ? _navArg.GitRef!
                : _navArg.Repo.DefaultBranch;
        StatusText = FormatString("RepoCommits.LoadingStatus", "Loading commits for {0}...", gitRef);
        Commits.Clear();
        SelectedCommit = null;
        ResetCommitDetails();

        try
        {
            IReadOnlyList<GitHubCommit> commits = await _gitHubClientService.GetCommitsAsync(
                token,
                _navArg.Repo.Owner.Login,
                _navArg.Repo.Name,
                gitRef,
                50);

            foreach (GitHubCommit commit in commits)
            {
                Commits.Add(commit);
            }

            StatusText = Commits.Count == 0
                ? FormatString("RepoCommits.NoCommitsStatus", "No commits are available for {0}.", gitRef)
                : FormatString("RepoCommits.CommitsStatus", "Showing {0} commits for {1}.", Commits.Count, gitRef);

            GitHubCommit? commitToSelect = null;
            if (!_navArg.NoRef)
            {
                commitToSelect = Commits.FirstOrDefault(commit =>
                    string.Equals(commit.Sha, _navArg.GitRef, StringComparison.OrdinalIgnoreCase));
                if (commitToSelect is null)
                {
                    commitToSelect = await _gitHubClientService.GetCommitAsync(
                        token,
                        _navArg.Repo.Owner.Login,
                        _navArg.Repo.Name,
                        _navArg.GitRef!);
                    if (Commits.All(existingCommit => !string.Equals(existingCommit.Sha, commitToSelect.Sha, StringComparison.OrdinalIgnoreCase)))
                    {
                        Commits.Insert(0, commitToSelect);
                    }
                }
            }

            commitToSelect ??= Commits.Count > 0 ? Commits[0] : null;
            SelectedCommit = commitToSelect;
        }
        catch (GitHubAuthenticationException)
        {
            _authService.SignOut();
        }
        catch (GitHubApiException ex)
        {
            StatusText = ex.Message;
        }
        catch (HttpRequestException)
        {
            StatusText = GetString("RepoCommits.NetworkError", "JitHub could not reach GitHub to load commits.");
        }
    }

    partial void OnSelectedCommitChanged(GitHubCommit? value)
    {
        _ = SelectCommitAsync(value);
    }

    private async Task SelectCommitAsync(GitHubCommit? commit)
    {
        ShowCommit(commit);
        if (commit is null || _navArg is null || commit.Files.Length > 0)
        {
            return;
        }

        string? token = GetActiveToken();
        if (string.IsNullOrWhiteSpace(token))
        {
            _authService.SignOut();
            return;
        }

        int loadVersion = ++_commitDetailsLoadVersion;
        IsCommitDetailsLoading = true;
        CommitFilesText = GetString("RepoCommits.ChangedFilesLoading", "Loading changed files...");

        try
        {
            GitHubCommit detailedCommit = await _gitHubClientService.GetCommitAsync(
                token,
                _navArg.Repo.Owner.Login,
                _navArg.Repo.Name,
                commit.Sha);
            commit.Stats = detailedCommit.Stats;
            commit.Files = detailedCommit.Files;

            if (loadVersion == _commitDetailsLoadVersion
                && SelectedCommit is not null
                && string.Equals(SelectedCommit.Sha, commit.Sha, StringComparison.OrdinalIgnoreCase))
            {
                ShowCommit(commit);
            }
        }
        catch (GitHubAuthenticationException)
        {
            _authService.SignOut();
        }
        catch (GitHubApiException ex)
        {
            if (loadVersion == _commitDetailsLoadVersion
                && SelectedCommit is not null
                && string.Equals(SelectedCommit.Sha, commit.Sha, StringComparison.OrdinalIgnoreCase))
            {
                CommitFilesText = GetString("RepoCommits.ChangedFilesUnavailable", "Changed files unavailable");
                StatusText = ex.Message;
            }
        }
        catch (HttpRequestException)
        {
            if (loadVersion == _commitDetailsLoadVersion
                && SelectedCommit is not null
                && string.Equals(SelectedCommit.Sha, commit.Sha, StringComparison.OrdinalIgnoreCase))
            {
                CommitFilesText = GetString("RepoCommits.ChangedFilesUnavailable", "Changed files unavailable");
                StatusText = GetString(
                    "RepoCommits.CommitDetailsNetworkError",
                    "JitHub could not reach GitHub to load commit details.");
            }
        }
        finally
        {
            if (loadVersion == _commitDetailsLoadVersion
                && SelectedCommit is not null
                && string.Equals(SelectedCommit.Sha, commit.Sha, StringComparison.OrdinalIgnoreCase))
            {
                IsCommitDetailsLoading = false;
            }
        }
    }

    private void ShowCommit(GitHubCommit? commit)
    {
        AreCommitActionsEnabled = commit is not null;

        if (commit is null)
        {
            ResetCommitDetails();
            return;
        }

        CommitTitleText = commit.SummaryMessage;
        CommitMetaText = $"{commit.ShortSha}  •  {commit.AuthorDisplayName}  •  {commit.TimestampDisplayText}";
        CommitStatsText = commit.Stats?.SummaryText ?? string.Empty;
        CommitFilesText = commit.Files.Length > 0 ? FormatChangedFilesCount(commit.Files.Length) : string.Empty;
        CommitBodyText = string.IsNullOrWhiteSpace(commit.Commit.Message)
            ? GetString("RepoCommits.NoCommitMessage", "(No commit message)")
            : commit.Commit.Message;
        CommitFiles.Clear();
        foreach (GitHubCommitFile file in commit.Files)
        {
            CommitFiles.Add(file);
        }
    }

    private string FormatChangedFilesCount(int fileCount)
    {
        return fileCount == 1
            ? GetString("RepoCommits.ChangedFilesSingle", "1 changed file")
            : FormatString("RepoCommits.ChangedFilesPlural", "{0} changed files", fileCount);
    }

    private string? GetActiveToken()
    {
        long userId = _authService.AuthenticatedUser?.Id ?? _accountService.GetUser();
        return _authService.GetToken(userId);
    }

    private void ResetCommitDetails()
    {
        CommitTitleText = GetString("RepoCommits.SelectCommitTitle", "Select a commit");
        CommitMetaText = GetString("RepoCommits.SelectCommitDescription", "Choose a commit to inspect its details.");
        CommitStatsText = string.Empty;
        CommitFilesText = string.Empty;
        CommitBodyText = string.Empty;
        CommitFiles.Clear();
        IsCommitDetailsLoading = false;
        AreCommitActionsEnabled = false;
    }
}
