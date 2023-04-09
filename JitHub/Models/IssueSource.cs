using JitHub.Models.Base;
using JitHub.Services;
using CommunityToolkit.Mvvm.DependencyInjection;
using Octokit;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Common.Collections;

namespace JitHub.Models;

public class IssueSource : IIncrementalSource<RepoSelectableItemModel<Issue>>
{
    private RepositoryIssueRequest _repoIssueRequest;
    private IGitHubService _gitHubService;
    private Repository _repository;

    public IssueSource(Repository repository, RepositoryIssueRequest repoIssueRequest)
    {
        _repository = repository;
        _repoIssueRequest = repoIssueRequest;
        _gitHubService = Ioc.Default.GetService<IGitHubService>();
    }

    public async Task<IEnumerable<RepoSelectableItemModel<Issue>>> GetPagedItemsAsync(int pageIndex, int pageSize, CancellationToken cancellationToken = default)
    {
        var apiOptions = new ApiOptions
        {
            StartPage = pageIndex + 1,
            PageCount = 1,
            PageSize = pageSize
        };
        var issues = await _gitHubService.GetFilteredIssues(_repository.Owner.Login, _repository.Name, _repoIssueRequest, apiOptions);
        return issues.Select(issue => new RepoSelectableItemModel<Issue> { Model = issue, Repository = _repository }).ToList();
    }
}
