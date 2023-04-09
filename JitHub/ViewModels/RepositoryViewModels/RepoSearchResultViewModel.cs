using JitHub.Models;
using JitHub.ViewModels.Base;
using CommunityToolkit.Mvvm.Input;
using Octokit;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Input;

namespace JitHub.ViewModels.RepositoryViewModels
{
    public class RepoSearchResultViewModel : LoadableViewModel<string>
    {
        private ICollection<RepoModel> _items;
        public string Term
        {
            get => Model;
            set => Model = value;
        }
        public ICollection<RepoModel> Items
        {
            get => _items;
            set => SetProperty(ref _items, value);
        }

        public ICommand LoadCommand;

        public RepoSearchResultViewModel()
        {
            LoadCommand = new AsyncRelayCommand(Load);
        }

        private async Task Load()
        {
            if (!string.IsNullOrWhiteSpace(Term))
            {
                Loading = true;
                var repos = await GitHubService.SearchForRepos(Term);
                Items = repos
                    .Select(repo => new RepoModel(repo))
                    .ToList();
                Loading = false;
            }
        }
    }
}
