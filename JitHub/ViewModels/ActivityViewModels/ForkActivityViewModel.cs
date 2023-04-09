using Octokit;

namespace JitHub.ViewModels.ActivityViewModels
{
    public class ForkActivityViewModel : ActivityViewModel
    {
        private Repository _forkee;

        public Repository Forkee
        {
            get => _forkee;
            set => SetProperty(ref _forkee, value);
        }

        public ForkActivityViewModel(Activity activity) : base(activity)
        {
            var payload = (ForkEventPayload)activity.Payload;
            Forkee = payload.Forkee;
        }
    }
}
