using JitHub.Models;
using JitHub.Services;
using JitHub.ViewModels.Base;
using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;

namespace JitHub.ViewModels.CommitViewModels
{
    public class CommitDetailViewModel : RepoViewModel
    {
        private ICollection<FileDiffViewModel> _files;
        private CommandableCommit _commandableCommit;

        public ICommand LoadCommand { get; private set; }
        public ICollection<FileDiffViewModel> Files
        {
            get => _files;
            set => SetProperty(ref _files, value);
        }
        public CommandableCommit CommandableCommit
        {
            get => _commandableCommit;
            set => SetProperty(ref _commandableCommit, value);
        }

        public void Init(CommandableCommit commit)
        {
            Repo = commit.Repository;
            CommandableCommit = commit;
            LoadCommand = new AsyncRelayCommand(Load);
        }

        private async Task Load()
        {
            Loading = true;
            var commit = await GitHubService.GetGitHubCommit(Repo.Owner.Login, Repo.Name, CommandableCommit.Sha);
            Files = commit.Files.Select(file => new FileDiffViewModel(Repo, CommandableCommit.Sha, file)).ToList();
            Loading = false;
        }
    }
}
