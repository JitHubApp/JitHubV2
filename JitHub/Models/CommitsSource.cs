using JitHub.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Toolkit.Collections;
using CommunityToolkit.Mvvm.DependencyInjection;
using Octokit;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;

namespace JitHub.Models
{
    public class CommitsSource : IIncrementalSource<CommandableCommit>
    {
        private Repository _repo;
        private ICommand _copy;
        private ICommand _viewCode;
        private IGitHubService _gitHubService;
        private CommitRequest _commitRequest;

        public CommitsSource(Repository repo, CommitRequest request, ICommand copy, ICommand viewCode)
        {
            _repo = repo;
            _copy = copy;
            _viewCode = viewCode;
            _commitRequest = request;
            _gitHubService = Ioc.Default.GetService<IGitHubService>();
        }

        public async Task<IEnumerable<CommandableCommit>> GetPagedItemsAsync(int pageIndex, int pageSize, CancellationToken cancellationToken = default)
        {
            
            
            var apiOptions = new ApiOptions
            {
                StartPage = pageIndex + 1,
                PageCount = 1,
                PageSize = pageSize
            };
            var commits = await _gitHubService.GetCommits(_repo.Owner.Login, _repo.Name, _commitRequest, apiOptions);
            return commits.Select(commit => new CommandableCommit(_repo, _copy, _viewCode, commit));
        }
    }
}
