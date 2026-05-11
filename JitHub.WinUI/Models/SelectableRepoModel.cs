using JitHub.Models.Base;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Input;

namespace JitHub.Models
{
    [WinRT.GeneratedBindableCustomProperty]
    public partial class SelectableRepoModel : SelectableItem
    {
        private RepoModel _repo = null!;
        public RepoModel Repo
        {
            get => _repo;
            set => SetProperty(ref _repo, value);
        }
        public string FullName => Repo.FullName;
        public string Description => Repo.Description;
        public int StargazersCount => Repo.StargazersCount;

        public SelectableRepoModel(RepoModel repo)
        {
            Repo = repo ?? throw new ArgumentNullException(nameof(repo));
            Type = "Repo";
        }
    }
}
