using Octokit;

namespace JitHub.ViewModels.ActivityViewModels
{
    public class CheckRunActivityViewModel : ActivityViewModel
    {
        private string _action;
        private CheckRun _checkRun;
        private CheckRunRequestedAction _requestedAction;

        public string Action
        {
            get => _action;
            set => SetProperty(ref _action, value);
        }
        public CheckRun CheckRun
        {
            get => _checkRun;
            set => SetProperty(ref _checkRun, value);
        }
        public CheckRunRequestedAction RequestedAction
        {
            get => _requestedAction;
            set => SetProperty(ref _requestedAction, value);
        }

        public CheckRunActivityViewModel(Activity activity) : base(activity)
        {
            var payload = (CheckRunEventPayload)activity.Payload;
            Action = payload.Action;
            CheckRun = payload.CheckRun;
            RequestedAction = payload.RequestedAction;
        }
    }
}
