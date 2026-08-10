using JitHub.WinUI.Helpers;
using JitHub.Models;
using JitHub.Models.PRConversation;
using JitHub.WinUI.ViewModels.Base;
using JitHub.WinUI.ViewModels.EmojiViewModels;
using CommunityToolkit.Mvvm.Input;
using JitHub.Models.LegacyGitHub;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using Windows.ApplicationModel.DataTransfer;
using JitHub.Services.Markdown;
using JitHub.Services;
using MarkdownRenderer.Images;

namespace JitHub.WinUI.ViewModels.UserViewModel
{
    [WinRT.GeneratedBindableCustomProperty]
    public partial class ReactionWithUsers
    {
        public ReactionType Type { get; set; }
        public List<string> Users { get; set; } = [];
        public bool Voted { get; set; }
        public ICommand ReactionCommand { get; set; } = null!;
        public string AutomationInstanceId { get; set; } = string.Empty;

        public ReactionWithUsers(
            ReactionType type,
            IEnumerable<string> users,
            bool voted,
            ICommand reactionCommand,
            string automationInstanceId)
        {
            Type = type;
            Users = users.ToList();
            Voted = voted;
            ReactionCommand = reactionCommand;
            AutomationInstanceId = automationInstanceId;
        }
    }

    [WinRT.GeneratedBindableCustomProperty]
    public partial class UserCommentBlockViewModel : RepoViewModel
    {
        private string _body = string.Empty;
        private bool _hasReaction;
        private DateTimeOffset _createdAt;
        private bool _showPic = true;
        private User _commenter = null!;
        // only for the issue/pr number
        private int _number;
        // for issue comment and review comment
        private long _commentId;

        private MenuItem _copyLinkMenuItem = null!;
        private MenuItem _quoteReplyMenuItem = null!;
        private EmojiPanelViewModel? _emojiPanelViewModel;
        private Dictionary<ReactionType, Reaction> _votesMap = [];
        private List<ReactionWithUsers> _reactionWithUsers = [];

        public string Body
        {
            get => _body;
            set
            {
                SetProperty(ref _body, value);
            }
        }

        public bool HasReaction
        {
            get => _hasReaction;
            set => SetProperty(ref _hasReaction, value);
        }

        public DateTimeOffset CreatedAt
        {
            get => _createdAt;
            set => SetProperty(ref _createdAt, value);
        }

        public bool ShowPic
        {
            get => _showPic;
            set => SetProperty(ref _showPic, value);
        }

        public User Commenter
        {
            get => _commenter;
            set
            {
                if (!SetProperty(ref _commenter, value))
                    return;

                OnPropertyChanged(nameof(CommenterDisplayName));
                OnPropertyChanged(nameof(AuthenticatedCommenterLogin));
                OnPropertyChanged(nameof(CommenterAvatarUrl));
            }
        }

        public string CommenterDisplayName => string.IsNullOrWhiteSpace(Commenter?.Login)
            ? LocalizedResourceText.GetString("Common.UnknownUser", "unknown")
            : Commenter.Login;

        public string? AuthenticatedCommenterLogin =>
            UserIdentityNavigationPolicy.GetRoutableLogin(Commenter?.Login);

        public string CommenterAvatarUrl => Commenter?.AvatarUrl ?? string.Empty;

        public string CommenterAvatarAutomationId => $"{MarkdownAutomationId}_Author";

        public ICommand LoadCommand { get; }

        public string MarkdownAutomationId => $"LegacyComment_{_commentId}";

        public string HeaderReactionAutomationId => $"{MarkdownAutomationId}_HeaderReactions";

        public string SummaryReactionAutomationId => $"{MarkdownAutomationId}_SummaryReactions";

        public string OverflowAutomationId => $"{MarkdownAutomationId}_Actions";

        public string CopyLinkAutomationId => $"{MarkdownAutomationId}_CopyLink";

        public string QuoteReplyAutomationId => $"{MarkdownAutomationId}_QuoteReply";

