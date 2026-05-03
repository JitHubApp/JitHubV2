using JitHub.WinUI.Helpers;
using JitHub.Models.NavArgs;
using JitHub.Services;
using JitHub.WinUI.ViewModels.Base;
using JitHub.WinUI.Views.Pages;
using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.Mvvm.Input;
using JitHub.Models.LegacyGitHub;
using NewRepositoryFork = JitHub.Models.LegacyGitHub.NewRepositoryFork;
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

namespace JitHub.WinUI.ViewModels.RepositoryViewModels
{
    public class RepoDetailViewModel : RepoViewModel
    {
        private Frame _frame = null!;
        private bool _starred;
        private bool _watching;
        private readonly NavigationService _navigationService;
        private Microsoft.UI.Xaml.Controls.NavigationViewItem _codeMenuItem = null!;
        private Microsoft.UI.Xaml.Controls.NavigationViewItem _issueMenuItem = null!;
        private Microsoft.UI.Xaml.Controls.NavigationViewItem _pullRequestsMenuItem = null!;
        private Microsoft.UI.Xaml.Controls.NavigationViewItem _commitsMenuItem = null!;
        private Microsoft.UI.Xaml.Controls.NavigationViewItem? _projectsMenuItem;
        private Microsoft.UI.Xaml.Controls.NavigationViewItem? _selectedItem;
        private ICollection<Microsoft.UI.Xaml.Controls.NavigationViewItem> _menuItems = [];
        private ICollection<Branch> _branches = [];
        private Branch? _selectedBranch;
        private Visibility _branchVisible;
        private bool _updatingBranchSelection;

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

        public Branch? SelectedBranch
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

        public Microsoft.UI.Xaml.Controls.NavigationViewItem? ProjectsMenuItem
        {
            get => _projectsMenuItem;
            set => SetProperty(ref _projectsMenuItem, value);
        }

        public Microsoft.UI.Xaml.Controls.NavigationViewItem? SelectedItem
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
            _navigationService = Ioc.Default.GetService<NavigationService>()
                ?? throw new InvalidOperationException("NavigationService is not registered.");
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
            if (Frame?.CanGoBack == true)
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
            Frame.Navigate(
                typeof(RepoCodePage),
                CodeViewerNavArg.CreateWithBranch(Model, SelectedBranch?.Name ?? Model.DefaultBranch),
                new SuppressNavigationTransitionInfo());
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
            try
            {
                Loading = true;
                var args = (RepoDetailPageArgs)e.Parameter;
                await HandleNavigatedTo(args);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to navigate repository detail: {ex}");
            }
            finally
            {
                Loading = false;
            }
        }

        // This is only for code view
        public void BranchSelectionChanged(object sender, Microsoft.UI.Xaml.Controls.SelectionChangedEventArgs e)
        {
            if (_updatingBranchSelection)
            {
                return;
            }

            if (e.AddedItems.FirstOrDefault() is not Branch addedBranch)
            {
                return;
            }

            if (e.RemovedItems.FirstOrDefault() is Branch removedBranch &&
                string.Equals(addedBranch.Name, removedBranch.Name, StringComparison.Ordinal))
            {
                return;
            }

            SelectedBranch = addedBranch;
            GoToCodePageWithBranch(addedBranch.Name);
        }

