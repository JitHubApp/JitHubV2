using JitHub.WinUI.Converters.Activities;
using JitHub.Models;
using JitHub.Models.NavArgs;
using JitHub.Services;
using JitHub.WinUI.ViewModels.Base;
using JitHub.WinUI.Views.Pages;
using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.WinUI;
using JitHub.Models.LegacyGitHub;
using CommitRequest = JitHub.Models.LegacyGitHub.CommitRequest;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Input;
using Windows.ApplicationModel.DataTransfer;

namespace JitHub.WinUI.ViewModels.CommitViewModels
{
    public class RepoCommitsViewModel : RepoViewModel
    {
        private readonly NavigationService _navigationService;
        private ICollection<Branch> _branches = [];
        private Branch? _selectedBranch;
        private IncrementalLoadingCollection<CommitsSource, CommandableCommit> _commits = null!;
        private CommandableCommit? _selectedCommit;
        private readonly CommitPageNavArg _navArgs;
        
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
        public IncrementalLoadingCollection<CommitsSource, CommandableCommit> Commits
        {
            get => _commits;
            set => SetProperty(ref _commits, value);
        }
        public CommandableCommit? SelectedCommit
        {
            get => _selectedCommit;
            set => SetProperty(ref _selectedCommit, value);
        }
        public ICommand LoadCommand { get; }
        public ICommand CopyCommand { get; }
        public ICommand ViewCodeCommand { get; }

        public RepoCommitsViewModel(CommitPageNavArg args)
        {
            _navArgs = args;
            _navigationService = Ioc.Default.GetService<NavigationService>()
                ?? throw new InvalidOperationException("NavigationService is not registered.");
            LoadCommand = new AsyncRelayCommand(Load);
            CopyCommand = new RelayCommand<string?>(Copy);
            ViewCodeCommand = new RelayCommand<string?>(ViewCode);
        }

        public void SelectionChanged(object sender, Microsoft.UI.Xaml.Controls.SelectionChangedEventArgs e)
        {
            if (e.RemovedItems.FirstOrDefault() is CommandableCommit oldItem)
            {
                oldItem.Selected = false;
            }

            if (e.AddedItems.FirstOrDefault() is CommandableCommit newItem)
            {
                newItem.Selected = true;
                SelectedCommit = newItem;
                return;
            }

            SelectedCommit = null;
        }

        public void BranchSelectionChanged(object sender, Microsoft.UI.Xaml.Controls.SelectionChangedEventArgs e)
        {
            if (e.AddedItems.FirstOrDefault() is Branch addedBranch &&
                e.RemovedItems.FirstOrDefault() is Branch removedBranch &&
                string.Equals(addedBranch.Name, removedBranch.Name, StringComparison.Ordinal))
            {
                return;
            }

            Reload();
        }

        private async Task<CommandableCommit?> GetCommandableCommit(string? gitRef)
        {
            if (string.IsNullOrWhiteSpace(gitRef))
            {
                return null;
            }

            var githubCommit = await GitHubService.GetGitHubCommit(Repo.Owner.Login, Repo.Name, gitRef);
            return new CommandableCommit(Repo, CopyCommand, ViewCodeCommand, githubCommit);
        }

        private void Copy(string? sha)
        {
            if (string.IsNullOrWhiteSpace(sha))
            {
                return;
            }

            var dataPackage = new DataPackage();
            dataPackage.SetText(sha);
            Clipboard.SetContent(dataPackage);
        }

        private void ViewCode(string? sha)
        {
            if (string.IsNullOrWhiteSpace(sha))
            {
                return;
            }

            var gitRef = RefFullStringToBranchConverter.ConvertFromRefToBranch(sha);
            _navigationService.RepoNagivateTo(typeof(RepoCodePage), CodeViewerNavArg.CreateWithGitRef(Repo, gitRef));
        }

        private void Reload()
        {
            if (SelectedBranch is null)
            {
                Commits = new IncrementalLoadingCollection<CommitsSource, CommandableCommit>(
                    new CommitsSource(Repo, new CommitRequest(), CopyCommand, ViewCodeCommand), 50);
                return;
            }

            Loading = true;
            var commitsSource = new CommitsSource(Repo, new CommitRequest { Sha = SelectedBranch.Name }, CopyCommand, ViewCodeCommand);
            Commits = new IncrementalLoadingCollection<CommitsSource, CommandableCommit>(commitsSource, 50);
            _ = Commits.RefreshAsync();
            Loading = false;
        }

        public async Task Load()
        {
            Loading = true;
            Repo = await GitHubService.GetRepository(_navArgs.Repo.Id);
            Branches = await GitHubService.GetRepoBranches(Repo.Owner.Login, Repo.Name);
            if (!_navArgs.NoBranch)
            {
                SelectedBranch = Branches.FirstOrDefault(branch => branch.Name == _navArgs.Branch);
            }
            else
            {
                SelectedBranch = Branches.FirstOrDefault(branch => branch.Name == Repo.DefaultBranch);
            }

            Reload();
            if (!_navArgs.NoRef)
            {
                if (SelectedCommit == null && Commits != null)
                {
                    var commandableCommit = await GetCommandableCommit(_navArgs.GitRef);
                    if (commandableCommit is null)
                    {
                        Loading = false;
                        return;
                    }

                    Commits.Add(commandableCommit);
                    SelectedCommit = Commits.FirstOrDefault(commit => commit.Sha == _navArgs.GitRef);
                }
            }
            Loading = false;
        }
    }
}






