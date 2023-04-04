using JitHub.Models;
using JitHub.Services;
using JitHub.ViewModels.Base;
using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.Mvvm.Input;
using Octokit;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using Windows.ApplicationModel.DataTransfer;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;

namespace JitHub.ViewModels.PullRequestViewModels
{
    public class PullRequestCommitsViewModel : RepoViewModel
    {
        private PullRequest _pullRequest;
        private ICollection<CommandableCommit> _commits;
        private DataPackage _dataPackage;

        public PullRequest PullRequest
        {
            get => _pullRequest;
            set => SetProperty(ref _pullRequest, value);
        }
        public ICollection<CommandableCommit> Commits
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

        private void Copy(string sha)
        {
            _dataPackage.SetText(sha);
            Clipboard.SetContent(_dataPackage);
        }

        private void ViewCode(string sha)
        {
            //TODO
        }

        public void SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            try
            {
                var oldItem = e.RemovedItems[0] as CommandableCommit;
                var newItem = e.AddedItems[0] as CommandableCommit;
                if (oldItem != null)
                    oldItem.Selected = false;
                if (newItem != null)
                    newItem.Selected = true;
            }
            catch (Exception) { }
        }

        public void OnNavigatedTo(NavigationEventArgs e)
        {
            var pr = ((Repository, PullRequest))e.Parameter;
            Repo = pr.Item1;
            PullRequest = pr.Item2;
        }
    }
}
