using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Net.Http;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using JitHub.Models.GitHub;
using JitHub.Services;

namespace JitHub.WinUI.ViewModels.Pages;

public sealed partial class RepoSearchResultPageViewModel : ViewModelBase
{
    private readonly IAuthService _authService;
    private readonly IAccountService _accountService;
    private readonly IGitHubClientService _gitHubClientService;

    public RepoSearchResultPageViewModel()
    {
        _authService = GetService<IAuthService>();
        _accountService = GetService<IAccountService>();
        _gitHubClientService = GetService<IGitHubClientService>();
        EmptyStateTitle = GetString("RepoSearch.EmptyStateTitle", "No repositories found.");
        EmptyStateDescription = GetString("RepoSearch.EmptyStateDescription", "Try a different repository name in the search box.");
    }

    public ObservableCollection<GitHubRepository> Results { get; } = [];

    [ObservableProperty]
    public partial string QueryTitleText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string StatusText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    public partial bool AreResultsVisible { get; set; }

    [ObservableProperty]
    public partial bool IsEmptyStateVisible { get; set; }

    [ObservableProperty]
    public partial string EmptyStateTitle { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string EmptyStateDescription { get; set; } = string.Empty;

    public async Task InitializeAsync(string query)
    {
        QueryTitleText = string.IsNullOrWhiteSpace(query)
            ? GetString("RepoSearch.TitleEmptyQuery", "Repository search")
            : FormatString("RepoSearch.TitleWithQuery", "Search: {0}", query);
        StatusText = GetString("RepoSearch.Searching", "Searching GitHub repositories...");
        Results.Clear();
        AreResultsVisible = false;
        IsEmptyStateVisible = false;

        if (string.IsNullOrWhiteSpace(query))
        {
            StatusText = GetString("RepoSearch.EnterQuery", "Enter a repository name in the search box to begin.");
            return;
        }

        await LoadResultsAsync(query);
    }

    private async Task LoadResultsAsync(string query)
    {
        string? token = GetActiveToken();
        if (string.IsNullOrWhiteSpace(token))
        {
            StatusText = GetString(
                "Common.AuthUnavailable",
                "GitHub authentication is no longer available. Please sign in again.");
            _authService.SignOut();
            return;
        }

        IsLoading = true;
        AreResultsVisible = false;
        IsEmptyStateVisible = false;

        try
        {
            IReadOnlyList<GitHubRepository> repositories =
                await _gitHubClientService.SearchRepositoriesAsync(token, query, 30);
            Results.Clear();
            foreach (GitHubRepository repository in repositories)
            {
                Results.Add(repository);
            }

            AreResultsVisible = Results.Count > 0;
            IsEmptyStateVisible = Results.Count == 0;
            StatusText = Results.Count == 0
                ? FormatString("RepoSearch.NoResultsStatus", "No repositories matched \"{0}\".", query)
                : FormatString("RepoSearch.ResultsStatus", "Showing {0} repositories for \"{1}\".", Results.Count, query);
        }
        catch (GitHubAuthenticationException)
        {
            StatusText = GetString(
                "Common.AuthInvalid",
                "GitHub authentication is no longer valid. Please sign in again.");
            _authService.SignOut();
        }
        catch (GitHubApiException ex)
        {
            IsEmptyStateVisible = true;
            StatusText = ex.Message;
        }
        catch (HttpRequestException)
        {
            IsEmptyStateVisible = true;
            StatusText = GetString(
                "RepoSearch.NetworkError",
                "JitHub could not reach GitHub to search repositories.");
        }
        finally
        {
            IsLoading = false;
        }
    }

    private string? GetActiveToken()
    {
        return _authService.GetToken(_authService.AuthenticatedUser?.Id ?? _accountService.GetUser());
    }
}
