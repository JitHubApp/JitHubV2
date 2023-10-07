using Octokit;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace JitHub.ViewModels.ActivityViewModels
{
    public class DeleteActivityViewModel : ActivityViewModel
    {
        private string _ref;

        private StringEnum<RefType> _refType;


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

        public DeleteActivityViewModel(Activity activity) : base(activity)
        {
            var payload = (DeleteEventPayload)activity.Payload;
            Ref = payload.Ref;
            RefType = payload.RefType;
        }
    }
}
