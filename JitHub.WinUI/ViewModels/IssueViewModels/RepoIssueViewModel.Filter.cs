using JitHub.Models;
using JitHub.Models.Filter;
using CommunityToolkit.Mvvm.Input;
using JitHub.Models.LegacyGitHub;
using IssueFilter = JitHub.Models.LegacyGitHub.IssueFilter;
using IssueSort = JitHub.Models.LegacyGitHub.IssueSort;
using ItemStateFilter = JitHub.Models.LegacyGitHub.ItemStateFilter;
using RepositoryIssueRequest = JitHub.Models.LegacyGitHub.RepositoryIssueRequest;
using SortDirection = JitHub.Models.LegacyGitHub.SortDirection;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows.Input;

namespace JitHub.WinUI.ViewModels.IssueViewModels
{
    public partial class RepoIssueViewModel
    {
        private EventfulCollection<FilterUnit> _filters = new EventfulCollection<FilterUnit>();
        private bool _isCollabotor;
        private RepositoryIssueRequest _repoIssueRequest = new();
        private const int _perPage = 50;

        public RepositoryIssueRequest RepoIssueRequest
        {
            get => _repoIssueRequest;
            set => SetProperty(ref _repoIssueRequest, value);
        }
        public bool FilterLoading
        {
            get => _filterLoading;
            set => SetProperty(ref _filterLoading, value);
        }
        public EventfulCollection<FilterUnit> Filters
        {
            get => _filters;
            set => SetProperty(ref _filters, value);
        }
        public ICommand LoadCommand { get; private set; } = null!;
        public ICommand FilterCommand { get; private set; } = null!;
        public ICommand ClearCommand { get; private set; } = null!;

        private void InitializeFilters()
        {
            RepoIssueRequest = new RepositoryIssueRequest();
            Filters = new EventfulCollection<FilterUnit>();
            LoadCommand = new AsyncRelayCommand(GetFilterParams);
            FilterCommand = new RelayCommand<Dictionary<string, FilterUnit>>(ApplyFilter);
            ClearCommand = new RelayCommand<Dictionary<string, FilterUnit>>(ApplyFilter);
        }

        private async Task GetFilterParams()
        {
            FilterLoading = true;
            Filters.Add(await GetLabels());
            Filters.Add(await GetMilestones());
            _isCollabotor = await IsCollaborator();
            if (_isCollabotor)
            {//TODO, collabotor can also do this
                var userFilters = await GetUsers();
                foreach (var userFilter in userFilters)
                {
                    Filters.Add(userFilter);
                }
            }
            Filters.Add(GetIssueFilterForAuthenticatedUser());
            Filters.Add(GetStates());
            Filters.Add(GetSorts());
            Filters.Add(GetDirections());
            Filters.Add(GetSince());
            FilterLoading = false;
        }

        private FilterUnit GetSince()
        {
            return new DateFilter(Repo.CreatedAt, "Date", "Pick a date");
        }

        private FilterUnit GetIssueFilterForAuthenticatedUser()
        {
            var selections = new List<Selection>
            {
                new Selection("Assigned", IssueFilter.Assigned.ToString(), IssueFilter.Assigned),
                new Selection("Created", IssueFilter.Created.ToString(), IssueFilter.Created),
                new Selection("Mentioned", IssueFilter.Mentioned.ToString(), IssueFilter.Mentioned),
                new Selection("Subscribed", IssueFilter.Subscribed.ToString(), IssueFilter.Subscribed),
                new Selection("All", IssueFilter.All.ToString(), IssueFilter.All)
            };
            var res = new DropdownFilter("Issue Filter");
            res.AddRange(selections);
            return res;
        }

        private FilterUnit GetDirections()
        {
            var selections = new List<Selection>
            {
                new Selection("asc", SortDirection.Ascending.ToString(), SortDirection.Ascending),
                new Selection("desc", SortDirection.Descending.ToString(), SortDirection.Descending)
            };
            var res = new DropdownFilter("Direction");
            res.AddRange(selections);
            return res;
        }

        private FilterUnit GetSorts()
        {
            var selections = new List<Selection>
            {
                new Selection("created", IssueSort.Created.ToString(), IssueSort.Created),
                new Selection("updated", IssueSort.Updated.ToString(), IssueSort.Updated),
                new Selection("comments", IssueSort.Comments.ToString(), IssueSort.Comments)
            };
            var res = new DropdownFilter("Sort");
            res.AddRange(selections);
            return res;
        }

        private FilterUnit GetStates()
        {
            var selections = new List<Selection>
            {
                new Selection("open", ItemStateFilter.Open.ToString(), ItemStateFilter.Open),
                new Selection("closed", ItemStateFilter.Closed.ToString(), ItemStateFilter.Closed),
                new Selection("all", ItemStateFilter.All.ToString(), ItemStateFilter.All)
            };
            var res = new DropdownFilter("State");
            res.AddRange(selections);
            return res;
        }

