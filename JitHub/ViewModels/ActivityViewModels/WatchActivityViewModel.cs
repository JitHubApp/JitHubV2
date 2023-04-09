using Octokit;

namespace JitHub.ViewModels.ActivityViewModels
{
    public class WatchActivityViewModel : ActivityViewModel
    {
        private ActivityPayload _payload;
        private string _action;
        public ActivityPayload Payload
        {
            get => _payload;
            set => SetProperty(ref _payload, value);
        }
        public string Action
        {
            get => _action;
            set => SetProperty(ref _action, value);
        }
        public WatchActivityViewModel(Activity activity) : base(activity)
        {
            Payload = activity.Payload;
            var type = activity.Payload.GetType();
            if (type.Name == "StarredEventPayload")
            {
                Action = "starred";
            }
            else
            {
                Action = "started watching";
            }
            // TODO: change this into a real activity event once the SDK supports it
        }
    }
}
