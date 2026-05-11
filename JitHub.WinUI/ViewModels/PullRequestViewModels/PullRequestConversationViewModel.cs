using JitHub.Models.PRConversation;
using JitHub.Services;
using JitHub.WinUI.ViewModels.Base;
using JitHub.WinUI.ViewModels.IssueViewModels;
using JitHub.WinUI.ViewModels.UserViewModel;
using JitHub.WinUI.Views.Controls.PullRequest;
using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.Mvvm.Input;
using JitHub.Models.LegacyGitHub;
using MergePullRequest = JitHub.Models.LegacyGitHub.MergePullRequest;
using PullRequestMergeMethod = JitHub.Models.LegacyGitHub.PullRequestMergeMethod;
using PullRequestUpdate = JitHub.Models.LegacyGitHub.PullRequestUpdate;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Input;

namespace JitHub.WinUI.ViewModels.PullRequestViewModels
{
    public class PullRequestConversationViewModel : RepoViewModel
    {
        private readonly ModalService _modalService;
        private Issue _asIssue = null!;
        private PullRequest _pullRequest = null!;
        private List<ConversationNode> _comments = [];
        private bool _isCollaborator;
        private IssueSideBarViewModel _prModel = null!;
        private readonly ICommand _refreshCommand;
        private bool _branchExist;
        private string _commentText = string.Empty;
        private string _closeButtonText = string.Empty;
        private UserCommentBlockViewModel _bodyViewModel = null!;
        private readonly Action _scrollToBottom;
        #region merge
        private string _mergeTitle = string.Empty;
        private string _mergeMessage = string.Empty;
        private bool _canMerge;
        private PullRequestMergeMethod _mergeMethod;

        public string MergeTitle
        {
            get => _mergeTitle;
            set => SetProperty(ref _mergeTitle, value);
        }
        public string MergeMessage
        {
            get => _mergeMessage;
            set => SetProperty(ref _mergeMessage, value);
        }
        public bool CanMerge
        {
            get => _canMerge;
            set => SetProperty(ref _canMerge, value);
        }
        public PullRequestMergeMethod MergeMethod
        {
            get => _mergeMethod;
            set => SetProperty(ref _mergeMethod, value);
        }
        #endregion

        public PullRequest PullRequest
        {
            get => _pullRequest;
            set
            {
                SetProperty(ref _pullRequest, value);
                CanMerge = value.Mergeable ?? false;
            }
        }
        public List<ConversationNode> Comments
        {
            get => _comments;
            set => SetProperty(ref _comments, value);
        }
        public bool UserIsCollaborator
        {
            get => _isCollaborator;
            set => SetProperty(ref _isCollaborator, value);
        }
        public IssueSideBarViewModel PRModel
        {
            get => _prModel;
            set => SetProperty(ref _prModel, value);
        }
        public bool BranchExist
        {
            get => _branchExist;
            set => SetProperty(ref _branchExist, value);
        }
        public string CommentText
        {
            get => _commentText;
            set => SetProperty(ref _commentText, value);
        }
        public string CloseButtonText
        {
            get => _closeButtonText;
            set => SetProperty(ref _closeButtonText, value);
        }
        public UserCommentBlockViewModel BodyViewModel
        {
            get => _bodyViewModel;
            set => SetProperty(ref _bodyViewModel, value);
        }
        public ICommand LoadCommand { get; }
        public ICommand ReloadPullRequestCommand { get; }
        public ICommand QuoteReplyCommand { get; }

        // takes one argument UIElement
        public ICommand ScrollToElementCommand { get; }
        

        public PullRequestConversationViewModel(Repository repo, PullRequest pr, ICommand refresh, Action scrollToBottom, ICommand scrollToElement)
        {
            ArgumentNullException.ThrowIfNull(repo);
            ArgumentNullException.ThrowIfNull(pr);
            ArgumentNullException.ThrowIfNull(refresh);
            ArgumentNullException.ThrowIfNull(scrollToBottom);
            ArgumentNullException.ThrowIfNull(scrollToElement);

            Repo = repo;
            PullRequest = pr;
            _refreshCommand = refresh;
            _scrollToBottom = scrollToBottom;
            QuoteReplyCommand = new RelayCommand<string?>(QuoteReply);
            LoadCommand = new AsyncRelayCommand(Load);
            ReloadPullRequestCommand = new AsyncRelayCommand(ReloadPullRequest);
            ScrollToElementCommand = scrollToElement;
            _modalService = Ioc.Default.GetService<ModalService>()
                ?? throw new InvalidOperationException("ModalService is not registered.");
        }

        private void QuoteReply(string? content)
        {
            var lines = (content ?? string.Empty).Split('\n');
            var stringBuilder = new StringBuilder();
            foreach (var line in lines)
            {
                stringBuilder.AppendLine($"> {line}\n");
            }
            CommentText = stringBuilder.ToString();
            _scrollToBottom();
        }