        private async Task HandleNavigatedTo(RepoDetailPageArgs args)
        {
            if (args.Repo != null)
            {
                Model = await GitHubService.GetRepository(args.Repo.Id);
                if (string.IsNullOrWhiteSpace(Model.DefaultBranch) &&
                    !string.IsNullOrWhiteSpace(args.Repo.DefaultBranch))
                {
                    Model.DefaultBranch = args.Repo.DefaultBranch;
                }
            }
            else
            {
                if (string.IsNullOrWhiteSpace(args.FullName))
                {
                    _navigationService.GoHome();
                    return;
                }

                var name = args.FullName.Split('/');
                if (name.Length < 2)
                {
                    _navigationService.GoHome();
                    return;
                }

                var repo = await GitHubService.GetRepository(name[0], name[1]);
                Model = repo;
            }
            if (Model == null)
            {
                _navigationService.GoHome();
                return;
            }

            Task<ICollection<Branch>> branchesTask = GitHubService.GetRepoBranches(Model.Owner.Login, Model.Name);
            Task<bool> starredTask = GitHubService.IsRepoStarredByCurrentUser(Model.Owner.Login, Model.Name);
            Task<bool> watchingTask = GitHubService.IsCurrentUserWatchingRepo(Model.Id);

            switch (args.Page)
            {
                case RepoPageType.CodePage:
                    GoToCodePage(ResolveInitialCodeViewerArg((CodeViewerNavArg)args.Ref));
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

            try
            {
                _updatingBranchSelection = true;
                Branches = await branchesTask;
                SelectedBranch = ResolveSelectedBranch(args);
            }
            catch (Exception)
            {
                Branches = [];
                SelectedBranch = null;
            }
            finally
            {
                _updatingBranchSelection = false;
            }

            EnsureCodePageBranchAlignment(args);

            try
            {
                IsStarred = await starredTask;
            }
            catch (Exception)
            {
                IsStarred = false;
            }

            try
            {
                IsWatching = await watchingTask;
            }
            catch (Exception)
            {
                IsWatching = false;
            }
        }

        private CodeViewerNavArg ResolveInitialCodeViewerArg(CodeViewerNavArg arg)
        {
            if (arg.IsGitRef)
            {
                return CodeViewerNavArg.CreateWithGitRef(Model, arg.GitRef);
            }

            string originalDefaultBranch = arg.Repo.DefaultBranch;
            string targetBranch = arg.Branch ?? string.Empty;

            if (string.IsNullOrWhiteSpace(targetBranch) ||
                string.Equals(targetBranch, originalDefaultBranch, StringComparison.OrdinalIgnoreCase))
            {
                targetBranch = Model.DefaultBranch;
            }

            return CodeViewerNavArg.CreateWithBranch(Model, targetBranch);
        }

        private Branch? ResolveSelectedBranch(RepoDetailPageArgs args)
        {
            string? requestedBranch = GetRequestedCodeBranch(args);
            string? fallbackDefaultBranch = args.Repo?.DefaultBranch;

            return Branches.FirstOrDefault(branch => string.Equals(branch.Name, requestedBranch, StringComparison.Ordinal))
                ?? Branches.FirstOrDefault(branch => string.Equals(branch.Name, Model.DefaultBranch, StringComparison.Ordinal))
                ?? Branches.FirstOrDefault(branch => string.Equals(branch.Name, fallbackDefaultBranch, StringComparison.Ordinal))
                ?? Branches.FirstOrDefault(branch => string.Equals(branch.Name, "master", StringComparison.Ordinal))
                ?? Branches.FirstOrDefault(branch => string.Equals(branch.Name, "main", StringComparison.Ordinal))
                ?? Branches.FirstOrDefault();
        }

        private void EnsureCodePageBranchAlignment(RepoDetailPageArgs args)
        {
            if (args.Page != RepoPageType.CodePage ||
                args.Ref is not CodeViewerNavArg arg ||
                arg.IsGitRef ||
                Frame?.Content is not RepoCodePage)
            {
                return;
            }

            string? resolvedBranch = SelectedBranch?.Name ?? Model.DefaultBranch;
            if (string.IsNullOrWhiteSpace(resolvedBranch))
            {
                return;
            }

            string incomingBranch = arg.Branch ?? string.Empty;
            string requestedDefaultBranch = args.Repo?.DefaultBranch ?? string.Empty;
            bool isDefaultBranchNavigation =
                string.IsNullOrWhiteSpace(incomingBranch) ||
                string.Equals(incomingBranch, requestedDefaultBranch, StringComparison.OrdinalIgnoreCase);

            if (!isDefaultBranchNavigation ||
                string.Equals(incomingBranch, resolvedBranch, StringComparison.Ordinal))
            {
                return;
            }

            GoToCodePage(CodeViewerNavArg.CreateWithBranch(Model, resolvedBranch));
        }

        private static string? GetRequestedCodeBranch(RepoDetailPageArgs args)
        {
            if (args.Page != RepoPageType.CodePage ||
                args.Ref is not CodeViewerNavArg { IsBranch: true } codeArg ||
                string.IsNullOrWhiteSpace(codeArg.Branch))
            {
                return null;
            }

            return codeArg.Branch;
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
                    await Task.Delay(100);
                    newRepo = await GitHubService.GetRepository(newRepo.Id);
                    branches = await GitHubService.GetRepoBranches(newRepo.Owner.Login, newRepo.Name);
                }
                var navArgs = new RepoDetailPageArgs(RepoPageType.CodePage, newRepo);
                await HandleNavigatedTo(navArgs);
                _navigationService.ChangeTabTitle(newRepo.GetRepositoryFullName());
            }
            catch (Exception)
            {

            }
            Loading = false;
        }
    }
}





