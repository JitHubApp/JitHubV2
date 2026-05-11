using JitHub.WinUI.ViewModels.IssueViewModels;
using JitHub.WinUI.ViewModels.UserViewModel;
using CommunityToolkit.Mvvm.Input;
using JitHub.Models.LegacyGitHub;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Input;
using Windows.ApplicationModel.DataTransfer;

namespace JitHub.Models.PRConversation
{
    [WinRT.GeneratedBindableCustomProperty]
    public partial class ReviewCommentNode : ConversationNode
    {
        public long? InReplyToId { get; set; }
        public ReactionSummary Reactions { get; set; } = new();
        public string PullRequestUrl { get; set; } = string.Empty;
        public string HtmlUrl { get; set; } = string.Empty;
        public string CommitId { get; set; } = string.Empty;
        public long? PullRequestReviewId { get; set; }
        public int? OriginalPosition { get; set; }
        public int? Position { get; set; }
        public string Path { get; set; } = string.Empty;
        public string DiffHunk { get; set; } = string.Empty;
        public string NodeId { get; set; } = string.Empty;
        public long Id { get; set; }
        public string Url { get; set; } = string.Empty;
        public string OriginalCommitId { get; set; } = string.Empty;
        public string Body { get; set; } = string.Empty;
        public User User { get; set; } = null!;
        public StringEnum<AuthorAssociation> AuthorAssociation { get; set; }
        public ICollection<ReviewCommentNode> Replies { get; set; } = new List<ReviewCommentNode>();
        public ICommand CopyLinkCommand { get; set; } = null!;

        // For single comments for blocks of code
        public ReviewCommentNode(PullRequestReviewComment comment, Repository repo, int number) : base(repo, number)
        {
            CreatedAt = comment.CreatedAt;
            UpdatedAt = comment.UpdatedAt;
            Object = comment;
            User = comment.User;
            Body = comment.Body;
            InReplyToId = comment.InReplyToId;
            Reactions = comment.Reactions;
            PullRequestUrl = comment.PullRequestUrl;
            HtmlUrl = comment.HtmlUrl;
            CommitId = comment.CommitId;
            PullRequestReviewId = comment.PullRequestReviewId;
            OriginalPosition = comment.OriginalPosition;
            Position = comment.Position;
            Path = comment.Path;
            DiffHunk = TrimDiffHunk(comment.DiffHunk);
            NodeId = comment.NodeId;
            Id = comment.Id;
            Url = comment.HtmlUrl;
            OriginalCommitId = comment.OriginalCommitId;
            AuthorAssociation = comment.AuthorAssociation;
            CopyLinkCommand = new RelayCommand(CopyLink);
        }

        private void CopyLink()
        {
            var dataPackage = new DataPackage();
            dataPackage.SetText(Url);
            Clipboard.SetContent(dataPackage);
        }

        // Only take the last 4 lines of diff hunk to be more concise
        // The one that the comment is about is the last line
        private string TrimDiffHunk(string? ogHunk)
        {
            var lines = (ogHunk ?? string.Empty).Split('\n');
            IEnumerable<string> res;
            if (lines.Length > 4)
            {
                res = lines.TakeLast(4);
            }
            else
            {
                res = lines;
            }
            var builder = new StringBuilder();
            foreach (var line in res)
            {
                builder.AppendLine(line);
            }
            return builder.ToString();
        }
    }
}

