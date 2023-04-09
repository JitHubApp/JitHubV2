using Octokit;
using System;
using System.Threading.Tasks;

namespace JitHub.ViewModels.Base
{
    public class RepoViewModel : LoadableViewModel<Repository>
    {
        public Repository Repo
        {
            get => Model;
            set => Model = value;
        }

        public bool IsAuthor => User?.Login == Repo?.Owner?.Login;

        virtual public async Task<bool> IsCollaborator()
        {
            try
            {
                var res = await GitHubService.GitHubClient.Repository.Collaborator.IsCollaborator(Repo.Id, User?.Login);
                return res;
            }
            catch (Exception e)
            {
                return false;
            }
        }
    }
}
