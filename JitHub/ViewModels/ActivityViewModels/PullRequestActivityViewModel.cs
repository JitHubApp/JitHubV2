using Octokit;

namespace JitHub.ViewModels.ActivityViewModels
{
    public class PullRequestActivityViewModel : ActivityViewModel
    {
        private string _action;
        private int _number;
        private PullRequest _pullRequest;

        public string Action
        {
            get => _action;
            set => SetProperty(ref _action, value);
        }
        public int Number
        {
            get => _number;
            set => SetProperty(ref _number, value);
        }
        public PullRequest PullRequest
        {
            get => _pullRequest;
            set => SetProperty(ref _pullRequest, value);
        }

        public PullRequestActivityViewModel(Activity activity) : base(activity)
        {
            var payload = (PullRequestEventPayload)activity.Payload;
            Action = payload.Action;
            Number = payload.Number;
            PullRequest = payload.PullRequest;
        }
    }
}
