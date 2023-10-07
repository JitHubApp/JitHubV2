using Octokit;

namespace JitHub.ViewModels.ActivityViewModels
{
    public class IssueCommentActivityViewModel : ActivityViewModel
    {
        private string _action;
        private Issue _issue;
        private IssueComment _comment;

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
        public IssueComment Comment
        {
            get => _comment;
            set
            {
                SetProperty(ref _comment, value);
                MarkdownConfig = GitHubService.GetMarkdownConfig(value.Body);
            }
        }

        public IssueCommentActivityViewModel(Activity activity) : base(activity)
        {
            var payload = (IssueCommentPayload)activity.Payload;
            Action = payload.Action;
            Issue = payload.Issue;
            Comment = payload.Comment;
        }
    }
}
