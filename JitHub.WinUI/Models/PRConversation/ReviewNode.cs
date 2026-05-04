using JitHub.Models.LegacyGitHub;
using System;
using System.Collections.Generic;
using System.Windows.Input;

namespace JitHub.Models.PRConversation
{
    public class ReviewNode : ConversationNode
    {
        public long Id { get; set; }
        public string NodeId { get; set; } = string.Empty;
        public PullRequestReviewState State { get; set; }
        public string CommitId { get; set; } = string.Empty;
        public User User { get; set; } = null!;
        public string Body { get; set; } = string.Empty;
        public string HtmlUrl { get; set; } = string.Empty;
        public string PullRequestUrl { get; set; } = string.Empty;
        public StringEnum<AuthorAssociation> AuthorAssociation { get; set; }
        public DateTimeOffset SubmittedAt { get; set; }
        public ICollection<ReviewCommentNode> Comments { get; set; } = new List<ReviewCommentNode>();
        public ICommand? ScrollToElementCommand { get; set; }

        public ReviewNode(PullRequestReview review, Repository repo, int number) : base(repo, number)
        {
            CreatedAt = review.SubmittedAt;
            Object = review;
            Id = review.Id;
            NodeId = review.NodeId;
            State = review.State.Value;
            CommitId = review.CommitId;
            User = review.User;
            Body = review.Body;
            HtmlUrl = review.HtmlUrl;
            PullRequestUrl = review.PullRequestUrl;
            AuthorAssociation = review.AuthorAssociation;
            SubmittedAt = review.SubmittedAt;
        }
    }
}
