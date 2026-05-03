using JitHub.Models;
using JitHub.Models.Base;
using JitHub.Services;
using JitHub.WinUI.ViewModels.Base;
using JitHub.WinUI.ViewModels.UserViewModel;
using JitHub.WinUI.Views.Controls.Issue;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.Mvvm.Input;
using JitHub.Models.LegacyGitHub;
using IssueUpdate = JitHub.Models.LegacyGitHub.IssueUpdate;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Input;
using Windows.ApplicationModel.DataTransfer;

namespace JitHub.WinUI.ViewModels.IssueViewModels
{
    public class RepoIssueDetailViewModel : RepoViewModel
    {
        private Issue _issue = null!;
        private UserCommentBlockViewModel _bodyViewModel = null!;
        private readonly NavigationService _navigationService;
        private readonly ModalService _modalService;
        private readonly ICommandService _commandService;
        private ICollection<UserCommentBlockViewModel> _comments = [];
        private IssueSideBarViewModel _sideBarViewModel = null!;
        private string _text = string.Empty;
        private string _closeButtonText = string.Empty;
        private bool _userIsCollaborator;

        public ICollection<UserCommentBlockViewModel> Comments
        {
            get => _comments;
            set => SetProperty(ref _comments, value);
        }
        public Issue Issue
        {
            get => _issue;
            set => SetProperty(ref _issue, value);
        }
        public UserCommentBlockViewModel BodyViewModel
        {
            get => _bodyViewModel;
            set => SetProperty(ref _bodyViewModel, value);
        }
        public IssueSideBarViewModel SideBarViewModel
        {
            get => _sideBarViewModel;
            set => SetProperty(ref _sideBarViewModel, value);
        }
        public ICommand LoadCommentsCommand { get; }
        public ICommand CommentCommand { get; }
        public ICommand LoadCommand { get; }
        public ICommand CloseCommand { get; }
        public ICommand EditCommand { get; }
        public string Text
        {
            get => _text;
            set
            {
                SetProperty(ref _text, value);
                UpdateCloseButtonText();
            }
        }
        public string CloseButtonText
        {
            get => _closeButtonText;
            set => SetProperty(ref _closeButtonText, value);
        }
        public bool IsUserCollaborator
        {
            get => _userIsCollaborator;
            set => SetProperty(ref _userIsCollaborator, value);
        }

        public RepoIssueDetailViewModel()
        {
            _navigationService = Ioc.Default.GetService<NavigationService>()
                ?? throw new InvalidOperationException("NavigationService is not registered.");
            _modalService = Ioc.Default.GetService<ModalService>()
                ?? throw new InvalidOperationException("ModalService is not registered.");
            _commandService = Ioc.Default.GetService<ICommandService>()
                ?? throw new InvalidOperationException("ICommandService is not registered.");
            LoadCommentsCommand = new AsyncRelayCommand(LoadComments);
            LoadCommand = new AsyncRelayCommand(Load);
            CommentCommand = new AsyncRelayCommand(SubmitComment);
            CloseCommand = new AsyncRelayCommand(CloseComment);
            EditCommand = new RelayCommand(EditIssue);
        }

        public RepoIssueDetailViewModel(RepoSelectableItemModel<Issue> issueModel)
            : this()
        {
            Issue = issueModel.Model;
            Repo = issueModel.Repository;
            SetBody(Issue);
            SideBarViewModel = new IssueSideBarViewModel(Repo, Issue);
            UpdateCloseButtonText();
        }

        //setting issue body's view model and reaction block
        private void SetBody(Issue issue)
        {
            var quoteReplyCommand = new RelayCommand(() => QuoteReply(issue.Body ?? string.Empty));
            BodyViewModel = new UserCommentBlockViewModel(Repo, issue, quoteReplyCommand);
        }

        override public async Task<bool> IsCollaborator()
        {
            var isCollaborator = await base.IsCollaborator();
            return isCollaborator || string.Equals(User?.Login, Issue.User?.Login, StringComparison.Ordinal);
        }

        private async Task Load()
        {
            Loading = true;
            await LoadComments();
            Issue = await GitHubService.GetIssue(Repo.Id, Issue.Number);
            //SetBody(Issue);
            await SideBarViewModel.Load();
            IsUserCollaborator = await IsCollaborator();
            Loading = false;
        }

        private void UpdateCloseButtonText()
        {
            var builder = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(Text))
            {
                builder.Append("Comment & ");
            }

            if (Issue.State == ItemState.Closed)
            {
                builder.Append("Reopen");
            }
            else 
            {
                builder.Append("Close");
            }

            CloseButtonText = builder.ToString();
        }

        private async Task SubmitComment()
        {
            if (string.IsNullOrWhiteSpace(Text))
            {
                return;
            }

            (string ownerLogin, string repoName, int issueNumber) = GetIssueRoute();
            await GitHubService.SubmitComment(ownerLogin, repoName, issueNumber, Text);
            Text = string.Empty;
            await Refresh();
        }

        private void EditIssue()
        {
            _modalService.Open(
                "Edit Issue",
                new IssueForm(
                    _commandService.GetCommand(JitHubCommand.UpdateIssue),
                    Issue.Title,
                    Issue.Body,
                    Issue,
                    Repo.Id));
        }

        private async Task CloseComment()
        {
            var issueUpdate = new IssueUpdate
            {
                State = Issue.State == ItemState.Closed ? ItemState.Open : ItemState.Closed,
            };

            (string ownerLogin, string repoName, int issueNumber) = GetIssueRoute();
            if (!string.IsNullOrWhiteSpace(Text))
            {
                await GitHubService.SubmitComment(ownerLogin, repoName, issueNumber, Text);
                Text = string.Empty;
            }

            await GitHubService.CloseIssue(ownerLogin, repoName, issueNumber, issueUpdate);
            await Refresh();
        }

        private async Task Refresh()
        {
            (string ownerLogin, string repoName, int issueNumber) = GetIssueRoute();
            Issue = await GitHubService.GetIssue(ownerLogin, repoName, issueNumber);
            SetBody(Issue);
            await LoadComments();
            UpdateCloseButtonText();
        }

        private async Task LoadComments()
        {
            var comments = await GitHubService.GetIssueComments(Repo, Issue.Number);
            Comments = comments
                .Select(comment => new UserCommentBlockViewModel(comment))
                .ToList();
        }

        private void QuoteReply(string text)
        {
            var lines = text.Split('\n')
                .Select((line) => $"> {line}\n");
            var builder = new StringBuilder();
            foreach (var line in lines)
            {
                builder.Append(line);
            }
            Text = builder.ToString();
        }

        private (string OwnerLogin, string RepoName, int IssueNumber) GetIssueRoute()
        {
            string ownerLogin = Repo?.Owner?.Login
                ?? throw new InvalidOperationException("Repository owner information is required.");
            string repoName = Repo?.Name
                ?? throw new InvalidOperationException("Repository name is required.");

            return (ownerLogin, repoName, Issue.Number);
        }
    }
}




