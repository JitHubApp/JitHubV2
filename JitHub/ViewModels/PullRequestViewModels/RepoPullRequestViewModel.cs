using JitHub.Models;
using JitHub.Models.Base;
using JitHub.Models.NavArgs;
using JitHub.Services;
using JitHub.ViewModels.Base;
using JitHub.Views.Controls.PullRequest;
using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.Mvvm.Input;
using Octokit;
using System;
using System.Windows.Input;
using Microsoft.UI.Xaml.Controls;
using CommunityToolkit.WinUI;

namespace JitHub.ViewModels.PullRequestViewModels;

public partial class RepoPullRequestViewModel : RepoViewModel
{
    private bool _isEmpty;
    private NavigationService _navigationService;
    private ModalService _modalService;
    private RepoSelectableItemModel<PullRequest> _selectedPullRequest;
    private IncrementalLoadingCollection<PullRequestSource, RepoSelectableItemModel<PullRequest>> _pullRequests;
    

    public RepoSelectableItemModel<PullRequest> SelectedPullRequest
    {
        get => _selectedPullRequest;
        set => SetProperty(ref _selectedPullRequest, value);
    }
    public bool IsEmpty
    {
        get => _isEmpty;
        set => SetProperty(ref _isEmpty, value);
    }
    public ICommand NewPRCommand { get; set; }
    public ContentDialog NewPRDialog { get; set; }
    public ICommand CreationCallBackCommand { get; }
    public IncrementalLoadingCollection<PullRequestSource, RepoSelectableItemModel<PullRequest>> PullRequests
    {
        get => _pullRequests;
        set => SetProperty(ref _pullRequests, value);
    }
    

    public RepoPullRequestViewModel()
    {
        _modalService = Ioc.Default.GetService<ModalService>();
        _navigationService = Ioc.Default.GetService<NavigationService>();
        NewPRCommand = new RelayCommand(OpenNewPRDialog);
        CreationCallBackCommand = new RelayCommand(OnCreation);
        InitializeFilters();
    }

    public async void Init(PullRequestPageNavArg arg)
    {
        Repo = await GitHubService.GetRepository(arg.Repo.Id);
        RepoSelectableItemModel<PullRequest> selectedPullRequest = null;
        if (!arg.NoDetail)
        {
            var pullRequest = await GitHubService.GetPullRequest(Repo.Owner.Login, Repo.Name, arg.PullRequestId);
            selectedPullRequest = new RepoSelectableItemModel<PullRequest>() { Model = pullRequest, Repository = Repo };
        }
        var prParams = new PullRequestRequest
        {
            State = ItemStateFilter.Open,
            SortProperty = PullRequestSort.Created,
            SortDirection = SortDirection.Descending
        };
        var pullRequestSource = new PullRequestSource(Repo, prParams);
        SetIncrementalCollection(pullRequestSource, selectedPullRequest);
    }

    private void SetIncrementalCollection(PullRequestSource prSource, RepoSelectableItemModel<PullRequest> selectedPullRequest)
    {
        PullRequests = new IncrementalLoadingCollection<PullRequestSource, RepoSelectableItemModel<PullRequest>>(prSource, _perPage);
        if (selectedPullRequest != null)
        {
            PullRequests.Add(selectedPullRequest);
            SelectedPullRequest = selectedPullRequest;
        }
        else
        {
            PullRequests.RefreshAsync();
        }
        PullRequests.OnStartLoading += () =>
        {
            Loading = true;
        };
        PullRequests.OnEndLoading += () =>
        {
            Loading = false;
            IsEmpty = PullRequests.Count == 0;
        };
    }

    private void OpenNewPRDialog()
    {
        _modalService.Open("New Pull Request", new PullRequestForm(Repo, CreationCallBackCommand));
    }

    private void OnCreation()
    {
        _modalService.Close();
        ApplyFilter(null);
    }

    public void PullRequestPageMasterDetail_SelectionChanged(object sender, Microsoft.UI.Xaml.Controls.SelectionChangedEventArgs e)
    {
        try
        {
            var oldItem = e.RemovedItems[0] as RepoSelectableItemModel<PullRequest>;
            var newItem = e.AddedItems[0] as RepoSelectableItemModel<PullRequest>;
            if (oldItem != null)
                oldItem.Selected = false;
            if (newItem != null)
                newItem.Selected = true;
        }
        catch (Exception)
        { }
    }
}
