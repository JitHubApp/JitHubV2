using JitHub.Models;
using CommunityToolkit.Mvvm.Input;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;

namespace JitHub.ViewModels.Base
{
    public abstract class RepoListViewModel<T> : LoadableViewModel<string>
    {
        private ICollection<T> _repos;
        private bool _isEmpty;

        public ICollection<T> Repos
        {
            get => _repos;
            set
            {
                SetProperty(ref _repos, value);
                IsEmpty = !Loading && value.Count == 0;
            }
        }
        public bool IsEmpty
        {
            get => _isEmpty;
            set => SetProperty(ref _isEmpty, value);
        }

        public ICommand LoadCommand { get; }

        public RepoListViewModel()
        {
            LoadCommand = new AsyncRelayCommand(LoadRepos);
        }

        public void Load(bool value)
        {
            Loading = value;
            IsEmpty = !value && Repos.Count == 0;
        }

        abstract public Task<ICollection<T>> GetRepos();

        // Override this to get different list of repos
        virtual public async Task LoadRepos()
        {
            Load(true);
            Repos = await GetRepos();
            Load(false);
        }
    }
}
