using JitHub.Models;
using JitHub.WinUI.ViewModels.Base;
using CommunityToolkit.Mvvm.Input;
using Octokit;
using SearchRepositoriesRequest = Octokit.SearchRepositoriesRequest;
using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Windows.Input;

namespace JitHub.WinUI.ViewModels.RepositoryViewModels
{
    public class RepoListViewModel : LoadableViewModel<string>
    {
        private ObservableCollection<RepoModel> _repos = [];
        private string _searchTerm = string.Empty;
        private bool _isEmpty;

        public string SearchTerm { get => _searchTerm; set => SetProperty(ref _searchTerm, value); }
        public ObservableCollection<RepoModel> Repos
        {
            get => _repos;
            set
            {
                SetProperty(ref _repos, value);
                IsEmpty = !Loading && value.Count == 0;
            }
        }
        public bool IsEmpty { get => _isEmpty; set => SetProperty(ref _isEmpty, value); }
        public ICommand LoadCommand { get; }
        public ICommand SearchCommand { get; }
        public ICommand ClearCommand { get; }

        public RepoListViewModel()
        {
            SearchTerm = "";
            Loading = true;
            Repos = new ObservableCollection<RepoModel>();
            LoadCommand = new AsyncRelayCommand(LoadRepos);
            SearchCommand = new AsyncRelayCommand(SearchRepo);
            ClearCommand = new AsyncRelayCommand(async () => { SearchTerm = ""; await LoadRepos(); });
        }

        public async Task LoadRepos()
        {
            Load(true);
            var repos = await GitHubService.GetAllRepos();
            Repos.Clear();
            foreach (var repo in repos)
            {
                var repoModel = new RepoModel(repo);
                Repos.Add(repoModel);
            }
            Load(false);
        }

        public async Task SearchRepo()
        {
            if (String.IsNullOrWhiteSpace(SearchTerm)) return;
            Load(true);
            SearchRepositoriesRequest request = new SearchRepositoriesRequest(SearchTerm);
            var user = await GitHubService.GetCurrentUser();
            request.User = user.Login;
            var result = await GitHubService.SearchForRepos(request);
            Repos.Clear();
            foreach (var repo in result)
            {
                var repoModel = new RepoModel(repo);
                Repos.Add(repoModel);
            }
            Load(false);
        }

        private void Load(bool value)
        {
            Loading = value;
            IsEmpty = !value && Repos.Count == 0;
        }
    }
}




