using JitHub.Models;
using JitHub.Models.Base;
using JitHub.Models.NavArgs;
using JitHub.Services;
using JitHub.WinUI.ViewModels.Base;
using JitHub.WinUI.Views.Controls.PullRequest;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.WinUI;
using JitHub.Models.LegacyGitHub;
using ItemStateFilter = JitHub.Models.LegacyGitHub.ItemStateFilter;
using PullRequestRequest = JitHub.Models.LegacyGitHub.PullRequestRequest;
using PullRequestSort = JitHub.Models.LegacyGitHub.PullRequestSort;
using SortDirection = JitHub.Models.LegacyGitHub.SortDirection;
using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using Microsoft.UI.Xaml.Controls;

namespace JitHub.WinUI.ViewModels.PullRequestViewModels
{
    public partial class RepoPullRequestViewModel : RepoViewModel
    {
        private bool _isEmpty;
        private readonly ModalService _modalService;
        private RepoSelectableItemModel<PullRequest>? _selectedPullRequest;
        private IncrementalLoadingCollection<PullRequestSource, RepoSelectableItemModel<PullRequest>> _pullRequests = null!;
        

        public RepoSelectableItemModel<PullRequest>? SelectedPullRequest
        {
            get => _selectedPullRequest;
            set => SetProperty(ref _selectedPullRequest, value);
        }
        public bool IsEmpty
        {
            get => _isEmpty;
            set => SetProperty(ref _isEmpty, value);
        }
        public ICommand NewPRCommand { get; }
        public ContentDialog? NewPRDialog { get; set; }
        public ICommand CreationCallBackCommand { get; }
        public IncrementalLoadingCollection<PullRequestSource, RepoSelectableItemModel<PullRequest>> PullRequests
        {
            get => _pullRequests;
            set => SetProperty(ref _pullRequests, value);
        }
        

        public RepoPullRequestViewModel()
        {
            _modalService = Ioc.Default.GetService<ModalService>()
                ?? throw new InvalidOperationException("ModalService is not registered.");
            NewPRCommand = new RelayCommand(OpenNewPRDialog);
            CreationCallBackCommand = new RelayCommand(OnCreation);
            InitializeFilters();
        }

        public async void Init(PullRequestPageNavArg arg)
        {
            try
            {
                Repo = await ResolveRepositoryAsync(arg);
                if (Repo is null)
                {
                    return;
                }

                RepoSelectableItemModel<PullRequest>? selectedPullRequest = null;
                if (!arg.NoDetail)
                {
                    var pullRequest = await GitHubService.GetPullRequest(Repo.Owner.Login, Repo.Name, arg.PullRequestId);
                    selectedPullRequest = new RepoSelectableItemModel<PullRequest>() { Model = pullRequest, Repository = Repo };
                }
                else if (JitHub.WinUI.Program.CurrentLaunchOptions.IsPublicPreviewOverride)
                {
                    var previewPullRequests = await GitHubService.GetPullRequests(
                        Repo.Owner.Login,
                        Repo.Name,
                        new PullRequestRequest
                        {
                            State = ItemStateFilter.Open,
                            SortProperty = PullRequestSort.Created,
                            SortDirection = SortDirection.Descending
                        },
                        new ApiOptions { StartPage = 1, PageCount = 1, PageSize = 1 });
                    if (previewPullRequests.FirstOrDefault() is PullRequest firstPullRequest)
                    {
                        selectedPullRequest = new RepoSelectableItemModel<PullRequest>
                        {
                            Model = firstPullRequest,
                            Repository = Repo,
                            Selected = true
                        };
                    }
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
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to initialize repository pull requests: {ex}");
            }
        }

        private async Task<Repository> ResolveRepositoryAsync(PullRequestPageNavArg arg)
        {
            if (arg.Repo.Id > 0)
            {
                return await GitHubService.GetRepository(arg.Repo.Id);
            }

            if (!string.IsNullOrWhiteSpace(arg.Repo.Owner?.Login) &&
                !string.IsNullOrWhiteSpace(arg.Repo.Name))
            {
                return await GitHubService.GetRepository(arg.Repo.Owner.Login, arg.Repo.Name);
            }

            if (!string.IsNullOrWhiteSpace(arg.Repo.FullName))
            {
                string[] parts = arg.Repo.FullName.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (parts.Length >= 2)
                {
                    return await GitHubService.GetRepository(parts[0], parts[1]);
                }
            }

            if (JitHub.WinUI.Program.CurrentLaunchOptions.IsPublicPreviewOverride)
            {
                string[] parts = JitHub.WinUI.Program.CurrentLaunchOptions.RepositoryFullName.Split(
                    '/',
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (parts.Length >= 2)
                {
                    return await GitHubService.GetRepository(parts[0], parts[1]);
                }
            }

            return null!;
        }

        private void SetIncrementalCollection(PullRequestSource prSource, RepoSelectableItemModel<PullRequest>? selectedPullRequest)
        {
            PullRequests = new IncrementalLoadingCollection<PullRequestSource, RepoSelectableItemModel<PullRequest>>(prSource, _perPage);
            PullRequests.OnStartLoading += () =>
            {
                Loading = true;
            };
            PullRequests.OnEndLoading += () =>
            {
                Loading = false;
                IsEmpty = PullRequests.Count == 0;
                if (JitHub.WinUI.Program.CurrentLaunchOptions.IsPublicPreviewOverride &&
                    SelectedPullRequest is null &&
                    PullRequests.FirstOrDefault() is RepoSelectableItemModel<PullRequest> firstPullRequest)
                {
                    SelectedPullRequest = firstPullRequest;
                    firstPullRequest.Selected = true;
                }
            };
            if (selectedPullRequest != null)
            {
                PullRequests.Add(selectedPullRequest);
                SelectedPullRequest = selectedPullRequest;
            }
            else
            {
                _ = PullRequests.RefreshAsync();
            }
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
            if (e.RemovedItems.FirstOrDefault() is RepoSelectableItemModel<PullRequest> oldItem)
            {
                oldItem.Selected = false;
            }

            if (e.AddedItems.FirstOrDefault() is RepoSelectableItemModel<PullRequest> newItem)
            {
                newItem.Selected = true;
            }
        }
    }
}






