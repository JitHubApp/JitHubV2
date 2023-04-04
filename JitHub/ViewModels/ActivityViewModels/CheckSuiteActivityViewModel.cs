using Octokit;

namespace JitHub.ViewModels.ActivityViewModels
{
    public class CheckSuiteActivityViewModel : ActivityViewModel
    {
        private string _action;
        private CheckSuite _checkSuite;

        public string Action
        {
            get => _action;
            set => SetProperty(ref _action, value);
        }
        public CheckSuite CheckSuite
        {
            get => _checkSuite;
            set => SetProperty(ref _checkSuite, value);
        }

        public CheckSuiteActivityViewModel(Activity activity) : base(activity)
        {
            var payload = (CheckSuiteEventPayload)activity.Payload;
            Action = payload.Action;
            CheckSuite = payload.CheckSuite;
        }
    }
}
