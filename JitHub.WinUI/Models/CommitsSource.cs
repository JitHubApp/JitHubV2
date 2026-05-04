using JitHub.Services;
using Microsoft.Extensions.DependencyInjection;
using CommunityToolkit.Common.Collections;
using CommunityToolkit.Mvvm.DependencyInjection;
using JitHub.Models.LegacyGitHub;
using ApiOptions = JitHub.Models.LegacyGitHub.ApiOptions;
using CommitRequest = JitHub.Models.LegacyGitHub.CommitRequest;
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
        private readonly Repository _repo;
        private readonly ICommand _copy;
        private readonly ICommand _viewCode;
        private readonly IGitHubService _gitHubService;
        private readonly CommitRequest _commitRequest;

        public CommitsSource(Repository repo, CommitRequest request, ICommand copy, ICommand viewCode)
        {
            _repo = repo ?? throw new ArgumentNullException(nameof(repo));
            _copy = copy ?? throw new ArgumentNullException(nameof(copy));
            _viewCode = viewCode ?? throw new ArgumentNullException(nameof(viewCode));
            _commitRequest = request ?? throw new ArgumentNullException(nameof(request));
            _gitHubService = Ioc.Default.GetService<IGitHubService>()
                ?? throw new InvalidOperationException("IGitHubService is not registered.");
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



