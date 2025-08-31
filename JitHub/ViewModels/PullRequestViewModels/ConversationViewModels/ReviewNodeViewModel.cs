using JitHub.Models.PRConversation;
using JitHub.ViewModels.Base;
using Octokit;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace JitHub.ViewModels.PullRequestViewModels.ConversationViewModels
{
    public class ReviewNodeViewModel : RepoViewModel
    {
        private ObservableCollection<ReviewCommentViewModel> _blocks;
        private User _reviewer;
        private PullRequestReviewState _state;
        private DateTimeOffset _submittedAt;
        private ReviewNode _review;

        public ObservableCollection<ReviewCommentViewModel> Blocks
        {
            get => _blocks;
            set => SetProperty(ref _blocks, value);
        }

        public User Reviewer
        {
            get => _reviewer;
            set => SetProperty(ref _reviewer, value);
        }

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
            foreach (var comment in dict.Values)
            {
                Blocks.Add(new ReviewCommentViewModel(Repo, comment, review.ScrollToElementCommand));
            }
        }
    }
}