        public async Task OnNavigatedTo()
        {
            Loading = true;
            _asIssue = await GitHubService.GetIssue(Repo.Id, PullRequest.Number);
            BodyViewModel = new UserCommentBlockViewModel(Repo, _asIssue, QuoteReplyCommand);
            PRModel = new IssueSideBarViewModel(Repo, PullRequest);
            await PRModel.Load();
            await Load();
        }

        private void SetCloseButtonText()
        {
            var builder = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(CommentText))
                builder.Append("Comment & ");
            if (PullRequest.State == ItemState.Closed)
                builder.Append("Reopen");
            else
                builder.Append("Close");
            builder.Append(" Pull Request");
            CloseButtonText = builder.ToString();
        }

        public async Task ReloadPullRequest()
        {
            Loading = true;
            (string ownerLogin, string repoName) = GetRepoRoute();
            PullRequest = await GitHubService.GetPullRequest(ownerLogin, repoName, PullRequest.Number);
            UserIsCollaborator = await IsCollaborator();
            Loading = false;
        }

        public async Task Load()
        {
            Loading = true;
            (string ownerLogin, string repoName) = GetRepoRoute();
            PullRequest = await GitHubService.GetPullRequest(ownerLogin, repoName, PullRequest.Number);
            try
            {
                var branch = await GitHubService.GetBranch(ownerLogin, repoName, PullRequest.Head.Ref);
                BranchExist = branch != null;
            }
            catch (Exception)
            {
                BranchExist = false;
            }
            UserIsCollaborator = await IsCollaborator();
            var comments = await GitHubService.GetPRConversationNodesAsync(Repo, PullRequest);
            var commentList = new List<ConversationNode>();
            foreach (var comment in comments)
            {
                if (comment is IssueCommentNode issueComment)
                {
                    issueComment.QuoteReplyCommand = QuoteReplyCommand;
                }
                else if (comment is ReviewNode review)
                {
                    review.ScrollToElementCommand = ScrollToElementCommand;
                }
                commentList.Add(comment);
            }
            Comments = commentList;
            SetCloseButtonText();
            Loading = false;
        }

        override public async Task<bool> IsCollaborator()
        {
            var isCollaborator = await base.IsCollaborator();
            //&& !(!(PullRequest.MaintainerCanModify == null ? true : (bool)PullRequest.MaintainerCanModify) && IsAuthor)
            //could be at some point we need to check this property
            return (isCollaborator || string.Equals(User?.Login, PullRequest.User?.Login, StringComparison.Ordinal)) && BranchExist;
        }

        public async void Merge()
        {
            try
            {
                var mergeRequest = new MergePullRequest()
                {
                    CommitTitle = MergeTitle,
                    CommitMessage = MergeMessage,
                    MergeMethod = MergeMethod,
                };
                _ = await GitHubService.MergePullRequest(Repo.Id, PullRequest.Number, mergeRequest);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to merge pull request: {ex}");
            }
        }

        public async void ClosePullRequest()
        {
            try
            {
                if (UserIsCollaborator)
                {
                    await Comment();
                    var updateRequest = new PullRequestUpdate()
                    {
                        Title = PullRequest.Title,
                        State = PullRequest.State == ItemState.Closed ? ItemState.Open : ItemState.Closed,
                    };
                    try
                    {
                        await GitHubService.UpdatePullRequest(Repo.Id, PullRequest.Number, updateRequest);

                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Failed to update pull request state: {ex}");
                    }
                    if (_refreshCommand != null && _refreshCommand.CanExecute(null))
                    {
                        _refreshCommand.Execute(null);
                    }
                    await Load();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to close pull request: {ex}");
            }
        }

        private async Task Comment()
        {
            if (!string.IsNullOrWhiteSpace(CommentText))
            {
                (string ownerLogin, string repoName) = GetRepoRoute();
                await GitHubService.SubmitComment(ownerLogin, repoName, PullRequest.Number, CommentText);
                CommentText = string.Empty;
            }
        }

        public async void HandleComment()
        {
            try
            {
                await Comment();
                await Load();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to submit pull request comment: {ex}");
            }
        }

        private async Task HandleRefresh()
        {
            if (_refreshCommand.CanExecute(null))
            {
                _refreshCommand.Execute(null);
            }
            await Load();
        }

        private void UseMerge()
        {
            _modalService.Open("Merge Pull Request", new MergeForm(Repo, PullRequest, MergeMethod, new AsyncRelayCommand(HandleRefresh)));
        }

        public void HandleMergeCommit()
        {
            MergeMethod = PullRequestMergeMethod.Merge;
            UseMerge();
        }

        public void HandleSquashMerge()
        {
            MergeMethod = PullRequestMergeMethod.Squash;
            UseMerge();
        }

        public void HandleRebaseMerge()
        {
            MergeMethod = PullRequestMergeMethod.Rebase;
            UseMerge();
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





