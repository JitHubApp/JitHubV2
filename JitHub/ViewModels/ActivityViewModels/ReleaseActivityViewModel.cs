using Octokit;

namespace JitHub.ViewModels.ActivityViewModels
{
    public class ReleaseActivityViewModel : ActivityViewModel
    {
        private string _action;

        private Release _release;

        public string Action
        {
            get => _action;
            set => SetProperty(ref _action, value);
        }

        public Release Release
        {
            get => _release;
            set
            {
                SetProperty(ref _release, value);
                MarkdownConfig = GitHubService.GetMarkdownConfig();
                MarkdownText = value?.Body ?? string.Empty;
            }
        }

        public ReleaseActivityViewModel(Activity activity) : base(activity)
        {
            var payload = (ReleaseEventPayload)activity.Payload;
            Action = payload.Action;
            Release = payload.Release;
        }
    }
}
