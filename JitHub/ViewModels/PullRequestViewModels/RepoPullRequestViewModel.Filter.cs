using JitHub.Models;
using JitHub.Models.Filter;
using CommunityToolkit.Mvvm.Input;
using Octokit;
using System.Collections.Generic;
using System.Windows.Input;

namespace JitHub.ViewModels.PullRequestViewModels
{
    public partial class RepoPullRequestViewModel
    {
        private EventfulCollection<FilterUnit> _filters;
        private PullRequestRequest _pullRequestRequest;
        private const int _perPage = 50;
        private bool _filterLoading;

        public bool FilterLoading
        {
            get => _filterLoading;
            set => SetProperty(ref _filterLoading, value);
        }
        public ICommand LoadCommand { get; set; }
        public ICommand FilterCommand { get; set; }
        public ICommand ClearCommand { get; set; }
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
            if (!filters["State"].DefaultSelected)
            {
                PullRequestRequest.State = (ItemStateFilter)((DropdownFilter)filters["State"]).Selected.Value;
            }
            if (!filters["Head"].DefaultSelected)
            {
                PullRequestRequest.Head = ((TextFilter)filters["Head"]).Text;
            }
            if (!filters["Base"].DefaultSelected)
            {
                PullRequestRequest.Base = ((TextFilter)filters["Base"]).Text;
            }
            if (!filters["Sort Property"].DefaultSelected)
            {
                PullRequestRequest.SortProperty = (PullRequestSort)((DropdownFilter)filters["Sort Property"]).Selected.Value;
            }
            if (!filters["Direction"].DefaultSelected)
            {
                PullRequestRequest.SortDirection = (SortDirection)((DropdownFilter)filters["Direction"]).Selected.Value;
            }
        }

        private void ApplyFilter(Dictionary<string, FilterUnit> filters)
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
