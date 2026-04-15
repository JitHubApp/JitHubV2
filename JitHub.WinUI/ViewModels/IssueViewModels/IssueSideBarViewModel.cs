using JitHub.Models;
using JitHub.Models.Base;
using JitHub.WinUI.ViewModels.Base;
using CommunityToolkit.Mvvm.Input;
using Octokit;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;

namespace JitHub.WinUI.ViewModels.IssueViewModels
{
    //Issue side bar can be in issues and pull request
    public class IssueSideBarViewModel : RepoViewModel
    {
        private PullRequest? _pullRequest;
        private Issue _issue = null!;
        private bool _isPr;
        private bool _isCollaborator;
        private ICollection<IssueSidePanelItem> _items = [];

        #region reviewers
        private readonly Dictionary<string, User> _requestedReviewers = [];
        private ICollection<SelectableItem> _selectableReviewers = [];

        public ICollection<SelectableItem> SelectableReviewers
        {
            get => _selectableReviewers;
            set => SetProperty(ref _selectableReviewers, value);
        }
        #endregion

        #region assignee
        private readonly Dictionary<string, User> _assignees = [];
        private ICollection<SelectableItem> _selectableAssignees = [];
        public ICollection<SelectableItem> SelectableAssignees
        {
            get => _selectableAssignees;
            set => SetProperty(ref _selectableAssignees, value);
        }
        #endregion

        #region label
        private readonly Dictionary<long, Label> _labels = [];
        private ICollection<SelectableItem> _selectableLabels = [];
        public ICollection<SelectableItem> SelectableLabels
        {
            get => _selectableLabels;
            set => SetProperty(ref _selectableLabels, value);
        }
        #endregion

        public ICollection<IssueSidePanelItem> Items
        {
            get => _items;
            set => SetProperty(ref _items, value);
        }

        public ICommand LoadCommand { get; }
        public ICommand UpdateReviewersCommand { get; }
        public ICommand UpdateAssigneesCommand { get; }
        public ICommand UpdateLabelsCommand { get; }

        public IssueSideBarViewModel(Repository repo, PullRequest pr)
        {
            Repo = repo;
            _pullRequest = pr;
            _isPr = true;
            LoadCommand = new AsyncRelayCommand(Load);
            UpdateReviewersCommand = new AsyncRelayCommand<SelectableUser?>(UpdateReviewers);
            UpdateAssigneesCommand = new AsyncRelayCommand<SelectableUser?>(UpdateAssignees);
            UpdateLabelsCommand = new AsyncRelayCommand<SelectableLabel?>(UpdateLabels);
        }

        public IssueSideBarViewModel(Repository repo, Issue issue)
        {
            Repo = repo;
            _issue = issue;
            LoadCommand = new AsyncRelayCommand(Load);
            UpdateReviewersCommand = new AsyncRelayCommand<SelectableUser?>(UpdateReviewers);
            UpdateAssigneesCommand = new AsyncRelayCommand<SelectableUser?>(UpdateAssignees);
            UpdateLabelsCommand = new AsyncRelayCommand<SelectableLabel?>(UpdateLabels);
        }

        public async Task Load()
        {
            Loading = true;
            _isCollaborator = await IsCollaborator();
            ICollection<Collaborator> contributors = [];
            if (_isCollaborator)
            {
                contributors = await GitHubService.GetRepositoryContributors(Repo.Owner.Login, Repo.Name);
            }

            if (_isPr && _pullRequest is not null)
            {
                _issue = await GitHubService.GetIssue(Repo.Owner.Login, Repo.Name, _pullRequest.Number);
                LoadReviewers(contributors);
            }

            LoadAssignees(_issue, contributors);
            await LoadLabels(_issue);
            LoadItems();
            Loading = false;
        }

        private void LoadItems()
        {
            var items = new List<IssueSidePanelItem>();
            if (_isPr)
            {
                items.Add(new IssueSidePanelItem("Reviewers", SelectableReviewers));
            }
            items.Add(new IssueSidePanelItem("Assignees", SelectableAssignees));
            items.Add(new IssueSidePanelItem("Labels", SelectableLabels));
            Items = items;
        }

        private void LoadAssignees(Issue issue, IEnumerable<Collaborator> contributors)
        {
            _assignees.Clear();
            foreach (var assignee in issue.Assignees)
            {
                _assignees.Add(assignee.Login, assignee);
            }
            var assignees = new List<SelectableItem>();
            if (_isCollaborator)
            {
                assignees.AddRange(
                    contributors
                    .Select(c => new SelectableUser(c, UpdateAssigneesCommand) { Selected = _assignees.ContainsKey(c.Login) })
                );
            }
            else
            {
                assignees.AddRange(
                    _assignees
                        .Select(assignee => new SelectableUser(assignee.Value, null))
                );
            }
            SelectableAssignees = assignees;
        }

