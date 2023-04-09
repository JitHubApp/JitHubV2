using Octokit;

namespace JitHub.ViewModels.ActivityViewModels
{
    public class GollumActivityViewModel : ActivityViewModel
    {
        private ActivityPayload _payload;
        public ActivityPayload Payload
        {
            get => _payload;
            set => SetProperty(ref _payload, value);
        }
        public GollumActivityViewModel(Activity activity) : base(activity)
        {
            Payload = activity.Payload;
            // TODO: change this into a real activity event once the SDK supports it
        }
    }
}
