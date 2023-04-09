using Octokit;

namespace JitHub.ViewModels.ActivityViewModels
{
    public class PullRequestReviewActivityViewModel : ActivityViewModel
    {
        private string _action;
        private PullRequest _pullRequest;
        private PullRequestReview _review;

        public string Action
        {
            get => _action;
            set => SetProperty(ref _action, value);
        }
        public PullRequest PullRequest
        {
            get => _pullRequest;
            set => SetProperty(ref _pullRequest, value);
        }
        public PullRequestReview Review
        {
            get => _review;
            set => SetProperty(ref _review, value);
        }

        public PullRequestReviewActivityViewModel(Activity activity) : base(activity)
        {
            var payload = (PullRequestReviewEventPayload)activity.Payload;
            Action = payload.Action;
            PullRequest = payload.PullRequest;
            Review = payload.Review;
        }
    }
}
