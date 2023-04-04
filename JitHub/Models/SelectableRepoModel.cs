using JitHub.Models.Base;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Input;

namespace JitHub.Models
{
    public class SelectableRepoModel : SelectableItem
    {
        private RepoModel _repo;
        public RepoModel Repo
        {
            get => _repo;
            set => SetProperty(ref _repo, value);
        }

        public SelectableRepoModel(RepoModel repo)
        {
            Repo = repo;
            Type = "Repo";
        }
    }
}
