using JitHub.Core.Services.Profile;
using JitHub.Core.Services.PullRequests;
using JitHub.WinUI.Models.LegacyGitHub;
using JitHub.WinUI.Models.PullRequests.Conversation;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace JitHub.WinUI.ViewModels.PullRequests.Conversation
{
public partial class ReviewNodeViewModel : RepoViewModel
    {
        private ObservableCollection<ReviewCommentViewModel> _blocks = [];
        private User? _reviewer;
        private PullRequestReviewState _state;
        private DateTimeOffset _submittedAt;
        private ReviewNode _review = null!;

        public ObservableCollection<ReviewCommentViewModel> Blocks
        {
            get => _blocks;
            set => SetProperty(ref _blocks, value);
        }

        public User? Reviewer
        {
            get => _reviewer;
            set
            {
                if (!SetProperty(ref _reviewer, value))
                    return;

                OnPropertyChanged(nameof(ReviewerDisplayName));
                OnPropertyChanged(nameof(AuthenticatedReviewerLogin));
            }
        }

        public string ReviewerDisplayName => UserIdentityNavigationPolicy.CreatePresentation(
            Reviewer?.Login,
            Reviewer?.Name,
            "unknown").DisplayName;

        public string? AuthenticatedReviewerLogin =>
            UserIdentityNavigationPolicy.GetRoutableLogin(Reviewer?.Login);

        public string ReviewerAvatarUrl => Reviewer?.AvatarUrl ?? string.Empty;

        public string ReviewerAutomationId => _review is null
            ? "PullRequestReview_unknown"
            : PullRequestReviewAutomationIdentity.CreateScope(
                "PullRequestReview",
                _review.Id,
                _review.NodeId,
                reviewId: null,
                position: null,
                originalPosition: null,
                createdAt: _review.SubmittedAt,
                deterministicContext: $"pr:{_review.Number}:review:{_review.AutomationOrdinal}");

        public PullRequestReviewState State
        {
            get => _state;
            set => SetProperty(ref _state, value);
        }

        public DateTimeOffset SubmittedAt
        {
            get => _submittedAt;
            set => SetProperty(ref _submittedAt, value);
        }

        public ReviewNodeViewModel(ReviewNode review)
        {
            Repo = review.Repo;
            Reviewer = review.User;
            State = review.State;
            SubmittedAt = review.SubmittedAt;
            _review = review;
            var dict = new Dictionary<long, ReviewCommentNode>();
            foreach (var comment in review.Comments)
            {
                if (!comment.InReplyToId.HasValue)
                {
                    dict.Add(comment.Id, comment);
                }
                else if (dict.ContainsKey(comment.InReplyToId.GetValueOrDefault()))
                {
                    dict[comment.InReplyToId.GetValueOrDefault()].Replies.Add(comment);
                }
            }
            Blocks = new ObservableCollection<ReviewCommentViewModel>();
            int threadOrdinal = 0;
            foreach (var comment in dict.Values)
            {
                Blocks.Add(new ReviewCommentViewModel(
                    Repo,
                    comment,
                    $"pr:{review.Number}:review:{review.AutomationOrdinal}:thread:{threadOrdinal}"));
                threadOrdinal++;
            }
        }
    }
}


