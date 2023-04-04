using Octokit;

namespace JitHub.ViewModels.ActivityViewModels
{
    public class IssueActivityViewModel : ActivityViewModel
    {
        private string _action;
        private Issue _issue;

        public string Action
        {
            get => _action;
            set => SetProperty(ref _action, value);
        }
        public Issue Issue
        {
            get => _issue;
            set => SetProperty(ref _issue, value);
        }

        public IssueActivityViewModel(Activity activity) : base(activity)
        {
            var payload = (IssueEventPayload)activity.Payload;
            Action = payload.Action;
            Issue = payload.Issue;
        }
    }
}
