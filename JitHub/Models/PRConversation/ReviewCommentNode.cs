using JitHub.ViewModels.IssueViewModels;
using JitHub.ViewModels.UserViewModel;
using CommunityToolkit.Mvvm.Input;
using Octokit;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Input;
using Windows.ApplicationModel.DataTransfer;

namespace JitHub.Models.PRConversation
{
    public class ReviewCommentNode : ConversationNode
    {
        private ICommand _quoteReplyCommand;
        public long? InReplyToId { get; set; }
        public ReactionSummary Reactions { get; set; }
        public string PullRequestUrl { get; set; }
        public string HtmlUrl { get; set; }
        public string CommitId { get; set; }
        public long? PullRequestReviewId { get; set; }
        public int? OriginalPosition { get; set; }
        public int? Position { get; set; }
        public string Path { get; set; }
        public string DiffHunk { get; set; }
        public string NodeId { get; set; }
        public long Id { get; set; }
        public string Url { get; set; }
        public string OriginalCommitId { get; set; }
        public string Body { get; set; }
        public User User { get; set; }
        public StringEnum<AuthorAssociation> AuthorAssociation { get; set; }
        public ICollection<ReviewCommentNode> Replies { get; set; } = new List<ReviewCommentNode>();
        public ICommand CopyLinkCommand { get; set; }

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
        private string TrimDiffHunk(string ogHunk)
        {
            var lines = ogHunk.Split('\n');
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