        public MarkdownDocumentSource? MarkdownSource => Repo?.Owner?.Login is string owner &&
            !string.IsNullOrWhiteSpace(owner) &&
            !string.IsNullOrWhiteSpace(Repo.Name)
                ? MarkdownDocumentSourceFactory.CreateRepositoryDocument(
                    "legacy-comment",
                    _commentId.ToString(),
                    owner,
                    Repo.Name,
                    Repo.DefaultBranch)
                : null;

        public IAsyncRelayCommand<ReactionType> ReactionCommand { get; }
        public ICommand? RemoveReactionCommand { get; }

        public MenuItem CopyLinkMenuItem
        {
            get => _copyLinkMenuItem;
            set => SetProperty(ref _copyLinkMenuItem, value);
        }
        public MenuItem QuoteReplyMenuItem
        {
            get => _quoteReplyMenuItem;
            set => SetProperty(ref _quoteReplyMenuItem, value);
        }

        public EmojiPanelViewModel? EmojiPanelViewModel
        {
            get => _emojiPanelViewModel;
            set => SetProperty(ref _emojiPanelViewModel, value);
        }

        public List<ReactionWithUsers> ReactionWithUsers
        {
            get => _reactionWithUsers;
            set => SetProperty(ref _reactionWithUsers, value);
        }

        public UserCommentBlockViewModel(Repository repo, Issue issue, ICommand quoteReplyCommand)
        {
            Model = repo;
            Body = issue.Body ?? string.Empty;
            CreatedAt = issue.CreatedAt;
            _number = issue.Number;
            _commentId = issue.Id;
            Commenter = issue.User ?? new User();
            var copyLinkCommand  = new RelayCommand(() => CopyLink(issue.HtmlUrl, issue.Id.ToString()));
            CopyLinkMenuItem = new MenuItem(
                LocalizedResourceText.GetString("Comment.Menu.CopyLink", "Copy Link"),
                copyLinkCommand);
            QuoteReplyMenuItem = new MenuItem(
                LocalizedResourceText.GetString("Comment.Menu.QuoteReply", "Quote Reply"),
                quoteReplyCommand);
            ReactionCommand = new AsyncRelayCommand<ReactionType>(
                type => ReactToIssue(type, Repo.Id, _number),
                AsyncRelayCommandOptions.None);
            LoadCommand = new AsyncRelayCommand(LoadFromIssue);
        }

        public UserCommentBlockViewModel(IssueCommentNode comment)
        {
            Model = comment.Repo;
            Body = comment.Body ?? string.Empty;
            CreatedAt = comment.CreatedAt;
            _number = comment.Number;
            _commentId = comment.Id;
            CopyLinkMenuItem = new MenuItem(
                LocalizedResourceText.GetString("Comment.Menu.CopyLink", "Copy Link"),
                comment.CopyLinkCommand);
            QuoteReplyMenuItem = new MenuItem(
                LocalizedResourceText.GetString("Comment.Menu.QuoteReply", "Quote Reply"),
                comment.QuoteReplyCommand ?? new RelayCommand<string?>(_ => { }),
                comment.Body ?? string.Empty);
            ReactionCommand = new AsyncRelayCommand<ReactionType>(
                type => ReactToIssueComment(type, Repo.Id, comment.Id),
                AsyncRelayCommandOptions.None);
            Commenter = comment.User ?? new User();
            LoadCommand = new AsyncRelayCommand(LoadFromIssueComment);
        }

        public UserCommentBlockViewModel(ReviewCommentNode comment, ICommand quoteReplyCommand)
        {
            Model = comment.Repo;
            Body = comment.Body ?? string.Empty;
            _number = comment.Number;
            _commentId = comment.Id;
            CreatedAt = comment.CreatedAt;
            Commenter = comment.User ?? new User();
            var copyCommand = new RelayCommand(() => PlatformHelper.CopyString(comment.HtmlUrl));
            CopyLinkMenuItem = new MenuItem(
                LocalizedResourceText.GetString("Comment.Menu.CopyLink", "Copy Link"),
                copyCommand);
            QuoteReplyMenuItem = new MenuItem(
                LocalizedResourceText.GetString("Comment.Menu.QuoteReply", "Quote Reply"),
                quoteReplyCommand,
                comment.Body ?? string.Empty);
            ReactionCommand = new AsyncRelayCommand<ReactionType>(
                type => ReactToReviewComment(type, Repo.Id, comment.Id),
                AsyncRelayCommandOptions.None);
            LoadCommand = new AsyncRelayCommand(LoadFromReviewComment);
        }

