using JitHub.Models.Base;
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

namespace JitHub.Models
{
    public class PullRequestSource : IIncrementalSource<RepoSelectableItemModel<PullRequest>>
    {
        private IGitHubService _gitHubService;
        private Repository _repository;
        private PullRequestRequest _requestParameters;

        public PullRequestSource(Repository repository, PullRequestRequest requestParams)
        {
            _repository = repository;
            _requestParameters = requestParams;
            _gitHubService = Ioc.Default.GetService<IGitHubService>();
        }

        public async Task<IEnumerable<RepoSelectableItemModel<PullRequest>>> GetPagedItemsAsync(int pageIndex, int pageSize, CancellationToken cancellationToken = default)
        {
            var apiOptions = new ApiOptions
            {
                StartPage = pageIndex + 1,
                PageCount = 1,
                PageSize = pageSize
            };
            var prs = await _gitHubService.GetPullRequests(_repository.Owner.Login, _repository.Name, _requestParameters, apiOptions);
            var prsModels = new List<RepoSelectableItemModel<PullRequest>>();
            foreach (var pr in prs)
            {
                prsModels.Add(new RepoSelectableItemModel<PullRequest>
                {
                    Model = pr,
                    Repository = _repository
                });
            }
            return prsModels;
        }
    }
}
