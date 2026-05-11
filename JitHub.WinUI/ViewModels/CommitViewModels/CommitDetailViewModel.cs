using JitHub.Models;
using JitHub.Services;
using JitHub.WinUI.ViewModels.Base;
using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;

namespace JitHub.WinUI.ViewModels.CommitViewModels
{
    public class CommitDetailViewModel : RepoViewModel
    {
        private List<FileDiffViewModel> _files = [];
        private CommandableCommit _commandableCommit = null!;

        public ICommand LoadCommand { get; }
        public List<FileDiffViewModel> Files
        {
            get => _files;
            set => SetProperty(ref _files, value);
        }
        public CommandableCommit CommandableCommit
        {
            get => _commandableCommit;
            set => SetProperty(ref _commandableCommit, value);
        }

        public CommitDetailViewModel()
        {
            LoadCommand = new AsyncRelayCommand(Load);
        }

        public void Init(CommandableCommit commit)
        {
            ArgumentNullException.ThrowIfNull(commit);
            Repo = commit.Repository;
            CommandableCommit = commit;
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



