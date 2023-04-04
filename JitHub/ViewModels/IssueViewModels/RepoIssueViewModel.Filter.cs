using JitHub.Models;
using JitHub.Models.Filter;
using CommunityToolkit.Mvvm.Input;
using Octokit;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows.Input;

namespace JitHub.ViewModels.IssueViewModels
{
    public partial class RepoIssueViewModel
    {
        private EventfulCollection<FilterUnit> _filters;
        private bool _isCollabotor;
        private RepositoryIssueRequest _repoIssueRequest;
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
        public ICommand LoadCommand { get; set; }
        public ICommand FilterCommand { get; set; }
        public ICommand ClearCommand { get; set; }

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
            var users = await GitHubService.GitHubClient.Repository.Collaborator.GetAll(Repo.Id);
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
            var labels = await GitHubService.GitHubClient.Issue.Labels.GetAllForRepository(Repo.Id);
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
            var milestones = await GitHubService.GitHubClient.Issue.Milestone.GetAllForRepository(Repo.Id);
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
            if (!filters["Milestone"].DefaultSelected)
            {
                RepoIssueRequest.Milestone = (string)((DropdownFilter)filters["Milestone"]).Selected.Value;
            }
            if (_isCollabotor)
            {
                if (!filters["Assignee"].DefaultSelected)
                {
                    RepoIssueRequest.Assignee = ((DropdownFilter)filters["Assignee"]).Selected.SelectedValue;
                }
                if (!filters["Creator"].DefaultSelected)
                {
                    RepoIssueRequest.Creator = ((DropdownFilter)filters["Creator"]).Selected.SelectedValue;
                }
                if (!filters["Mentioned"].DefaultSelected)
                {
                    RepoIssueRequest.Mentioned = ((DropdownFilter)filters["Mentioned"]).Selected.SelectedValue;
                }
            }
            if (!filters["Issue Filter"].DefaultSelected)
            {
                RepoIssueRequest.Filter = (IssueFilter)((DropdownFilter)filters["Issue Filter"]).Selected.Value;
            }
            if (!filters["State"].DefaultSelected)
            {
                RepoIssueRequest.State = (ItemStateFilter)((DropdownFilter)filters["State"]).Selected.Value;
            }
            if (!filters["Sort"].DefaultSelected)
            {
                RepoIssueRequest.SortProperty = (IssueSort)((DropdownFilter)filters["Sort"]).Selected.Value;
            }
            if (!filters["Direction"].DefaultSelected)
            {
                RepoIssueRequest.SortDirection = (SortDirection)((DropdownFilter)filters["Direction"]).Selected.Value;
            }
            RepoIssueRequest.Since = ((DateFilter)filters["Date"]).StartDate;
        }

        private void ApplyFilter(Dictionary<string, FilterUnit> filters)
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
