using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.DependencyInjection;
using JitHub.Services;
using Octokit;
using System.Collections.Generic;
using System.Linq;
using Windows.UI.Xaml;

namespace JitHub.ViewModels.IssueViewModels;

public partial class UserIssueListViewModel : ObservableObject
{
    [ObservableProperty]
    private bool _loading;
    private IGitHubService _gitHubService;
    private IAuthService _authService;

    [ObservableProperty]
    private ICollection<Issue> _issues;
    [ObservableProperty]
    private ICollection<Issue> _pullRequests;

    public User User => _authService.AuthenticatedUser;
    
    public UserIssueListViewModel()
    {
        _gitHubService = Ioc.Default.GetService<IGitHubService>();
        _authService = Ioc.Default.GetService<IAuthService>();
    }

    public async void OnLoad(object sender, RoutedEventArgs e)
    {

        var all = await _gitHubService.GetIssuesAssignedToAuthenticatedUser();
        Issues = all.Where((issue) => issue.PullRequest == null).ToList();
        PullRequests = all.Where((issue) => issue.PullRequest != null).ToList();
    }
}