        private async Task ReactToIssue(ReactionType type, long repoId, int number)
        {
            (string ownerLogin, string repoName) = GetRepoRoute();
            if (!_votesMap.ContainsKey(type))
            {
                await GitHubService.ReactToIssue(repoId, number, type);
            }
            else
            {
                var reaction = _votesMap[type];
                await GitHubService.DeleteIssueReaction(ownerLogin, repoName, number, reaction.Id);
            }

            await LoadFromIssue();
        }

        private async Task ReactToIssueComment(ReactionType type, long repoId, long commentId)
        {
            if (!_votesMap.ContainsKey(type))
            {
                await GitHubService.ReactToIssueComment(repoId, commentId, type);
            }
            else
            {
                var reaction = _votesMap[type];
                await GitHubService.DeleteIssueCommentReaction(repoId, commentId, reaction.Id);
            }
            await LoadFromIssueComment();
        }

        private async Task ReactToReviewComment(ReactionType type, long repoId, long commentId)
        {
            if (!_votesMap.ContainsKey(type))
            {
                await GitHubService.ReactToReviewComment(repoId, commentId, type);
            }
            else
            {
                var reaction = _votesMap[type];
                await GitHubService.DeleteReviewCommentReaction(repoId, commentId, reaction.Id);
            }
            await LoadFromReviewComment();
        }

        private async Task LoadFromIssueComment()
        {
            Loading = true;

            var reactions = await GitHubService.GetReactionFromIssueComment(Repo.Id, _commentId);
            SetReactions(reactions);

            Loading = false;
        }

        private async Task LoadFromIssue()
        {
            Loading = true;

            var reactions = await GitHubService.GetReactionFromIssueAsync(Repo.Id, _number);
            SetReactions(reactions);

            Loading = false;
        }

        private async Task LoadFromReviewComment()
        {
            Loading = true;

            var reactions = await GitHubService.GetReactionFromReviewComment(Repo.Id, _commentId);
            SetReactions(reactions);

            Loading = false;
        }

        private void CopyLink(string htmlUrl, string id)
        {
            var dataPackage = new DataPackage();
            dataPackage.SetText($"{htmlUrl}#issue-{id}");
            Clipboard.SetContent(dataPackage);
        }

        //TODO: this is getting called twice
        //      second time is with old data.
        //      sigh... we need functional programming
        private void SetReactions(ICollection<Reaction> reactions)
        {
            var userReactions = new Dictionary<ReactionType, ICollection<string>>();
            var votesMap = new Dictionary<ReactionType, Reaction>();
            foreach (var reaction in reactions)
            {
                if (!userReactions.ContainsKey(reaction.Content.Value))
                {
                    userReactions.Add(reaction.Content.Value, new List<string> { reaction.User?.Login ?? string.Empty });
                }
                else
                {
                    userReactions[reaction.Content.Value].Add(reaction.User?.Login ?? string.Empty);
                }

                if (!votesMap.ContainsKey(reaction.Content.Value) &&
                    string.Equals(reaction.User?.Login, User?.Login, StringComparison.Ordinal))
                {
                    votesMap.Add(reaction.Content.Value, reaction);
                }
            }

            _votesMap = votesMap;

            ReactionWithUsers = userReactions
                .Select(userReaction => new ReactionWithUsers(
                    userReaction.Key,
                    userReaction.Value,
                    votesMap.ContainsKey(userReaction.Key),
                    ReactionCommand,
                    SummaryReactionAutomationId)
                )
                .ToList();
            
            HasReaction = userReactions.Count > 0;
            if (EmojiPanelViewModel == null)
            {
                EmojiPanelViewModel = new EmojiPanelViewModel()
                {
                    UserReactions = userReactions,
                    VotesMap = votesMap,
                    ReactionCommand = ReactionCommand,
                };
            }
            else
            {
                EmojiPanelViewModel.UserReactions = userReactions;
                EmojiPanelViewModel.VotesMap = votesMap;
            }
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



