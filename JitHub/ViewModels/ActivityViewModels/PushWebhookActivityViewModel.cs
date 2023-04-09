using Octokit;
using System.Collections.Generic;
using System.Linq;

namespace JitHub.ViewModels.ActivityViewModels
{
    public class PushWebhookActivityViewModel : ActivityViewModel
    {
        private string _head;
        private string _before;
        private string _after;
        private string _ref;
        private string _baseRef;
        private bool _created;
        private bool _deleted;
        private bool _forced;
        private string _compare;
        private int _size;
        private ICollection<PushWebhookCommit> _commits;
        private PushWebhookCommit _headCommit;


        public string Head
        {
            get => _head;
            set => SetProperty(ref _head, value);
        }
        public string Before
        {
            get => _before;
            set => SetProperty(ref _before, value);
        }
        public string After
        {
            get => _after;
            set => SetProperty(ref _after, value);
        }
        public string Ref
        {
            get => _ref;
            set => SetProperty(ref _ref, value);
        }
        public string BaseRef
        {
            get => _baseRef;
            set => SetProperty(ref _baseRef, value);
        }
        public bool Created
        {
            get => _created;
            set => SetProperty(ref _created, value);
        }
        public bool Deleted
        {
            get => _deleted;
            set => SetProperty(ref _deleted, value);
        }
        public bool Forced
        {
            get => _forced;
            set => SetProperty(ref _forced, value);
        }
        public string Compare
        {
            get => _compare;
            set => SetProperty(ref _compare, value);
        }
        public int Size
        {
            get => _size;
            set => SetProperty(ref _size, value);
        }
        public ICollection<PushWebhookCommit> Commits
        {
            get => _commits;
            set => SetProperty(ref _commits, value);
        }
        public PushWebhookCommit HeadCommit
        {
            get => _headCommit;
            set => SetProperty(ref _headCommit, value);
        }

        public PushWebhookActivityViewModel(Activity activity) : base(activity)
        {
            var payload = (PushWebhookPayload)activity.Payload;
            Head = payload.Head;
            Before = payload.Before;
            After = payload.After;
            Ref = payload.Ref;
            BaseRef = payload.BaseRef;
            Created = payload.Created;
            Deleted = payload.Deleted;
            Forced = payload.Forced;
            Compare = payload.Compare;
            Size = payload.Size;
            Commits = payload.Commits.ToList();
            HeadCommit = payload.HeadCommit;
        }
    }
}