        private async Task<ICollection<FilterUnit>> GetUsers()
        {
            var users = await GitHubService.GetRepositoryCollaborators(Repo.Id);
            var assignees = new List<Selection>();
            var creators = new List<Selection>();
            var mentions = new List<Selection>();
            foreach (var user in users)
            {
                var userSelection = new Selection(user.Login, user.Login, user);
                assignees.Add(userSelection);
                creators.Add(userSelection);
                mentions.Add(userSelection);
            }
            var noneSelection = new Selection("none", string.Empty, string.Empty);
            var anySelection = new Selection("all", "*", "*");

            assignees.Add(noneSelection);
            assignees.Add(anySelection);
            creators.Add(noneSelection);
            creators.Add(anySelection);
            mentions.Add(noneSelection);
            mentions.Add(anySelection);

            var assigneeFilter = new DropdownFilter("Assignee");
            var creatorFilter = new DropdownFilter("Creator");
            var mentionFilter = new DropdownFilter("Mentioned");

            assigneeFilter.AddRange(assignees);
            creatorFilter.AddRange(creators);
            mentionFilter.AddRange(mentions);

            return new List<FilterUnit>() { assigneeFilter, creatorFilter, mentionFilter };
        }

        private async Task<FilterUnit> GetLabels()
        {
            var labels = await GitHubService.GetLabelsFromRepository(Repo.Id);
            var selections = new List<Selection>();
            foreach (var label in labels)
            {
                var labelSelection = new Selection(label.Name, label.Name, label);
                selections.Add(labelSelection);
            }
            var res = new DropdownFilter("Labels");
            res.AddRange(selections);
            return res;
        }

        private async Task<FilterUnit> GetMilestones()
        {
            var milestones = await GitHubService.GetMilestonesFromRepository(Repo.Id);
            var selections = new List<Selection>();
            foreach (var milestone in milestones)
            {
                var milestoneSelection = new Selection(milestone.Title, milestone.Number.ToString(), milestone);
                selections.Add(milestoneSelection);
            }
            var noneMilestone = new Selection("none", "none", "none");
            var anyMilestone = new Selection("any", "*", "*");
            selections.Add(noneMilestone);
            selections.Add(anyMilestone);
            var res = new DropdownFilter("Milestone");
            res.AddRange(selections);
            return res;
        }

        private void SetFilterParam(Dictionary<string, FilterUnit> filters)
        {
            RepoIssueRequest = new RepositoryIssueRequest();

            if (filters.TryGetValue("Labels", out FilterUnit? labelsUnit) &&
                labelsUnit is DropdownFilter labelsFilter &&
                !labelsFilter.DefaultSelected &&
                labelsFilter.Selected.SelectedValue is { Length: > 0 } label)
            {
                RepoIssueRequest.Labels = [label];
            }
            if (filters.TryGetValue("Milestone", out FilterUnit? milestoneUnit) &&
                milestoneUnit is DropdownFilter milestoneFilter &&
                !milestoneFilter.DefaultSelected &&
                milestoneFilter.Selected.SelectedValue is { Length: > 0 } milestone)
            {
                RepoIssueRequest.Milestone = milestone;
            }
            if (_isCollabotor)
            {
                if (filters.TryGetValue("Assignee", out FilterUnit? assigneeUnit) &&
                    assigneeUnit is DropdownFilter assigneeFilter &&
                    !assigneeFilter.DefaultSelected)
                {
                    RepoIssueRequest.Assignee = assigneeFilter.Selected.SelectedValue;
                }
                if (filters.TryGetValue("Creator", out FilterUnit? creatorUnit) &&
                    creatorUnit is DropdownFilter creatorFilter &&
                    !creatorFilter.DefaultSelected)
                {
                    RepoIssueRequest.Creator = creatorFilter.Selected.SelectedValue;
                }
                if (filters.TryGetValue("Mentioned", out FilterUnit? mentionedUnit) &&
                    mentionedUnit is DropdownFilter mentionedFilter &&
                    !mentionedFilter.DefaultSelected)
                {
                    RepoIssueRequest.Mentioned = mentionedFilter.Selected.SelectedValue;
                }
            }
            if (filters.TryGetValue("Issue Filter", out FilterUnit? issueFilterUnit) &&
                issueFilterUnit is DropdownFilter issueFilter &&
                !issueFilter.DefaultSelected &&
                issueFilter.Selected.Value is IssueFilter selectedIssueFilter)
            {
                RepoIssueRequest.Filter = selectedIssueFilter;
            }
            if (filters.TryGetValue("State", out FilterUnit? stateUnit) &&
                stateUnit is DropdownFilter stateFilter &&
                !stateFilter.DefaultSelected &&
                stateFilter.Selected.Value is ItemStateFilter selectedState)
            {
                RepoIssueRequest.State = selectedState;
            }
            if (filters.TryGetValue("Sort", out FilterUnit? sortUnit) &&
                sortUnit is DropdownFilter sortFilter &&
                !sortFilter.DefaultSelected &&
                sortFilter.Selected.Value is IssueSort selectedSort)
            {
                RepoIssueRequest.SortProperty = selectedSort;
            }
            if (filters.TryGetValue("Direction", out FilterUnit? directionUnit) &&
                directionUnit is DropdownFilter directionFilter &&
                !directionFilter.DefaultSelected &&
                directionFilter.Selected.Value is SortDirection selectedDirection)
            {
                RepoIssueRequest.SortDirection = selectedDirection;
            }
            if (filters.TryGetValue("Date", out FilterUnit? dateUnit) &&
                dateUnit is DateFilter dateFilter)
            {
                RepoIssueRequest.Since = dateFilter.StartDate;
            }
        }

        private void ApplyFilter(Dictionary<string, FilterUnit>? filters)
        {
            if (filters != null)
            {
                SetFilterParam(filters);
            }
            var issueSource = new IssueSource(Repo, RepoIssueRequest);
            SetIncrementalCollection(issueSource, null);
        }
    }
}




