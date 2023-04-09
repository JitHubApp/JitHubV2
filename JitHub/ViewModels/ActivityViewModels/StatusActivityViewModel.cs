using Octokit;
using System;
using System.Collections.Generic;
using System.Linq;

namespace JitHub.ViewModels.ActivityViewModels
{
    public class StatusActivityViewModel : ActivityViewModel
    {
        private string _name;
        private string _sha;
        private DateTimeOffset _statusCreatedAt;
        private DateTimeOffset _updatedAt;
        private StringEnum<CommitState> _state;
        private string _targetUrl;
        private string _description;
        private string _context;
        private long _statusId;
        private GitHubCommit _commit;
        private Organization _organization;
        private ICollection<Branch> _branches;

        public string Name
        {
            get => _name;
            set => SetProperty(ref _name, value);
        }
        public string Sha
        {
            get => _sha;
            set => SetProperty(ref _sha, value);
        }
        public DateTimeOffset StatusCreatedAt
        {
            get => _statusCreatedAt;
            set => SetProperty(ref _statusCreatedAt, value);
        }
        public DateTimeOffset UpdatedAt
        {
            get => _updatedAt;
            set => SetProperty(ref _updatedAt, value);
        }
        public StringEnum<CommitState> State
        {
            get => _state;
            set => SetProperty(ref _state, value);
        }
        public string TargetUrl
        {
            get => _targetUrl;
            set => SetProperty(ref _targetUrl, value);
        }
        public string Description
        {
            get => _description;
            set => SetProperty(ref _description, value);
        }
        public string Context
        {
            get => _context;
            set => SetProperty(ref _context, value);
        }
        public long StatusId
        {
            get => _statusId;
            set => SetProperty(ref _statusId, value);
        }
        public GitHubCommit Commit
        {
            get => _commit;
            set => SetProperty(ref _commit, value);
        }
        public Organization Organization
        {
            get => _organization;
            set => SetProperty(ref _organization, value);
        }
        public ICollection<Branch> Branches
        {
            get => _branches;
            set => SetProperty(ref _branches, value);
        }

        public StatusActivityViewModel(Activity activity) : base(activity)
        {
            var payload = (StatusEventPayload)activity.Payload;
            Name = payload.Name;
            Sha = payload.Sha;
            StatusCreatedAt = payload.CreatedAt;
            UpdatedAt = payload.UpdatedAt;
            State = payload.State;
            TargetUrl = payload.TargetUrl;
            Description = payload.Description;
            Context = payload.Context;
            StatusId = payload.Id;
            Commit = payload.Commit;
            Organization = payload.Organization;
            Branches = payload.Branches.ToList();
        }
    }
}
