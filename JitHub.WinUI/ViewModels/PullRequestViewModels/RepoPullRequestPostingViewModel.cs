using JitHub.Services;
using JitHub.WinUI.ViewModels.Base;
using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.Mvvm.Input;
using JitHub.Models.LegacyGitHub;
using NewPullRequest = JitHub.Models.LegacyGitHub.NewPullRequest;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Input;

namespace JitHub.WinUI.ViewModels.PullRequestViewModels
{
    public class RepoPullRequestPostingViewModel : RepoViewModel
    {
        private ICollection<Branch> _branches = [];
        private ICollection<Branch> _headBranches = [];
        private Branch? _selectedBase;
        private Branch? _selectedHead;
        private string _title = string.Empty;
        private string _body = string.Empty;
        private CompareResult? _compareResult;
        private bool _selected;
        private string _totalCommits = string.Empty;
        private string _filesChanged = string.Empty;
        private string _commentsCount = string.Empty;
        private string _authorsCount = string.Empty;

        public ICollection<Branch> Branches
        {
            get => _branches;
            set => SetProperty(ref _branches, value);
        }
        public ICollection<Branch> HeadBranches
        {
            get => _headBranches;
            set => SetProperty(ref _headBranches, value);
        }
        public Branch? SelectedBase
        {
            get => _selectedBase;
            set => SetProperty(ref _selectedBase, value);
        }
        public Branch? SelectedHead
        {
            get => _selectedHead;
            set => SetProperty(ref _selectedHead, value);
        }
        public string Title
        {
            get => _title;
            set => SetProperty(ref _title, value);
        }
        public string Body
        {
            get => _body;
            set => SetProperty(ref _body, value);
        }
        public CompareResult? CompareResult
        {
            get => _compareResult;
            set => SetProperty(ref _compareResult, value);
        }
        public bool Selected
        {
            get => _selected;
            set => SetProperty(ref _selected, value);
        }
        public string TotalCommits
        {
            get => _totalCommits;
            set => SetProperty(ref _totalCommits, value);
        }
        public string FilesChanged
        {
            get => _filesChanged;
            set => SetProperty(ref _filesChanged, value);
        }
        public string CommentsCount
        {
            get => _commentsCount;
            set => SetProperty(ref _commentsCount, value);
        }
        public string AuthorsCount
        {
            get => _authorsCount;
            set => SetProperty(ref _authorsCount, value);
        }
        public ICommand? SuccessCallbackCommand { get; set; }
        public ICommand LoadCommand { get; }
        public ICommand CreateCommand { get; }
        public ICommand CompareCommand { get; }

        public RepoPullRequestPostingViewModel()
        {
            LoadCommand = new AsyncRelayCommand(Init);
            CompareCommand = new AsyncRelayCommand(Compare);
            CreateCommand = new AsyncRelayCommand(Create);
        }

        public async Task Init()
        {
            Loading = true;
            (string owner, string name) = GetRepoRoute();
            var branches = await GitHubService.GetRepoBranches(owner, name);
            Branches = branches;
            HeadBranches = branches;
            SelectedBase = Branches.FirstOrDefault(branch => branch.Name == Repo.DefaultBranch)
                ?? Branches.FirstOrDefault();
            SelectedHead = SelectedBase;
            Loading = false;
        }

        public async Task Create()
        {
            if (string.IsNullOrWhiteSpace(Title) || SelectedHead is null || SelectedBase is null)
            {
                return;
            }

            (string owner, string name) = GetRepoRoute();
            await GitHubService.CreatePullRequest(owner, name, new NewPullRequest(Title, SelectedHead.Name, SelectedBase.Name) { Body = Body });
            if (SuccessCallbackCommand?.CanExecute(null) == true)
            {
                SuccessCallbackCommand.Execute(null);
            }
        }

        public async Task Compare()
        {
            if (SelectedBase is null || SelectedHead is null)
            {
                return;
            }

            Loading = true;
            Selected = false;
            (string owner, string name) = GetRepoRoute();
            CompareResult = await GitHubService.CompareCommits(owner, name, SelectedBase.Name, SelectedHead.Name);
            TotalCommits = $"{CompareResult.TotalCommits} commits";
            FilesChanged = $"{CompareResult.Files.Count} files changed";
            var stringBuilder = new StringBuilder();
            var commentCount = 0;
            var commits = CompareResult.Commits.Select(commit => commit.Commit);
            foreach (var commit in commits)
            {
                if (!string.IsNullOrWhiteSpace(commit.Message))
                {
                    stringBuilder.Append(commit.Message);
                    stringBuilder.Append("\n");
                }
                commentCount += commit.CommentCount;
            }
            Body = stringBuilder.ToString();
            var authorsCount = GitHubService.GetContributorsCountFromCompareResult(owner, name, CompareResult);
            CommentsCount = $"{commentCount} comments";
            AuthorsCount = $"{authorsCount} contributors";
            Loading = false;
            Selected = true;
        }

        private (string OwnerLogin, string RepoName) GetRepoRoute()
        {
            string ownerLogin = Repo?.Owner?.Login
                ?? throw new InvalidOperationException("Repository owner information is required.");
            string repoName = Repo?.Name
                ?? throw new InvalidOperationException("Repository name is required.");

            return (ownerLogin, repoName);
        }
    }
}




