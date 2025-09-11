using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.WinUI.Controls;
using JitHub.Services;
using Octokit;
using System.Collections.Generic;
using System.Linq;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace JitHub.ViewModels.IssueViewModels;

public partial class UserIssueListViewModel : ObservableObject
{
    [ObservableProperty]
    private bool _loading;
    private IGitHubService _gitHubService;
    private IAuthService _authService;

    [ObservableProperty]
    private ICollection<Issue> _assignedIssues;
    [ObservableProperty]
    private ICollection<Issue> _assignedPullRequests;

    [ObservableProperty]
    private ICollection<Issue> _createdIssues;
    [ObservableProperty]
    private ICollection<Issue> _createdPullRequests;

    [ObservableProperty]
    private ICollection<Issue> _issues;
    [ObservableProperty]
    private ICollection<Issue> _pullRequests;

    private bool _createdSelected;

    public bool CreatedSelected
    {
        get => _createdSelected;
        set
        {
            SetProperty(ref _createdSelected, value);
            if (value)
            {
                Issues = CreatedIssues;
                PullRequests = CreatedPullRequests;
            }
            else
            {
                Issues = AssignedIssues;
                PullRequests = AssignedPullRequests;
            }
        }
    }

    public User User => _authService.AuthenticatedUser;
    
    public UserIssueListViewModel()
    {
        _gitHubService = Ioc.Default.GetService<IGitHubService>();
        _authService = Ioc.Default.GetService<IAuthService>();
    }

    public void SegmentedSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.AddedItems.Count > 0 && e.AddedItems[0] is SegmentedItem item)
        {
            if (item.Tag.ToString() == "Created")
            {
                CreatedSelected = true;
            }
            else
            {
                CreatedSelected = false;
            }
        }
    }

    public async void OnLoad(object sender, RoutedEventArgs e)
    {
        var all = await _gitHubService.GetIssuesForAuthenticatedUser(new());
        AssignedIssues = all.Where((issue) => issue.PullRequest == null).ToList();
        AssignedPullRequests = all.Where((issue) => issue.PullRequest != null).ToList();

        var created = await _gitHubService.GetIssuesForAuthenticatedUser(new()
        {
            Filter = IssueFilter.Created
        });
        CreatedIssues = created.Where((issue) => issue.PullRequest == null).ToList();
        CreatedPullRequests = created.Where((issue) => issue.PullRequest != null).ToList();
        CreatedSelected = false;
    }
}