        private async Task LoadLabels(Issue issue)
        {
            var labels = await GitHubService.GetLabelsFromRepository(Repo.Owner.Login, Repo.Name);
            _labels.Clear();
            foreach (var label in issue.Labels)
            {
                _labels.Add(label.Id, label);
            }
            var selectableLabels = new List<SelectableItem>();
            if (_isCollaborator)
            {
                selectableLabels.AddRange(
                    labels
                    .Select(label => new SelectableLabel(label, UpdateLabelsCommand) { Selected = _labels.ContainsKey(label.Id) })
                );
            }
            else
            {
                selectableLabels.AddRange(
                    _labels
                        .Select(label => new SelectableLabel(label.Value, null))
                );
            }
            SelectableLabels = selectableLabels;
        }

        private void LoadReviewers(IEnumerable<Collaborator> contributors)
        {
            if (_pullRequest is null)
            {
                SelectableReviewers = [];
                return;
            }

            _requestedReviewers.Clear();
            foreach (var user in _pullRequest.RequestedReviewers)
            {
                _requestedReviewers.Add(user.Login, user);
            }
            var reviewers = new List<SelectableItem>();
            if (_isCollaborator)
            {
                reviewers.AddRange(
                    contributors
                    .Where(c => !string.Equals(c.Login, _pullRequest.User.Login))
                    .Select(c => new SelectableUser(c, UpdateReviewersCommand) { Selected = _requestedReviewers.ContainsKey(c.Login) })
                );
            }
            else
            {
                reviewers.AddRange(
                    _requestedReviewers
                        .Select(reviewer => new SelectableUser(reviewer.Value, null))
                );
            }
            
            SelectableReviewers = reviewers;
        }

        private async Task UpdateReviewers(SelectableUser? user)
        {
            if (!_isCollaborator || user is null || _pullRequest is null) return;
            Loading = true;
            if (user.Selected && !_requestedReviewers.ContainsKey(user.Login))
            {
                await GitHubService.CreatePullRequestReviewers(Repo.Owner.Login, Repo.Name, _pullRequest.Number, new List<string> { user.Login });
            }
            else if (!user.Selected && _requestedReviewers.ContainsKey(user.Login))
            {
                await GitHubService.RemovePullRequestReviewers(Repo.Owner.Login, Repo.Name, _pullRequest.Number, new List<string> { user.Login });
            }
            var contributors = await GitHubService.GetRepositoryContributors(Repo.Owner.Login, Repo.Name);
            _pullRequest = await GitHubService.GetPullRequest(Repo.Owner.Login, Repo.Name, _pullRequest.Number);
            LoadReviewers(contributors);
            Loading = false;
        }

        private async Task UpdateAssignees(SelectableUser? user)
        {
            if (!_isCollaborator || user is null) return;
            Loading = true;
            if (user.Selected && !_assignees.ContainsKey(user.Login))
            {
                await GitHubService.AssignIssue(Repo.Owner.Login, Repo.Name, _issue.Number, new List<string> { user.Login });
            }
            else if (!user.Selected && _assignees.ContainsKey(user.Login))
            {
                await GitHubService.RemoveAssignees(Repo.Owner.Login, Repo.Name, _issue.Number, new List<string> { user.Login });
            }
            var contributors = await GitHubService.GetRepositoryContributors(Repo.Owner.Login, Repo.Name);
            _issue = await GitHubService.GetIssue(Repo.Owner.Login, Repo.Name, _issue.Number);
            LoadAssignees(_issue, contributors);
            Loading = false;
        }

        private async Task UpdateLabels(SelectableLabel? label)
        {
            if (!_isCollaborator || label is null) return;
            Loading = true;
            if (label.Selected && !_labels.ContainsKey(label.Label.Id))
            {
                await GitHubService.AddLabelToIssue(Repo.Owner.Login, Repo.Name, _issue.Number, new List<string> { label.Label.Name });
            }
            else if (!label.Selected && _labels.ContainsKey(label.Label.Id))
            {
                await GitHubService.RemoveLabelFromIssue(Repo.Owner.Login, Repo.Name, _issue.Number, label.Label.Name);
            }
            _issue = await GitHubService.GetIssue(Repo.Owner.Login, Repo.Name, _issue.Number);
            await LoadLabels(_issue);
            Loading = false;
        }
    }
}


