using JitHub.WinUI.Helpers;
using JitHub.Models;
using JitHub.Models.PRConversation;
using JitHub.WinUI.ViewModels.Base;
using JitHub.WinUI.ViewModels.EmojiViewModels;
using CommunityToolkit.Mvvm.Input;
using Octokit;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using Windows.ApplicationModel.DataTransfer;
using CommunityToolkit.WinUI.Controls;

namespace JitHub.WinUI.ViewModels.UserViewModel
{
    public class ReactionWithUsers
    {
        public ReactionType Type { get; set; }
        public ICollection<string> Users { get; set; } = [];
        public bool Voted { get; set; }
        public ICommand ReactionCommand { get; set; } = null!;

        public ReactionWithUsers(ReactionType type, ICollection<string> users, bool voted, ICommand reactionCommand)
        {
            Type = type;
            Users = users;
            Voted = voted;
            ReactionCommand = reactionCommand;
        }
    }

    public class UserCommentBlockViewModel : RepoViewModel
    {
        private string _body = string.Empty;
        private MarkdownConfig _markdownConfig = null!;
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
        private ICollection<ReactionWithUsers> _reactionWithUsers = [];

        public string Body
        {
            get => _body;
            set
            {
                SetProperty(ref _body, value);
            }
        }

        public MarkdownConfig MarkdownConfig
        {
            get => _markdownConfig;
            set => SetProperty(ref _markdownConfig, value);
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
            set => SetProperty(ref _commenter, value);
        }

        public ICommand LoadCommand { get; }

        public ICommand ReactionCommand { get; }
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

        public ICollection<ReactionWithUsers> ReactionWithUsers
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
            Commenter = issue.User!;
            var copyLinkCommand  = new RelayCommand(() => CopyLink(issue.HtmlUrl, issue.Id.ToString()));
            CopyLinkMenuItem = new MenuItem("Copy Link", copyLinkCommand);
            QuoteReplyMenuItem = new MenuItem("Quote Reply", quoteReplyCommand);
            ReactionCommand = new AsyncRelayCommand<ReactionType>(async type => await ReactToIssue(type, Repo.Id, _number));
            LoadCommand = new AsyncRelayCommand(LoadFromIssue);
            _markdownConfig = GitHubService.GetMarkdownConfig();
        }

        public UserCommentBlockViewModel(IssueCommentNode comment)
        {
            Model = comment.Repo;
            Body = comment.Body ?? string.Empty;
            CreatedAt = comment.CreatedAt;
            _number = comment.Number;
            _commentId = comment.Id;
            CopyLinkMenuItem = new MenuItem("Copy Link", comment.CopyLinkCommand);
            QuoteReplyMenuItem = new MenuItem(
                "Quote Reply",
                comment.QuoteReplyCommand ?? new RelayCommand<string?>(_ => { }),
                comment.Body ?? string.Empty);
            ReactionCommand = new AsyncRelayCommand<ReactionType>(async type => await ReactToIssueComment(type, Repo.Id, comment.Id));
            Commenter = comment.User!;
            LoadCommand = new AsyncRelayCommand(LoadFromIssueComment);
            _markdownConfig = GitHubService.GetMarkdownConfig();
        }

        public UserCommentBlockViewModel(ReviewCommentNode comment, ICommand quoteReplyCommand)
        {
            Model = comment.Repo;
            Body = comment.Body ?? string.Empty;
            _number = comment.Number;
            _commentId = comment.Id;
            CreatedAt = comment.CreatedAt;
            Commenter = comment.User!;
            var copyCommand = new RelayCommand(() => PlatformHelper.CopyString(comment.HtmlUrl));
            CopyLinkMenuItem = new MenuItem("Copy Link", copyCommand);
            QuoteReplyMenuItem = new MenuItem("Quote Reply", quoteReplyCommand, comment.Body ?? string.Empty);
            ReactionCommand = new RelayCommand<ReactionType>(async type => await ReactToReviewComment(type, Repo.Id, comment.Id));
            LoadCommand = new AsyncRelayCommand(LoadFromReviewComment);
            _markdownConfig = GitHubService.GetMarkdownConfig();
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

            LoadCommand.Execute(null);
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
            LoadCommand.Execute(null);
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
            LoadCommand.Execute(null);
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
                    ReactionCommand)
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



