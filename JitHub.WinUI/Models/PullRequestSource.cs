using JitHub.Models.Base;
using JitHub.Services;
using Microsoft.Extensions.DependencyInjection;
using CommunityToolkit.Common.Collections;
using CommunityToolkit.Mvvm.DependencyInjection;
using JitHub.Models.LegacyGitHub;
using ApiOptions = JitHub.Models.LegacyGitHub.ApiOptions;
using PullRequestRequest = JitHub.Models.LegacyGitHub.PullRequestRequest;
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
        private readonly IGitHubService _gitHubService;
        private readonly Repository _repository;
        private readonly PullRequestRequest _requestParameters;

        public PullRequestSource(Repository repository, PullRequestRequest requestParams)
        {
            _repository = repository ?? throw new ArgumentNullException(nameof(repository));
            _requestParameters = requestParams ?? throw new ArgumentNullException(nameof(requestParams));
            _gitHubService = Ioc.Default.GetService<IGitHubService>()
                ?? throw new InvalidOperationException("IGitHubService is not registered.");
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



