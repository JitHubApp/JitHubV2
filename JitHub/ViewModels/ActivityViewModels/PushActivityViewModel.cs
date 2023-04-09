using Octokit;
using System.Collections.Generic;
using System.Linq;

namespace JitHub.ViewModels.ActivityViewModels
{
    public class PushActivityViewModel : ActivityViewModel
    {
        private string _head;
        private string _ref;
        private int _size;
        private ICollection<Commit> _commits;

        public string Head
        {
            get => _head;
            set => SetProperty(ref _head, value);
        }
        public string Ref
        {
            get => _ref;
            set => SetProperty(ref _ref, value);
        }
        public int Size
        {
            get => _size;
            set => SetProperty(ref _size, value);
        }
        public ICollection<Commit> Commits
        {
            get => _commits;
            set => SetProperty(ref _commits, value);
        }

        public PushActivityViewModel(Activity activity) : base(activity)
        {
            var payload = (PushEventPayload)activity.Payload;
            Head = payload.Head;
            Ref = payload.Ref;
            Size = payload.Size;
            Commits = payload.Commits
                .Select(commit => new Commit(commit.NodeId, commit.Url, commit.Label, commit.Ref, commit.Sha, commit.User, Repo, commit.Message, commit.Author, commit.Committer, commit.Tree, commit.Parents == null ? new List<GitReference>() : commit.Parents, commit.CommentCount, commit.Verification))
                .ToList();
        }
    }
}
