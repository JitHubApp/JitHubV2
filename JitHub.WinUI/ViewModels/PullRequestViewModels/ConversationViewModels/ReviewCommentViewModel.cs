using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Windows.Input;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using JitHub.Models.LegacyGitHub;
using JitHub.Models.PRConversation;
using JitHub.Services;
using JitHub.Services.Markdown;
using JitHub.WinUI.ViewModels.Base;
using JitHub.WinUI.ViewModels.IssueViewModels;
using JitHub.WinUI.ViewModels.UserViewModel;
using MarkdownRenderer.Images;

namespace JitHub.WinUI.ViewModels.PullRequestViewModels.ConversationViewModels
{
    public class ReviewCommentViewModel : RepoViewModel
    {
        private readonly ICommand _quoteReplyCommand;
        private readonly string _automationScope;
        private readonly AsyncRelayCommand _replyCommand;
        private bool _replyBoxExpanded;
        private bool _isReplyInProgress;
        private string? _replyErrorMessage;
        private string _replyText = string.Empty;
        private string _diffHunk = string.Empty;
        private string _name = string.Empty;
        private User _author = null!;
        private ReviewCommentNode _reviewCommentNode = null!;
        private UserCommentBlockViewModel _userCommentBlockViewModel = null!;
        private ObservableCollection<UserCommentBlockViewModel> _replies = [];

        public bool ReplyBoxExpanded
        {
            get => _replyBoxExpanded;
            set => SetProperty(ref _replyBoxExpanded, value);
        }
        public string ReplyText
        {
            get => _replyText;
            set
            {
                if (SetProperty(ref _replyText, value))
                {
                    _replyCommand.NotifyCanExecuteChanged();
                }
            }
        }
        public bool IsReplyInProgress
        {
            get => _isReplyInProgress;
            private set
            {
                if (SetProperty(ref _isReplyInProgress, value))
                {
                    _replyCommand.NotifyCanExecuteChanged();
                }
            }
        }
        public string? ReplyErrorMessage
        {
            get => _replyErrorMessage;
            private set
            {
                if (SetProperty(ref _replyErrorMessage, value))
                {
                    OnPropertyChanged(nameof(HasReplyError));
                }
            }
        }
        public bool HasReplyError => !string.IsNullOrWhiteSpace(ReplyErrorMessage);
        public string DiffHunk
        {
            get => _diffHunk;
            set => SetProperty(ref _diffHunk, value);
        }
        public string Name
        {
            get => _name;
            set => SetProperty(ref _name, value);
        }
        public User Author
        {
            get => _author;
            set => SetProperty(ref _author, value);
        }
        public ReviewCommentNode ReviewCommentNode
        {
            get => _reviewCommentNode;
            set => SetProperty(ref _reviewCommentNode, value);
        }

        public UserCommentBlockViewModel BodyViewModel
        {
            get => _userCommentBlockViewModel;
            set => SetProperty(ref _userCommentBlockViewModel, value);
        }

        public ObservableCollection<UserCommentBlockViewModel> Replies
        {
            get => _replies;
            set => SetProperty(ref _replies, value);
        }

        public IAsyncRelayCommand ReplyCommand => _replyCommand;

        public event EventHandler? ReplyBoxRequested;

        public string ReplyAutomationId => $"{_automationScope}_Reply";
        public string ReplyExpanderAutomationId => $"{_automationScope}_ReplyExpander";
        public string ReplyFormAutomationId => $"{_automationScope}_ReplyForm";
        public string ReplyExpanderAutomationName => string.IsNullOrWhiteSpace(Author?.Login)
            ? "Reply to review comment"
            : $"Reply to review comment by {Author.Login}";
        public MarkdownDocumentSource? ReplyMarkdownSource => Repo?.Owner?.Login is string owner &&
            !string.IsNullOrWhiteSpace(owner) && !string.IsNullOrWhiteSpace(Repo.Name) && ReviewCommentNode is not null
                ? MarkdownDocumentSourceFactory.CreateRepositoryDocument(
                    "pull-request-review-reply-draft",
                    ReviewCommentNode.Id.ToString(),
                    owner,
                    Repo.Name,
                    gitRef: "HEAD")
                : null;

        public ReviewCommentViewModel(
            Repository repo,
            ReviewCommentNode comment,
            string deterministicContext)
        {
            ArgumentNullException.ThrowIfNull(comment);
            Repo = repo;
            _automationScope = CreateAutomationScope(comment, deterministicContext);
            DiffHunk = comment.DiffHunk ?? string.Empty;
            Name = comment.Path ?? string.Empty;
            Author = comment.User!;
            ReviewCommentNode = comment;
            _quoteReplyCommand = new RelayCommand<string?>(QuoteReply);
            BodyViewModel = new UserCommentBlockViewModel(comment, _quoteReplyCommand);
            foreach (var reply in comment.Replies)
            {
                Replies.Add(new UserCommentBlockViewModel(reply, _quoteReplyCommand));
            }
            _replyCommand = new AsyncRelayCommand(ReplyAsync, CanReply);
        }

        internal static string CreateAutomationScope(
            ReviewCommentNode? comment,
            string deterministicContext)
        {
            if (comment is null)
            {
                return string.Empty;
            }

            return PullRequestReviewAutomationIdentity.CreateScope(
                "ReviewComment",
                comment.Id,
                comment.NodeId,
                comment.PullRequestReviewId,
                comment.Position,
                comment.OriginalPosition,
                comment.CreatedAt,
                deterministicContext);
        }

        private bool CanReply() => !IsReplyInProgress && !string.IsNullOrWhiteSpace(ReplyText);

        private async Task ReplyAsync()
        {
            if (!CanReply())
            {
                return;
            }

            var replyText = ReplyText.Trim();
            IsReplyInProgress = true;
            ReplyErrorMessage = null;
            try
            {
                var reply = await GitHubService.ReplyToReview(Repo, ReviewCommentNode.Number, replyText, ReviewCommentNode.Id);
                ReplyText = string.Empty;
                var replyVM = new UserCommentBlockViewModel(reply, _quoteReplyCommand);
                Replies.Add(replyVM);
            }
            catch (Exception ex)
            {
                ReplyErrorMessage = JitHub.WinUI.Helpers.UserFacingError.For(
                    ex,
                    JitHub.WinUI.Helpers.UserFacingErrorKind.Action,
                    "pull-request-review-reply");
            }
            finally
            {
                IsReplyInProgress = false;
            }
        }

        private void ExpandReplyBox()
        {
            ReplyBoxExpanded = true;
        }

        private void QuoteReply(string? text)
        {
            ExpandReplyBox();
            var lines = (text ?? string.Empty).Split('\n')
                .Select((line) => $"> {line}\n");
            var builder = new StringBuilder();
            foreach (var line in lines)
            {
                builder.Append(line);
            }
            ReplyText = builder.ToString();
            ReplyBoxRequested?.Invoke(this, EventArgs.Empty);
        }
    }
}


