using Octokit;

namespace JitHub.ViewModels.ActivityViewModels
{
    public class PullRequestCommentActivityViewModel : ActivityViewModel
    {
        private string _action;
        private PullRequest _pullRequest;
        private PullRequestReviewComment _comment;

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
        public PullRequestReviewComment Comment
        {
            get => _comment;
            set => SetProperty(ref _comment, value);
        }

        public PullRequestCommentActivityViewModel(Activity activity) : base(activity)
        {
            var payload = (PullRequestCommentPayload)activity.Payload;
            Action = payload.Action;
            PullRequest = payload.PullRequest;
            Comment = payload.Comment;
        }
    }
}
