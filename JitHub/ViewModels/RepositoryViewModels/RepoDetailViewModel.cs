using JitHub.Helpers;
using JitHub.Models.NavArgs;
using JitHub.Services;
using JitHub.ViewModels.Base;
using JitHub.Views.Pages;
using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.Mvvm.Input;
using Octokit;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;
using System.Linq;
using Microsoft.UI.Xaml;

namespace JitHub.ViewModels.RepositoryViewModels
{
    public class RepoDetailViewModel : RepoViewModel
    {
        private Frame _frame;
        private bool _starred;
        private bool _watching;
        private NavigationService _navigationService;
        private Microsoft.UI.Xaml.Controls.NavigationViewItem _codeMenuItem;
        private Microsoft.UI.Xaml.Controls.NavigationViewItem _issueMenuItem;
        private Microsoft.UI.Xaml.Controls.NavigationViewItem _pullRequestsMenuItem;
        private Microsoft.UI.Xaml.Controls.NavigationViewItem _commitsMenuItem;
        private Microsoft.UI.Xaml.Controls.NavigationViewItem _projectsMenuItem;
        private Microsoft.UI.Xaml.Controls.NavigationViewItem _selectedItem;
        private ICollection<Microsoft.UI.Xaml.Controls.NavigationViewItem> _menuItems;
        private ICollection<Branch> _branches;
        private Branch _selectedBranch;
        private Visibility _branchVisible;

        public Visibility BranchVisible
        {
            get => _branchVisible;
            set => SetProperty(ref _branchVisible, value);
        }

        public ICollection<Branch> Branches
        {
            get => _branches;
            set => SetProperty(ref _branches, value);
        }

        public Branch SelectedBranch
        {
            get => _selectedBranch;
            set => SetProperty(ref _selectedBranch, value);
        }
        
        public Frame Frame
        {
            get => _frame;
            set
            {
                SetProperty(ref _frame, value);
                _navigationService.RepoFrame = value;
            }
        }
        public bool IsStarred
        {
            get => _starred;
            set => SetProperty(ref _starred, value);
        }
        public bool IsWatching
        {
            get => _watching;
            set => SetProperty(ref _watching, value);
        }
        public Microsoft.UI.Xaml.Controls.NavigationViewItem CodeMenuItem
        {
            get => _codeMenuItem;
            set => SetProperty(ref _codeMenuItem, value);
        }

        public Microsoft.UI.Xaml.Controls.NavigationViewItem IssueMenuItem
        {
            get => _issueMenuItem;
            set => SetProperty(ref _issueMenuItem, value);
        }

        public Microsoft.UI.Xaml.Controls.NavigationViewItem PullRequestsMenuItem
        {
            get => _pullRequestsMenuItem;
            set => SetProperty(ref _pullRequestsMenuItem, value);
        }

        public Microsoft.UI.Xaml.Controls.NavigationViewItem CommitsMenuItem
        {
            get => _commitsMenuItem;
            set => SetProperty(ref _commitsMenuItem, value);
        }

        public Microsoft.UI.Xaml.Controls.NavigationViewItem ProjectsMenuItem
        {
            get => _projectsMenuItem;
            set => SetProperty(ref _projectsMenuItem, value);
        }

