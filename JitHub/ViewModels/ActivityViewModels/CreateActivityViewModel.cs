using Octokit;

namespace JitHub.ViewModels.ActivityViewModels
{
    public class CreateActivityViewModel : ActivityViewModel
    {
        private string _ref;
        private StringEnum<RefType> _refType;
        private bool _isRepo;
        private string _mainBranch;
        private string _description;

        public string Ref
        {
            get => _ref;
            set => SetProperty(ref _ref, value);
        }
        public StringEnum<RefType> RefType
        {
            get => _refType;
            set => SetProperty(ref _refType, value);
        }
        public bool IsRepo
        {
            get => _isRepo;
            set => SetProperty(ref _isRepo, value);
        }
        public string MainBranch
        {
            get => _mainBranch;
            set => SetProperty(ref _mainBranch, value);
        }
        public string Description
        {
            get => _description;
            set => SetProperty(ref _description, value);
        }

        public CreateActivityViewModel(Activity activity) : base(activity)
        {
            var payload = (CreateEventPayload)activity.Payload;
            Ref = payload.Ref;
            RefType = payload.RefType;
            MainBranch = payload.MasterBranch;
            Description = payload.Description;
            IsRepo = RefType.Value == Octokit.RefType.Branch || RefType.Value == Octokit.RefType.Repository;
        }
    }
}
