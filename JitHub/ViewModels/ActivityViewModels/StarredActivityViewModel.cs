using Octokit;

namespace JitHub.ViewModels.ActivityViewModels
{
    public class StarredActivityViewModel : ActivityViewModel
    {
        private string _action;

        public string Action
        {
            get => _action;
            set => SetProperty(ref _action, value);
        }

        public StarredActivityViewModel(Activity activity) : base(activity)
        {
            var payload = (StarredEventPayload)activity.Payload;
            Action = payload.Action;
        }
    }
}
