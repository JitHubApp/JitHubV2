using JitHub.Models;
using JitHub.Models.Filter;
using CommunityToolkit.Mvvm.Input;
using Octokit;
using ItemStateFilter = Octokit.ItemStateFilter;
using PullRequestRequest = Octokit.PullRequestRequest;
using PullRequestSort = Octokit.PullRequestSort;
using SortDirection = Octokit.SortDirection;
using System.Collections.Generic;
using System.Windows.Input;

namespace JitHub.WinUI.ViewModels.PullRequestViewModels
{
    public partial class RepoPullRequestViewModel
    {
        private EventfulCollection<FilterUnit> _filters = new EventfulCollection<FilterUnit>();
        private PullRequestRequest _pullRequestRequest = new();
        private const int _perPage = 50;
        private bool _filterLoading;

        public bool FilterLoading
        {
            get => _filterLoading;
            set => SetProperty(ref _filterLoading, value);
        }
        public ICommand LoadCommand { get; private set; } = null!;
        public ICommand FilterCommand { get; private set; } = null!;
        public ICommand ClearCommand { get; private set; } = null!;
        public EventfulCollection<FilterUnit> Filters
        {
            get => _filters;
            set => SetProperty(ref _filters, value);
        }
        public PullRequestRequest PullRequestRequest
        {
            get => _pullRequestRequest;
            set => SetProperty(ref _pullRequestRequest, value);
        }

        private void InitializeFilters()
        {
            Filters = new EventfulCollection<FilterUnit>();
            PullRequestRequest = new PullRequestRequest();
            LoadCommand = new RelayCommand(GetFilterParams);
            FilterCommand = new RelayCommand<Dictionary<string, FilterUnit>>(ApplyFilter);
            ClearCommand = new RelayCommand<Dictionary<string, FilterUnit>>(ApplyFilter);
        }
        private void GetFilterParams()
        {
            FilterLoading = true;
            Filters.Add(GetStates());
            Filters.Add(GetDirections());
            Filters.Add(GetSorts());
            Filters.Add(new TextFilter("Head"));
            Filters.Add(new TextFilter("Base"));
            FilterLoading = false;
        }

        private FilterUnit GetStates()
        {
            var res = new DropdownFilter("State");
            res.AddRange(new List<Selection>
            {
                new Selection("open", "open", ItemStateFilter.Open),
                new Selection("closed", "closed", ItemStateFilter.Closed),
                new Selection("all", "all", ItemStateFilter.All)
            });
            return res;
        }

        private FilterUnit GetDirections()
        {
            var res = new DropdownFilter("Direction");
            res.AddRange(new List<Selection>
            {
                new Selection("asc", "asc", SortDirection.Ascending),
                new Selection("desc", "desc", SortDirection.Descending),

            });
            return res;
        }

        private FilterUnit GetSorts()
        {
            var res = new DropdownFilter("Sort Property");
            res.AddRange(new List<Selection>
            {
                new Selection("created", "created", PullRequestSort.Created),
                new Selection("updated", "updated", PullRequestSort.Updated),
                new Selection("popularity", "popularity", PullRequestSort.Popularity),
                new Selection("long-running", "long-running", PullRequestSort.LongRunning)
            });
            return res;
        }

        private void SetFilterParam(Dictionary<string, FilterUnit> filters)
        {
            if (filters["State"] is DropdownFilter stateFilter &&
                !stateFilter.DefaultSelected &&
                stateFilter.Selected.Value is ItemStateFilter selectedState)
            {
                PullRequestRequest.State = selectedState;
            }
            if (filters["Head"] is TextFilter headFilter && !headFilter.DefaultSelected)
            {
                PullRequestRequest.Head = headFilter.Text;
            }
            if (filters["Base"] is TextFilter baseFilter && !baseFilter.DefaultSelected)
            {
                PullRequestRequest.Base = baseFilter.Text;
            }
            if (filters["Sort Property"] is DropdownFilter sortPropertyFilter &&
                !sortPropertyFilter.DefaultSelected &&
                sortPropertyFilter.Selected.Value is PullRequestSort sortProperty)
            {
                PullRequestRequest.SortProperty = sortProperty;
            }
            if (filters["Direction"] is DropdownFilter directionFilter &&
                !directionFilter.DefaultSelected &&
                directionFilter.Selected.Value is SortDirection direction)
            {
                PullRequestRequest.SortDirection = direction;
            }
        }

        private void ApplyFilter(Dictionary<string, FilterUnit>? filters)
        {
            if (filters != null)
            {
                SetFilterParam(filters);
            }
            var pullRequestSource = new PullRequestSource(Repo, PullRequestRequest);
            SetIncrementalCollection(pullRequestSource, null);
        }
    }
}