        public Microsoft.UI.Xaml.Controls.NavigationViewItem SelectedItem
        {
            get => _selectedItem;
            set
            {
                SetProperty(ref _selectedItem, value);
                BranchVisible = value == CodeMenuItem ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        public ICollection<Microsoft.UI.Xaml.Controls.NavigationViewItem> MenuItems
        {
            get => _menuItems;
            set => SetProperty(ref _menuItems, value);
        }

        public ICommand ToggleStarCommand { get; }
        public ICommand ForkCommand { get; }
        public ICommand ToggleWatchCommand { get; }
        public RepoDetailViewModel()
        {
            _navigationService = Ioc.Default.GetService<NavigationService>();
            CodeMenuItem = new Microsoft.UI.Xaml.Controls.NavigationViewItem() { Content = "Code" };
            IssueMenuItem = new Microsoft.UI.Xaml.Controls.NavigationViewItem() { Content = "Issues" };
            PullRequestsMenuItem = new Microsoft.UI.Xaml.Controls.NavigationViewItem() { Content = "Pull Requests" };
            CommitsMenuItem = new Microsoft.UI.Xaml.Controls.NavigationViewItem() { Content = "Commits" };
            //ProjectsMenuItem = new Microsoft.UI.Xaml.Controls.NavigationViewItem() { Content = "Projects" };
            MenuItems = new List<Microsoft.UI.Xaml.Controls.NavigationViewItem>()
            {
                CodeMenuItem,
                IssueMenuItem,
                PullRequestsMenuItem,
                CommitsMenuItem,
                //ProjectsMenuItem,
            };

            ToggleStarCommand = new AsyncRelayCommand(ToggleStar);
            ForkCommand = new AsyncRelayCommand(ForkRepo);
            ToggleWatchCommand = new AsyncRelayCommand(ToggleWatch);
        }

        public void RepoDetailNav_BackRequested(Microsoft.UI.Xaml.Controls.NavigationView sender, Microsoft.UI.Xaml.Controls.NavigationViewBackRequestedEventArgs args)
        {
            if (Frame.CanGoBack)
                Frame.GoBack();
        }

        private void SelectNavVIewItem(Microsoft.UI.Xaml.Controls.NavigationViewItem item)
        {
            if (SelectedItem != null)
            {
                SelectedItem.IsSelected = false;
            }
            SelectedItem = item;
            SelectedItem.IsSelected = true;
        }

        private void GoToCodePage(CodeViewerNavArg arg)
        {
            SelectNavVIewItem(CodeMenuItem);
            Frame.Navigate(typeof(RepoCodePage), arg.WithRepo(Model), new SuppressNavigationTransitionInfo());
        }

        private void GoToCodePage()
        {
            SelectNavVIewItem(CodeMenuItem);
            Frame.Navigate(typeof(RepoCodePage), CodeViewerNavArg.CreateWithBranch(Model, SelectedBranch == null ? Model.DefaultBranch : SelectedBranch.Name), new SuppressNavigationTransitionInfo());
        }

        private void GoToCodePageWithBranch(string branch)
        {
            SelectNavVIewItem(CodeMenuItem);
            Frame.Navigate(typeof(RepoCodePage), CodeViewerNavArg.CreateWithBranch(Model, branch), new SuppressNavigationTransitionInfo());
        }

        private void GoToIssuesPage(IssueNavArg arg)
        {
            SelectNavVIewItem(IssueMenuItem);
            Frame.Navigate(typeof(RepoIssuePage), arg.WithRepo(Model), new SuppressNavigationTransitionInfo());
        }
        
        private void GoToIssuesPage()
        {
            SelectNavVIewItem(IssueMenuItem);
            Frame.Navigate(typeof(RepoIssuePage), new IssueNavArg(Model, 0), new SuppressNavigationTransitionInfo());
        }

        private void GoToPullRequestPage()
        {
            SelectNavVIewItem(PullRequestsMenuItem);
            Frame.Navigate(typeof(RepoPullRequestPage), new PullRequestPageNavArg(Model, 0), new SuppressNavigationTransitionInfo());
        }

        private void GoToPullRequestPage(PullRequestPageNavArg arg)
        {
            SelectNavVIewItem(PullRequestsMenuItem);
            Frame.Navigate(typeof(RepoPullRequestPage), arg.WithRepo(Repo), new SuppressNavigationTransitionInfo());
        }

        private void GoToCommitsPage()
        {
            SelectNavVIewItem(CommitsMenuItem);
            Frame.Navigate(typeof(RepoCommitsPage), CommitPageNavArg.CreateWithBranch(Model, Model.DefaultBranch), new SuppressNavigationTransitionInfo());
        }

        private void GoToCommitsPage(CommitPageNavArg arg)
        {
            if (string.IsNullOrWhiteSpace(arg.GitRef))
            {
                GoToCommitsPage();
                return;
            }
            SelectNavVIewItem(CommitsMenuItem);
            Frame.Navigate(typeof(RepoCommitsPage), arg.WithRepo(Model), new SuppressNavigationTransitionInfo());
        }

        public async void OnNavigatedTo(NavigationEventArgs e)
        {
            Loading = true;
            var args = (RepoDetailPageArgs)e.Parameter;
            await HandleNavigatedTo(args);
            Loading = false;
        }

        // This is only for code view
        public void BranchSelectionChanged(object sender, Microsoft.UI.Xaml.Controls.SelectionChangedEventArgs e)
        {
            if (e.RemovedItems.Count != 0)
            {
                // don't fetch again if same name
                var add = (Branch)e.AddedItems[0];
                var removed = (Branch)e.RemovedItems[0];
                if (add.Name == removed.Name) return;
                SelectedBranch = add;
                try
                {
                    GoToCodePageWithBranch(add.Name);
                }
                catch (Exception)
                {

                }
            }
        }

        private async Task HandleNavigatedTo(RepoDetailPageArgs args)
        {
            if (args.Repo != null)
            {
                Model = await GitHubService.GetRepository(args.Repo.Id);
                
            }
            else
            {
                var name = args.FullName.Split('/');
                var repo = await GitHubService.GetRepository(name[0], name[1]);
                Model = repo;
            }
            if (Model == null)
            {
                _navigationService.GoHome();
                return;
            }
            Branches = await GitHubService.GetRepoBranches(Model.Owner.Login, Model.Name);
            SelectedBranch = Branches.FirstOrDefault(branch => branch.Name == Model.DefaultBranch);
            IsStarred = await GitHubService.IsRepoStarredByCurrentUser(Model.Owner.Login, Model.Name);
            IsWatching = await GitHubService.IsCurrentUserWatchingRepo(Model.Id);
            switch (args.Page)
            {
                case RepoPageType.CodePage:
                    GoToCodePage((CodeViewerNavArg)args.Ref);
                    break;
                //TODO: add cases for other page types
                case RepoPageType.IssuePage:
                    GoToIssuesPage((IssueNavArg)args.Ref);
                    break;
                case RepoPageType.PullRequestPage:
                    GoToPullRequestPage((PullRequestPageNavArg)args.Ref);
                    break;
                case RepoPageType.CommitPage:
                    GoToCommitsPage((CommitPageNavArg)args.Ref);
                    break;
                default:
                    break;
            }
        }

        public void RepoDetailNav_ItemInvoked(Microsoft.UI.Xaml.Controls.NavigationView sender, Microsoft.UI.Xaml.Controls.NavigationViewItemInvokedEventArgs args)
        {
            switch (args.InvokedItem as string)
            {
                case "Code":
                    GoToCodePage();
                    break;
                case "Issues":
                    GoToIssuesPage();
                    break;
                case "Pull Requests":
                    GoToPullRequestPage();
                    break;
                case "Commits":
                    GoToCommitsPage();
                    break;
                default:
                    break;
            }
        }

        private async Task RefreshModel(long repoId)
        {
            Model = await GitHubService.GetRepository(repoId);
        }

        private async Task<bool> ToggleStar()
        {
            bool res;
            if (IsStarred)
            {
                res = await GitHubService.UnstarRepo(Model.Owner.Login, Model.Name);
            }
            else
            {
                res = await GitHubService.StarRepo(Model.Owner.Login, Model.Name);
            }
            await RefreshModel(Model.Id);
            IsStarred = await GitHubService.IsRepoStarredByCurrentUser(Model.Owner.Login, Model.Name);
            return res;
        }

        private async Task<bool> ToggleWatch()
        {
            bool res;
            if (IsWatching)
            {
                res = await GitHubService.UnwatchRepo(Model.Id);
            }
            else
            {
                var sub = await GitHubService.WatchRepo(Model.Id);
                res = sub.Subscribed;
            }
            await RefreshModel(Model.Id);
            IsWatching = await GitHubService.IsCurrentUserWatchingRepo(Model.Id);
            return res;
        }

        private async Task ForkRepo()
        {
            Loading = true;
            try
            {
                var newRepo = await GitHubService.ForkRepo(Model.Id, new NewRepositoryFork());
                var branches = await GitHubService.GetRepoBranches(newRepo.Owner.Login, newRepo.Name);
                while (branches.Count == 0)
                {
                    Thread.Sleep(100);
                    newRepo = await GitHubService.GetRepository(newRepo.Id);
                    branches = await GitHubService.GetRepoBranches(newRepo.Owner.Login, newRepo.Name);
                }
                var navArgs = new RepoDetailPageArgs(RepoPageType.CodePage, newRepo);
                await HandleNavigatedTo(navArgs);
                _navigationService.ChangeTabTitle(newRepo.GetRepositoryFullName());
            }
            catch (Exception ex)
            {

            }
            Loading = false;
        }
    }
}
