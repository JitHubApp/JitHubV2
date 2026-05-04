using JitHub.WinUI.ViewModels.Base;
using JitHub.Models.LegacyGitHub;
using System;

namespace JitHub.WinUI.ViewModels
{
    public class FileDiffViewModel : RepoViewModel
    {
        private string _sha = string.Empty;
        private GitHubCommitFile _file = null!;

        public string Sha
        {
            get => _sha;
            set => SetProperty(ref _sha, value);
        }
        public GitHubCommitFile File
        {
            get => _file;
            set => SetProperty(ref _file, value);
        }
        public FileDiffViewModel(Repository repo, string sha, GitHubCommitFile file)
        {
            Repo = repo ?? throw new ArgumentNullException(nameof(repo));
            Sha = sha;
            File = file ?? throw new ArgumentNullException(nameof(file));
        }
    }
}
 
