using JitHub.Models.Base;
using JitHub.Services;
using Microsoft.Extensions.DependencyInjection;
using CommunityToolkit.Common.Collections;
using CommunityToolkit.WinUI;
using CommunityToolkit.Mvvm.DependencyInjection;
using JitHub.Models.LegacyGitHub;
using ApiOptions = JitHub.Models.LegacyGitHub.ApiOptions;
using RepositoryIssueRequest = JitHub.Models.LegacyGitHub.RepositoryIssueRequest;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace JitHub.Models
{
    public class IssueSource : IIncrementalSource<RepoSelectableItemModel<Issue>>, IIncrementalLoadingSourceState
    {
        private readonly RepositoryIssueRequest _repoIssueRequest;
        private readonly IGitHubService _gitHubService;
        private readonly Repository _repository;

        public IssueSource(Repository repository, RepositoryIssueRequest repoIssueRequest)
        {
            _repository = repository ?? throw new ArgumentNullException(nameof(repository));
            _repoIssueRequest = repoIssueRequest ?? throw new ArgumentNullException(nameof(repoIssueRequest));
            _gitHubService = Ioc.Default.GetService<IGitHubService>()
                ?? throw new InvalidOperationException("IGitHubService is not registered.");
        }

        public bool HasMoreItems { get; private set; } = true;

        public async Task<IEnumerable<RepoSelectableItemModel<Issue>>> GetPagedItemsAsync(int pageIndex, int pageSize, CancellationToken cancellationToken = default)
        {
            if (pageIndex == 0)
            {
                HasMoreItems = true;
            }

            var apiOptions = new ApiOptions
            {
                StartPage = pageIndex + 1,
                PageCount = 1,
                PageSize = pageSize
            };
            var page = await _gitHubService.GetFilteredIssuesPage(_repository.Owner.Login, _repository.Name, _repoIssueRequest, apiOptions);
            HasMoreItems = page.HasMoreItems;
            return page.Items.Select(issue => new RepoSelectableItemModel<Issue> { Model = issue, Repository = _repository }).ToList();
        }
    }
}



