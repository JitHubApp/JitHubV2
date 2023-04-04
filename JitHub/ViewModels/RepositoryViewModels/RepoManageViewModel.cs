using JitHub.Models;
using JitHub.Services;
using JitHub.ViewModels.Base;
using JitHub.Views.Controls.Repo;
using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace JitHub.ViewModels.RepositoryViewModels
{
    public class RepoManageViewModel : RepoListViewModel<SelectableRepoModel>
    {
        private ModalService _modalService;
        private ISettingService _settings;
        private int _totalRepoToDelete;
        private int _progress;
        private bool _deleting;

        public int TotalRepoToDelete
        {
            get => _totalRepoToDelete;
            set => SetProperty(ref _totalRepoToDelete, value);
        }
        public int Progress
        {
            get => _progress;
            set => SetProperty(ref _progress, value);
        }
        public bool Deleting
        {
            get => _deleting;
            set => SetProperty(ref _deleting, value);
        }

        public RepoManageViewModel()
        {
            _modalService = Ioc.Default.GetService<ModalService>();
            _settings = Ioc.Default.GetService<ISettingService>();
            Progress = 0;
        }

        public override async Task<ICollection<SelectableRepoModel>> GetRepos()
        {
            var repos = await GitHubService.GetAllRepos();
            var repoModels = repos
                .Select(repo => new RepoModel(repo))
                .Select(repoModel => new SelectableRepoModel(repoModel))
                .ToList();
            return repoModels;
        }

        private async Task DeleteRepos()
        {
            var selectedRepos = Repos
                .Where(repo => repo.Selected)
                .Select(repo => repo.Repo)
                .ToList();
            TotalRepoToDelete = selectedRepos.Count;
            Deleting = true;
            var failedList = new List<FailedRepo>();
            foreach (var repo in selectedRepos)
            {
                try
                {
                    await GitHubService.DeleteRepo(repo.Repository.Id);
                    Progress += 1;
                }
                catch (Exception e)
                {
                    failedList.Add(new FailedRepo(repo, e.Message));
                }
            }
            if (failedList.Count > 0)
            {
                // retry
                var cancel = new AsyncRelayCommand(CancelAndRefresh);
                _modalService.Open("Failed to delete", new RepoDeletionFailDialog(failedList, cancel), cancel);
            }
            else
            {
                // oh god this is so hacky
                Thread.Sleep(1000);
                await LoadRepos();
            }
            Deleting = false;
        }

        private async Task CancelAndRefresh()
        {
            Cancel();
            await LoadRepos();
        }

        private void Cancel()
        {
            _modalService.Close();
        }

        public void DeselectAll()
        {
            foreach (var repo in Repos)
            {
                if (repo.Selected)
                {
                    repo.Selected = false;
                }
            }
        }

        private async Task ConfirmDelete()
        {
            _modalService.Close();
            await DeleteRepos();
        }

        public async Task OnDelete()
        {
            var selectedRepos = Repos
                .Where(repo => repo.Selected)
                .Select(repo => repo.Repo)
                .ToList();
            if (selectedRepos.Count == 0) return;
            if (!_settings.Get<bool>(AccountService.doNotWarnDeleteRepoKey))
            {
                // confirm
                var confirm = new AsyncRelayCommand(ConfirmDelete);
                var cancel = new RelayCommand(Cancel);
                var dialog = new RepoDeleteConfirmationDialog(selectedRepos.Count, confirm, cancel);
                _modalService.Open("Confirm", dialog);
            }
            else
            {
                await DeleteRepos();
            }
        }
    }
}
