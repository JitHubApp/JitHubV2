using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.DependencyInjection;
using JitHub.Services;
using Octokit;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;

namespace JitHub.ViewModels.IssueViewModels;

public partial class UserIssueListViewModel : ObservableObject
{
    [ObservableProperty]
    private bool _loading;
    private IGitHubService _gitHubService;
    private IAuthService _authService;

    [ObservableProperty]
    private ICollection<Issue> _createdIssues;
    [ObservableProperty]
    private ICollection<Issue> _assignedIssues;
    [ObservableProperty]
    private ICollection<Issue> _mentionedIssues;

    public User User => _authService.AuthenticatedUser;
    
    public UserIssueListViewModel()
    {
        _gitHubService = Ioc.Default.GetService<IGitHubService>();
        _authService = Ioc.Default.GetService<IAuthService>();
    }

    public async void OnLoad(object sender, RoutedEventArgs e)
    {

        var created = await _gitHubService.GitHubClient.Issue.GetAllForCurrent(new IssueRequest()
        {
            Filter = IssueFilter.Created,
            State = ItemStateFilter.Open
        });
        var assigned = await _gitHubService.GitHubClient.Issue.GetAllForCurrent(new IssueRequest()
        {
            Filter = IssueFilter.Assigned,
            State = ItemStateFilter.Open
        });
        var mentioned = await _gitHubService.GitHubClient.Issue.GetAllForCurrent(new IssueRequest()
        {
            Filter = IssueFilter.Mentioned,
            State = ItemStateFilter.Open
        });
        CreatedIssues = created.ToList();
        AssignedIssues = assigned.ToList();
        MentionedIssues = mentioned.ToList();
    }
}
