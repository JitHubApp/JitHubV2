using Octokit;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace JitHub.ViewModels.ActivityViewModels
{
    public class CommitCommentActivityViewModel : ActivityViewModel
    {
        private CommitComment _comment;

        public CommitComment Comment
        {
            get => _comment;
            set => SetProperty(ref _comment, value);
        }

        public CommitCommentActivityViewModel(Activity activity) : base(activity)
        {
            var payload = (CommitCommentPayload)activity.Payload;
            Comment = payload.Comment;
        }
    }
}
