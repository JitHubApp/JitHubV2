using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.DependencyInjection;
using JitHub.Models.GitHub;
using JitHub.Services;
using IssueFilter = JitHub.Models.LegacyGitHub.IssueFilter;
using IssueRequest = JitHub.Models.LegacyGitHub.IssueRequest;
using ItemStateFilter = JitHub.Models.LegacyGitHub.ItemStateFilter;
using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;

namespace JitHub.WinUI.ViewModels.IssueViewModels;

public partial class UserIssueListViewModel : ObservableObject
{
    [ObservableProperty]
    public partial bool Loading { get; set; }
    private readonly IGitHubService _gitHubService;
    private readonly IAuthService _authService;

    [ObservableProperty]
    public partial List<GitHubIssue> CreatedIssues { get; set; } = [];

    [ObservableProperty]
    public partial List<GitHubIssue> AssignedIssues { get; set; } = [];

    [ObservableProperty]
    public partial List<GitHubIssue> MentionedIssues { get; set; } = [];

    public GitHubUser? User => _authService.AuthenticatedUser;
    
    public UserIssueListViewModel()
    {
        _gitHubService = Ioc.Default.GetService<IGitHubService>()
            ?? throw new InvalidOperationException("IGitHubService is not registered.");
        _authService = Ioc.Default.GetService<IAuthService>()
            ?? throw new InvalidOperationException("IAuthService is not registered.");
    }

    public async void OnLoad(object sender, RoutedEventArgs e)
    {
        Loading = true;
        try
        {
            var created = await _gitHubService.GetCurrentUserIssues(new IssueRequest()
            {
                Filter = IssueFilter.Created,
                State = ItemStateFilter.Open
            });
            var assigned = await _gitHubService.GetCurrentUserIssues(new IssueRequest()
            {
                Filter = IssueFilter.Assigned,
                State = ItemStateFilter.Open
            });
            var mentioned = await _gitHubService.GetCurrentUserIssues(new IssueRequest()
            {
                Filter = IssueFilter.Mentioned,
                State = ItemStateFilter.Open
            });
            CreatedIssues = created.ToList();
            AssignedIssues = assigned.ToList();
            MentionedIssues = mentioned.ToList();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to load user issues: {ex}");
        }
        finally
        {
            Loading = false;
        }
    }
}




