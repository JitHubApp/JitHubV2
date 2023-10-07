using JitHub.ViewModels.Base;
using Octokit;

namespace JitHub.ViewModels
{
    public class FileDiffViewModel : RepoViewModel
    {
        private string _sha;
        private GitHubCommitFile _file;

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
            Repo = repo;
            Sha = sha;
            File = file;
        }
    }
}
 