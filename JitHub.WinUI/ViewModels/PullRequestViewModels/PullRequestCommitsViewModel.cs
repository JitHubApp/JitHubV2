using JitHub.Models;
using JitHub.Services;
using JitHub.WinUI.ViewModels.Base;
using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.Mvvm.Input;
using JitHub.Models.LegacyGitHub;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using Windows.ApplicationModel.DataTransfer;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace JitHub.WinUI.ViewModels.PullRequestViewModels
{
    public class PullRequestCommitsViewModel : RepoViewModel
    {
        private PullRequest _pullRequest = null!;
        private List<CommandableCommit> _commits = [];
        private readonly DataPackage _dataPackage;

        public PullRequest PullRequest
        {
            get => _pullRequest;
            set => SetProperty(ref _pullRequest, value);
        }
        public List<CommandableCommit> Commits
        {
            get => _commits;
            set => SetProperty(ref _commits, value);
        }

        public ICommand LoadCommand { get; }
        public ICommand CopyCommand { get; }
        public ICommand ViewCodeCommand { get; }

        public PullRequestCommitsViewModel()
        {
            _dataPackage = new DataPackage();
            _dataPackage.RequestedOperation = DataPackageOperation.Copy; 
            LoadCommand = new AsyncRelayCommand(LoadCommits);
            CopyCommand = new RelayCommand<string>(Copy);
            ViewCodeCommand = new RelayCommand<string>(ViewCode);
        }

        private async Task LoadCommits()
        {
            Loading = true;
            var commits = await GitHubService.GetCommitsFromPullRequest(Repo.Owner.Login, Repo.Name, PullRequest.Number);
            Commits = commits
                .OrderByDescending(commit => commit.Commit.Author.Date)
                .Select(commit => new CommandableCommit(Repo, CopyCommand, ViewCodeCommand, commit))
                .ToList();
            Loading = false;
        }

        private void Copy(string? sha)
        {
            if (string.IsNullOrWhiteSpace(sha))
            {
                return;
            }

            _dataPackage.SetText(sha);
            Clipboard.SetContent(_dataPackage);
        }

        private void ViewCode(string? sha)
        {
            //TODO
        }

        public void SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (e.RemovedItems.FirstOrDefault() is CommandableCommit oldItem)
            {
                oldItem.Selected = false;
            }

            if (e.AddedItems.FirstOrDefault() is CommandableCommit newItem)
            {
                newItem.Selected = true;
            }
        }

        public void OnNavigatedTo(NavigationEventArgs e)
        {
            var pr = ((Repository, PullRequest))e.Parameter;
            Repo = pr.Item1;
            PullRequest = pr.Item2;
        }
    }
}



