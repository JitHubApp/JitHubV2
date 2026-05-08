using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;
using JitHub.Models.GitHub;
using System.Linq;

namespace JitHub.Services;

public sealed class GitHubClientService : IGitHubClientService
{
    public const string PublicAccessToken = "__JITHUB_PUBLIC__";

    private readonly HttpClient _httpClient;

    public GitHubClientService()
    {
        _httpClient = new HttpClient
        {
            BaseAddress = new Uri("https://api.github.com/")
        };
        _httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("JitHub", "1.0"));
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        _httpClient.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
    }

    public Uri CreateLoginUri(string clientId, string? state = null, string? redirectUri = null)
    {
        List<string> queryParts =
        [
            $"client_id={Uri.EscapeDataString(clientId)}",
            $"scope={Uri.EscapeDataString("user repo delete_repo")}"
        ];

        if (!string.IsNullOrWhiteSpace(redirectUri))
        {
            queryParts.Add($"redirect_uri={Uri.EscapeDataString(redirectUri)}");
        }

        if (!string.IsNullOrWhiteSpace(state))
        {
            queryParts.Add($"state={Uri.EscapeDataString(state)}");
        }

        string query = string.Join("&", queryParts);
        return new Uri($"https://github.com/login/oauth/authorize?{query}", UriKind.Absolute);
    }

    public async Task<GitHubUser> GetCurrentUserAsync(string token, CancellationToken cancellationToken = default)
    {
        using HttpRequestMessage request = CreateAuthenticatedRequest(HttpMethod.Get, "user", token);
        using HttpResponseMessage response =
            await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await ReadResponseAsync(
            response,
            GitHubJsonSerializerContext.Default.GitHubUser,
            "user",
            cancellationToken);
    }

    public async Task<GitHubRepository> GetRepositoryAsync(
        string token,
        string owner,
        string name,
        CancellationToken cancellationToken = default)
    {
        string path = $"repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(name)}";
        using HttpRequestMessage request = CreateAuthenticatedRequest(HttpMethod.Get, path, token);
        using HttpResponseMessage response =
            await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        return await ReadResponseAsync(
            response,
            GitHubJsonSerializerContext.Default.GitHubRepository,
            "repository",
            cancellationToken);
    }

    public async Task<GitHubRepository> GetRepositoryAsync(
        string token,
        long repositoryId,
        CancellationToken cancellationToken = default)
    {
        string path = $"repositories/{repositoryId}";
        using HttpRequestMessage request = CreateAuthenticatedRequest(HttpMethod.Get, path, token);
        using HttpResponseMessage response =
            await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        return await ReadResponseAsync(
            response,
            GitHubJsonSerializerContext.Default.GitHubRepository,
            "repository",
            cancellationToken);
    }

    public async Task<GitHubRepository> CreateRepositoryAsync(
        string token,
        GitHubRepositoryCreateOptions options,
        CancellationToken cancellationToken = default)
    {
        GitHubRepositoryCreateRequest payload = new()
        {
            Name = options.Name,
            Description = string.IsNullOrWhiteSpace(options.Description) ? null : options.Description,
            Homepage = string.IsNullOrWhiteSpace(options.Homepage) ? null : options.Homepage,
            Private = options.Private,
            Visibility = string.IsNullOrWhiteSpace(options.Visibility) ? null : options.Visibility,
            AutoInit = options.AutoInit,
            LicenseTemplate = string.IsNullOrWhiteSpace(options.LicenseTemplate) ? null : options.LicenseTemplate,
            GitignoreTemplate = string.IsNullOrWhiteSpace(options.GitignoreTemplate) ? null : options.GitignoreTemplate,
            HasIssues = options.HasIssues,
            HasProjects = options.HasProjects,
            HasWiki = options.HasWiki
        };
        using HttpRequestMessage request = CreateJsonRequest(
            HttpMethod.Post,
            "user/repos",
            token,
            payload,
            GitHubJsonSerializerContext.Default.GitHubRepositoryCreateRequest);
        using HttpResponseMessage response =
            await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        return await ReadResponseAsync(
            response,
            GitHubJsonSerializerContext.Default.GitHubRepository,
            "repository",
            cancellationToken);
    }

    public async Task<bool> IsRepositoryStarredAsync(
        string token,
        string owner,
        string name,
        CancellationToken cancellationToken = default)
    {
        string path = $"user/starred/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(name)}";
        using HttpRequestMessage request = CreateAuthenticatedRequest(HttpMethod.Get, path, token);
        using HttpResponseMessage response =
            await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NoContent)
        {
            return true;
        }

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }

        await EnsureSuccessAsync(response, cancellationToken);
        return true;
    }

    public async Task StarRepositoryAsync(
        string token,
        string owner,
        string name,
        CancellationToken cancellationToken = default)
    {
        string path = $"user/starred/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(name)}";
        using HttpRequestMessage request = CreateAuthenticatedRequest(HttpMethod.Put, path, token);
        using HttpResponseMessage response =
            await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    public async Task UnstarRepositoryAsync(
        string token,
        string owner,
        string name,
        CancellationToken cancellationToken = default)
    {
        string path = $"user/starred/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(name)}";
        using HttpRequestMessage request = CreateAuthenticatedRequest(HttpMethod.Delete, path, token);
        using HttpResponseMessage response =
            await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return;
        }

        await EnsureSuccessAsync(response, cancellationToken);
    }

    public async Task<bool> IsRepositoryWatchedAsync(
        string token,
        string owner,
        string name,
        CancellationToken cancellationToken = default)
    {
        string path = $"repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(name)}/subscription";
        using HttpRequestMessage request = CreateAuthenticatedRequest(HttpMethod.Get, path, token);
        using HttpResponseMessage response =
            await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }

        await EnsureSuccessAsync(response, cancellationToken);
        GitHubRepositorySubscription subscription = await ReadResponseAsync(
            response,
            GitHubJsonSerializerContext.Default.GitHubRepositorySubscription,
            "repository subscription",
            cancellationToken);
        return subscription.Subscribed && !subscription.Ignored;
    }

    public async Task WatchRepositoryAsync(
        string token,
        string owner,
        string name,
        CancellationToken cancellationToken = default)
    {
        string path = $"repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(name)}/subscription";
        GitHubRepositorySubscriptionRequest payload = new()
        {
            Subscribed = true,
            Ignored = false
        };
        using HttpRequestMessage request = CreateJsonRequest(
            HttpMethod.Put,
            path,
            token,
            payload,
            GitHubJsonSerializerContext.Default.GitHubRepositorySubscriptionRequest);
        using HttpResponseMessage response =
            await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    public async Task UnwatchRepositoryAsync(
        string token,
        string owner,
        string name,
        CancellationToken cancellationToken = default)
    {
        string path = $"repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(name)}/subscription";
        using HttpRequestMessage request = CreateAuthenticatedRequest(HttpMethod.Delete, path, token);
        using HttpResponseMessage response =
            await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return;
        }

        await EnsureSuccessAsync(response, cancellationToken);
    }

    public async Task<GitHubRepository> ForkRepositoryAsync(
        string token,
        string owner,
        string name,
        CancellationToken cancellationToken = default)
    {
        string path = $"repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(name)}/forks";
        using HttpRequestMessage request = CreateAuthenticatedRequest(HttpMethod.Post, path, token);
        using HttpResponseMessage response =
            await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        return await ReadResponseAsync(
            response,
            GitHubJsonSerializerContext.Default.GitHubRepository,
            "repository",
            cancellationToken);
    }

    public async Task DeleteRepositoryAsync(
        string token,
        string owner,
        string name,
        CancellationToken cancellationToken = default)
    {
        string path = $"repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(name)}";
        using HttpRequestMessage request = CreateAuthenticatedRequest(HttpMethod.Delete, path, token);
        using HttpResponseMessage response =
            await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    public async Task<IReadOnlyList<GitHubRepository>> GetRepositoriesForCurrentUserAsync(
        string token,
        int pageSize,
        int pageNumber = 1,
        CancellationToken cancellationToken = default)
    {
        string path = IsPublicAccessToken(token)
            ? $"users/JitHubApp/repos?sort=updated&direction=desc&per_page={pageSize}&page={pageNumber}"
            : $"user/repos?sort=updated&direction=desc&per_page={pageSize}&page={pageNumber}";
        using HttpRequestMessage request = CreateAuthenticatedRequest(HttpMethod.Get, path, token);
        using HttpResponseMessage response =
            await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        GitHubRepository[] repositories = await ReadResponseAsync(
            response,
            GitHubJsonSerializerContext.Default.GitHubRepositoryArray,
            "repository list",
            cancellationToken);
        return repositories;
    }

    public async Task<IReadOnlyList<GitHubActor>> GetStargazersAsync(
        string token,
        string owner,
        string name,
        int pageSize = 100,
        int pageNumber = 1,
        CancellationToken cancellationToken = default)
    {
        string path =
            $"repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(name)}/stargazers?per_page={pageSize}&page={pageNumber}";
        using HttpRequestMessage request = CreateAuthenticatedRequest(HttpMethod.Get, path, token);
        using HttpResponseMessage response =
            await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        GitHubActor[] stargazers = await ReadResponseAsync(
            response,
            GitHubJsonSerializerContext.Default.GitHubActorArray,
            "stargazer list",
            cancellationToken);
        return stargazers;
    }

    public async Task<IReadOnlyList<GitHubRepository>> GetStarredRepositoriesForUserAsync(
        string token,
        string userName,
        int pageSize = 100,
        int pageNumber = 1,
        string? sort = null,
        string? direction = null,
        CancellationToken cancellationToken = default)
    {
        string path = BuildStarredRepositoriesPath(
            $"users/{Uri.EscapeDataString(userName)}/starred",
            pageSize,
            pageNumber,
            sort,
            direction);
        using HttpRequestMessage request = CreateAuthenticatedRequest(HttpMethod.Get, path, token);
        using HttpResponseMessage response =
            await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        GitHubRepository[] repositories = await ReadResponseAsync(
            response,
            GitHubJsonSerializerContext.Default.GitHubRepositoryArray,
            "starred repository list",
            cancellationToken);
        return repositories;
    }

    public async Task<IReadOnlyList<GitHubRepository>> GetStarredRepositoriesForCurrentUserAsync(
        string token,
        int pageSize = 100,
        int pageNumber = 1,
        string? sort = null,
        string? direction = null,
        CancellationToken cancellationToken = default)
    {
        string path = BuildStarredRepositoriesPath("user/starred", pageSize, pageNumber, sort, direction);
        using HttpRequestMessage request = CreateAuthenticatedRequest(HttpMethod.Get, path, token);
        using HttpResponseMessage response =
            await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        GitHubRepository[] repositories = await ReadResponseAsync(
            response,
            GitHubJsonSerializerContext.Default.GitHubRepositoryArray,
            "starred repository list",
            cancellationToken);
        return repositories;
    }

    public async Task<IReadOnlyList<GitHubActivityEvent>> GetUserEventsAsync(
        string token,
        string userName,
        int pageSize,
        int pageNumber = 1,
        CancellationToken cancellationToken = default)
    {
        string path =
            $"users/{Uri.EscapeDataString(userName)}/events?per_page={pageSize}&page={pageNumber}";
        using HttpRequestMessage request = CreateAuthenticatedRequest(HttpMethod.Get, path, token);
        using HttpResponseMessage response =
            await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        GitHubActivityEvent[] events = await ReadResponseAsync(
            response,
            GitHubJsonSerializerContext.Default.GitHubActivityEventArray,
            "user event list",
            cancellationToken);
        return events;
    }

    public async Task<IReadOnlyList<GitHubActivityEvent>> GetReceivedEventsAsync(
        string token,
        string userName,
        int pageSize,
        int pageNumber = 1,
        CancellationToken cancellationToken = default)
    {
        string path =
            $"users/{Uri.EscapeDataString(userName)}/received_events?per_page={pageSize}&page={pageNumber}";
        using HttpRequestMessage request = CreateAuthenticatedRequest(HttpMethod.Get, path, token);
        using HttpResponseMessage response =
            await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        GitHubActivityEvent[] events = await ReadResponseAsync(
            response,
            GitHubJsonSerializerContext.Default.GitHubActivityEventArray,
            "received event list",
            cancellationToken);
        return events;
    }

    public async Task<IReadOnlyList<GitHubBranch>> GetBranchesAsync(
        string token,
        string owner,
        string name,
        int pageSize = 100,
        int pageNumber = 1,
        CancellationToken cancellationToken = default)
    {
        string path =
            $"repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(name)}/branches?per_page={pageSize}&page={pageNumber}";
        using HttpRequestMessage request = CreateAuthenticatedRequest(HttpMethod.Get, path, token);
        using HttpResponseMessage response =
            await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        GitHubBranch[] branches = await ReadResponseAsync(
            response,
            GitHubJsonSerializerContext.Default.GitHubBranchArray,
            "branch list",
            cancellationToken);
        return branches;
    }

    public async Task<IReadOnlyList<GitHubActor>> GetAssigneesAsync(
        string token,
        string owner,
        string name,
        int pageSize = 100,
        int pageNumber = 1,
        CancellationToken cancellationToken = default)
    {
        string path =
            $"repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(name)}/assignees?per_page={pageSize}&page={pageNumber}";
        using HttpRequestMessage request = CreateAuthenticatedRequest(HttpMethod.Get, path, token);
        using HttpResponseMessage response =
            await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        GitHubActor[] assignees = await ReadResponseAsync(
            response,
            GitHubJsonSerializerContext.Default.GitHubActorArray,
            "assignee list",
            cancellationToken);
        return assignees;
    }

    public async Task<IReadOnlyList<GitHubActor>> GetCollaboratorsAsync(
        string token,
        string owner,
        string name,
        int pageSize = 100,
        int pageNumber = 1,
        CancellationToken cancellationToken = default)
    {
        string path =
            $"repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(name)}/collaborators?per_page={pageSize}&page={pageNumber}";
        using HttpRequestMessage request = CreateAuthenticatedRequest(HttpMethod.Get, path, token);
        using HttpResponseMessage response =
            await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        GitHubActor[] collaborators = await ReadResponseAsync(
            response,
            GitHubJsonSerializerContext.Default.GitHubActorArray,
            "collaborator list",
            cancellationToken);
        return collaborators;
    }

    public async Task<bool> IsCollaboratorAsync(
        string token,
        string owner,
        string name,
        string login,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(login))
        {
            return false;
        }

        string path =
            $"repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(name)}/collaborators/{Uri.EscapeDataString(login)}";
        using HttpRequestMessage request = CreateAuthenticatedRequest(HttpMethod.Get, path, token);
        using HttpResponseMessage response =
            await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NoContent)
        {
            return true;
        }

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }

        await EnsureSuccessAsync(response, cancellationToken);
        return true;
    }

    public async Task<IReadOnlyList<GitHubLabel>> GetLabelsAsync(
        string token,
        string owner,
        string name,
        int pageSize = 100,
        int pageNumber = 1,
        CancellationToken cancellationToken = default)
    {
        string path =
            $"repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(name)}/labels?per_page={pageSize}&page={pageNumber}";
        using HttpRequestMessage request = CreateAuthenticatedRequest(HttpMethod.Get, path, token);
        using HttpResponseMessage response =
            await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        GitHubLabel[] labels = await ReadResponseAsync(
            response,
            GitHubJsonSerializerContext.Default.GitHubLabelArray,
            "label list",
            cancellationToken);
        return labels;
    }

    public async Task<IReadOnlyList<GitHubMilestone>> GetMilestonesAsync(
        string token,
        string owner,
        string name,
        int pageSize = 100,
        int pageNumber = 1,
        CancellationToken cancellationToken = default)
    {
        string path =
            $"repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(name)}/milestones?state=all&per_page={pageSize}&page={pageNumber}";
        using HttpRequestMessage request = CreateAuthenticatedRequest(HttpMethod.Get, path, token);
        using HttpResponseMessage response =
            await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        GitHubMilestone[] milestones = await ReadResponseAsync(
            response,
            GitHubJsonSerializerContext.Default.GitHubMilestoneArray,
            "milestone list",
            cancellationToken);
        return milestones;
    }

    public async Task<IReadOnlyList<GitHubIssue>> GetIssuesAsync(
        string token,
        string owner,
        string name,
        int pageSize,
        int pageNumber = 1,
        GitHubIssueQueryOptions? queryOptions = null,
        CancellationToken cancellationToken = default)
    {
        GitHubIssueQueryOptions query = queryOptions ?? new GitHubIssueQueryOptions();
        List<string> queryParts =
        [
            $"state={Uri.EscapeDataString(query.State)}",
            $"sort={Uri.EscapeDataString(query.Sort)}",
            $"direction={Uri.EscapeDataString(query.Direction)}",
            $"per_page={pageSize}",
            $"page={pageNumber}"
        ];

        AddOptionalQueryParameter(queryParts, "since", query.Since?.UtcDateTime.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        AddOptionalQueryParameter(queryParts, "labels", query.Labels);
        AddOptionalQueryParameter(queryParts, "milestone", query.Milestone);
        AddOptionalQueryParameter(queryParts, "assignee", query.Assignee);
        AddOptionalQueryParameter(queryParts, "creator", query.Creator);
        AddOptionalQueryParameter(queryParts, "mentioned", query.Mentioned);
        AddOptionalQueryParameter(queryParts, "filter", query.Filter);

        string path =
            $"repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(name)}/issues?{string.Join("&", queryParts)}";
        using HttpRequestMessage request = CreateAuthenticatedRequest(HttpMethod.Get, path, token);
        using HttpResponseMessage response =
            await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        GitHubIssue[] issues = await ReadResponseAsync(
            response,
            GitHubJsonSerializerContext.Default.GitHubIssueArray,
            "issue list",
            cancellationToken);
        return issues.Where(static issue => !issue.IsPullRequest).ToArray();
    }

    public async Task<IReadOnlyList<GitHubIssue>> GetCurrentUserIssuesAsync(
        string token,
        int pageSize,
        int pageNumber = 1,
        GitHubIssueQueryOptions? queryOptions = null,
        CancellationToken cancellationToken = default)
    {
        GitHubIssueQueryOptions query = queryOptions ?? new GitHubIssueQueryOptions();
        List<string> queryParts =
        [
            $"state={Uri.EscapeDataString(query.State)}",
            $"sort={Uri.EscapeDataString(query.Sort)}",
            $"direction={Uri.EscapeDataString(query.Direction)}",
            $"per_page={pageSize}",
            $"page={pageNumber}"
        ];

        AddOptionalQueryParameter(queryParts, "since", query.Since?.UtcDateTime.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        AddOptionalQueryParameter(queryParts, "labels", query.Labels);
        AddOptionalQueryParameter(queryParts, "filter", query.Filter);

        string path = $"issues?{string.Join("&", queryParts)}";
        using HttpRequestMessage request = CreateAuthenticatedRequest(HttpMethod.Get, path, token);
        using HttpResponseMessage response =
            await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        GitHubIssue[] issues = await ReadResponseAsync(
            response,
            GitHubJsonSerializerContext.Default.GitHubIssueArray,
            "current user issue list",
            cancellationToken);
        return issues.Where(static issue => !issue.IsPullRequest).ToArray();
    }

    public async Task<GitHubIssue> GetIssueAsync(
        string token,
        string owner,
        string name,
        int issueNumber,
        CancellationToken cancellationToken = default)
    {
        string path =
            $"repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(name)}/issues/{issueNumber}";
        using HttpRequestMessage request = CreateAuthenticatedRequest(HttpMethod.Get, path, token);
        using HttpResponseMessage response =
            await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        return await ReadResponseAsync(
            response,
            GitHubJsonSerializerContext.Default.GitHubIssue,
            "issue",
            cancellationToken);
    }

    public async Task<IReadOnlyList<GitHubIssueComment>> GetIssueCommentsAsync(
        string token,
        string owner,
        string name,
        int issueNumber,
        int pageSize = 100,
        int pageNumber = 1,
        CancellationToken cancellationToken = default)
    {
        string path =
            $"repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(name)}/issues/{issueNumber}/comments?sort=created&direction=asc&per_page={pageSize}&page={pageNumber}";
        using HttpRequestMessage request = CreateAuthenticatedRequest(HttpMethod.Get, path, token);
        using HttpResponseMessage response =
            await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        GitHubIssueComment[] comments = await ReadResponseAsync(
            response,
            GitHubJsonSerializerContext.Default.GitHubIssueCommentArray,
            "issue comment list",
            cancellationToken);
        return comments;
    }

    public async Task<IReadOnlyList<GitHubIssueEvent>> GetIssueEventsAsync(
        string token,
        string owner,
        string name,
        int issueNumber,
        int pageSize = 100,
        int pageNumber = 1,
        CancellationToken cancellationToken = default)
    {
        string path =
            $"repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(name)}/issues/{issueNumber}/events?per_page={pageSize}&page={pageNumber}";
        using HttpRequestMessage request = CreateAuthenticatedRequest(HttpMethod.Get, path, token);
        using HttpResponseMessage response =
            await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        GitHubIssueEvent[] events = await ReadResponseAsync(
            response,
            GitHubJsonSerializerContext.Default.GitHubIssueEventArray,
            "issue event list",
            cancellationToken);
        return events;
    }

    public async Task<GitHubIssue> CreateIssueAsync(
        string token,
        string owner,
        string name,
        string title,
        string? body,
        CancellationToken cancellationToken = default)
    {
        string path = $"repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(name)}/issues";
        GitHubIssueCreateRequest payload = new()
        {
            Title = title,
            Body = NormalizeLineEndings(body)
        };
        using HttpRequestMessage request = CreateJsonRequest(
            HttpMethod.Post,
            path,
            token,
            payload,
            GitHubJsonSerializerContext.Default.GitHubIssueCreateRequest);
        using HttpResponseMessage response =
            await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        return await ReadResponseAsync(
            response,
            GitHubJsonSerializerContext.Default.GitHubIssue,
            "issue",
            cancellationToken);
    }

    public async Task<GitHubIssue> UpdateIssueAsync(
        string token,
        string owner,
        string name,
        int issueNumber,
        string? title,
        string? body,
        string? state = null,
        CancellationToken cancellationToken = default)
    {
        string path =
            $"repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(name)}/issues/{issueNumber}";
        GitHubIssueUpdateRequest payload = new()
        {
            Title = title,
            Body = NormalizeLineEndings(body),
            State = state
        };
        using HttpRequestMessage request = CreateJsonRequest(
            HttpMethod.Patch,
            path,
            token,
            payload,
            GitHubJsonSerializerContext.Default.GitHubIssueUpdateRequest);
        using HttpResponseMessage response =
            await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        return await ReadResponseAsync(
            response,
            GitHubJsonSerializerContext.Default.GitHubIssue,
            "issue",
            cancellationToken);
    }

    public async Task<GitHubIssue> UpdateIssueMetadataAsync(
        string token,
        string owner,
        string name,
        int issueNumber,
        IReadOnlyList<string> assignees,
        IReadOnlyList<string> labels,
        int? milestone,
        CancellationToken cancellationToken = default)
    {
        string path =
            $"repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(name)}/issues/{issueNumber}";
        GitHubIssueMetadataUpdateRequest payload = new()
        {
            Assignees = assignees,
            Labels = labels,
            Milestone = milestone
        };
        using HttpRequestMessage request = CreateJsonRequest(
            HttpMethod.Patch,
            path,
            token,
            payload,
            GitHubJsonSerializerContext.Default.GitHubIssueMetadataUpdateRequest);
        using HttpResponseMessage response =
            await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        return await ReadResponseAsync(
            response,
            GitHubJsonSerializerContext.Default.GitHubIssue,
            "issue",
            cancellationToken);
    }

    public async Task<GitHubIssueComment> CreateIssueCommentAsync(
        string token,
        string owner,
        string name,
        int issueNumber,
        string body,
        CancellationToken cancellationToken = default)
    {
        string path =
            $"repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(name)}/issues/{issueNumber}/comments";
        GitHubIssueCommentCreateRequest payload = new()
        {
            Body = NormalizeLineEndings(body) ?? string.Empty
        };
        using HttpRequestMessage request = CreateJsonRequest(
            HttpMethod.Post,
            path,
            token,
            payload,
            GitHubJsonSerializerContext.Default.GitHubIssueCommentCreateRequest);
        using HttpResponseMessage response =
            await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        return await ReadResponseAsync(
            response,
            GitHubJsonSerializerContext.Default.GitHubIssueComment,
            "issue comment",
            cancellationToken);
    }

    public async Task<IReadOnlyList<GitHubReaction>> GetIssueReactionsAsync(
        string token,
        string owner,
        string name,
        int issueNumber,
        int pageSize = 100,
        int pageNumber = 1,
        CancellationToken cancellationToken = default)
    {
        string path =
            $"repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(name)}/issues/{issueNumber}/reactions?per_page={pageSize}&page={pageNumber}";
        using HttpRequestMessage request = CreateAuthenticatedRequest(HttpMethod.Get, path, token);
        using HttpResponseMessage response =
            await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        GitHubReaction[] reactions = await ReadResponseAsync(
            response,
            GitHubJsonSerializerContext.Default.GitHubReactionArray,
            "issue reaction list",
            cancellationToken);
        return reactions;
    }

    public async Task<IReadOnlyList<GitHubReaction>> GetIssueCommentReactionsAsync(
        string token,
        string owner,
        string name,
        long commentId,
        int pageSize = 100,
        int pageNumber = 1,
        CancellationToken cancellationToken = default)
    {
        string path =
            $"repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(name)}/issues/comments/{commentId}/reactions?per_page={pageSize}&page={pageNumber}";
        using HttpRequestMessage request = CreateAuthenticatedRequest(HttpMethod.Get, path, token);
        using HttpResponseMessage response =
            await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        GitHubReaction[] reactions = await ReadResponseAsync(
            response,
            GitHubJsonSerializerContext.Default.GitHubReactionArray,
            "issue comment reaction list",
            cancellationToken);
        return reactions;
    }

    public async Task ReactToIssueAsync(
        string token,
        string owner,
        string name,
        int issueNumber,
        string reactionContent,
        CancellationToken cancellationToken = default)
    {
        string path =
            $"repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(name)}/issues/{issueNumber}/reactions";
        GitHubReactionCreateRequest payload = new()
        {
            Content = reactionContent
        };
        using HttpRequestMessage request = CreateJsonRequest(
            HttpMethod.Post,
            path,
            token,
            payload,
            GitHubJsonSerializerContext.Default.GitHubReactionCreateRequest);
        using HttpResponseMessage response =
            await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    public async Task ReactToIssueCommentAsync(
        string token,
        string owner,
        string name,
        long commentId,
        string reactionContent,
        CancellationToken cancellationToken = default)
    {
        string path =
            $"repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(name)}/issues/comments/{commentId}/reactions";
        GitHubReactionCreateRequest payload = new()
        {
            Content = reactionContent
        };
        using HttpRequestMessage request = CreateJsonRequest(
            HttpMethod.Post,
            path,
            token,
            payload,
            GitHubJsonSerializerContext.Default.GitHubReactionCreateRequest);
        using HttpResponseMessage response =
            await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    public async Task DeleteIssueReactionAsync(
        string token,
        string owner,
        string name,
        int issueNumber,
        long reactionId,
        CancellationToken cancellationToken = default)
    {
        string path =
            $"repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(name)}/issues/{issueNumber}/reactions/{reactionId}";
        using HttpRequestMessage request = CreateAuthenticatedRequest(HttpMethod.Delete, path, token);
        using HttpResponseMessage response =
            await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    public async Task DeleteIssueCommentReactionAsync(
        string token,
        string owner,
        string name,
        long commentId,
        long reactionId,
        CancellationToken cancellationToken = default)
    {
        string path =
            $"repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(name)}/issues/comments/{commentId}/reactions/{reactionId}";
        using HttpRequestMessage request = CreateAuthenticatedRequest(HttpMethod.Delete, path, token);
        using HttpResponseMessage response =
            await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    public async Task<IReadOnlyList<GitHubPullRequest>> GetPullRequestsAsync(
        string token,
        string owner,
        string name,
        int pageSize,
        int pageNumber = 1,
        GitHubPullRequestQueryOptions? queryOptions = null,
        CancellationToken cancellationToken = default)
    {
        GitHubPullRequestQueryOptions query = queryOptions ?? new GitHubPullRequestQueryOptions();
        List<string> queryParts =
        [
            $"state={Uri.EscapeDataString(query.State)}",
            $"sort={Uri.EscapeDataString(query.Sort)}",
            $"direction={Uri.EscapeDataString(query.Direction)}",
            $"per_page={pageSize}",
            $"page={pageNumber}"
        ];

        AddOptionalQueryParameter(queryParts, "head", query.Head);
        AddOptionalQueryParameter(queryParts, "base", query.Base);

        string path =
            $"repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(name)}/pulls?{string.Join("&", queryParts)}";
        using HttpRequestMessage request = CreateAuthenticatedRequest(HttpMethod.Get, path, token);
        using HttpResponseMessage response =
            await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        GitHubPullRequest[] pullRequests = await ReadResponseAsync(
            response,
            GitHubJsonSerializerContext.Default.GitHubPullRequestArray,
            "pull request list",
            cancellationToken);
        return pullRequests;
    }

    public async Task<GitHubPullRequest> GetPullRequestAsync(
        string token,
        string owner,
        string name,
        int pullRequestNumber,
        CancellationToken cancellationToken = default)
    {
        string path =
            $"repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(name)}/pulls/{pullRequestNumber}";
        using HttpRequestMessage request = CreateAuthenticatedRequest(HttpMethod.Get, path, token);
        using HttpResponseMessage response =
            await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        return await ReadResponseAsync(
            response,
            GitHubJsonSerializerContext.Default.GitHubPullRequest,
            "pull request",
            cancellationToken);
    }

    public async Task<IReadOnlyList<GitHubCommit>> GetPullRequestCommitsAsync(
        string token,
        string owner,
        string name,
        int pullRequestNumber,
        int pageSize = 100,
        int pageNumber = 1,
        CancellationToken cancellationToken = default)
    {
        string path =
            $"repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(name)}/pulls/{pullRequestNumber}/commits?per_page={pageSize}&page={pageNumber}";
        using HttpRequestMessage request = CreateAuthenticatedRequest(HttpMethod.Get, path, token);
        using HttpResponseMessage response =
            await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        GitHubCommit[] commits = await ReadResponseAsync(
            response,
            GitHubJsonSerializerContext.Default.GitHubCommitArray,
            "pull request commit list",
            cancellationToken);
        return commits;
    }

    public async Task<IReadOnlyList<GitHubPullRequestReview>> GetPullRequestReviewsAsync(
        string token,
        string owner,
        string name,
        int pullRequestNumber,
        int pageSize = 100,
        int pageNumber = 1,
        CancellationToken cancellationToken = default)
    {
        string path =
            $"repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(name)}/pulls/{pullRequestNumber}/reviews?per_page={pageSize}&page={pageNumber}";
        using HttpRequestMessage request = CreateAuthenticatedRequest(HttpMethod.Get, path, token);
        using HttpResponseMessage response =
            await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        GitHubPullRequestReview[] reviews = await ReadResponseAsync(
            response,
            GitHubJsonSerializerContext.Default.GitHubPullRequestReviewArray,
            "pull request review list",
            cancellationToken);
        return reviews;
    }

    public async Task<IReadOnlyList<GitHubPullRequestReviewComment>> GetPullRequestReviewCommentsAsync(
        string token,
        string owner,
        string name,
        int pullRequestNumber,
        int pageSize = 100,
        int pageNumber = 1,
        CancellationToken cancellationToken = default)
    {
        string path =
            $"repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(name)}/pulls/{pullRequestNumber}/comments?per_page={pageSize}&page={pageNumber}";
        using HttpRequestMessage request = CreateAuthenticatedRequest(HttpMethod.Get, path, token);
        using HttpResponseMessage response =
            await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        GitHubPullRequestReviewComment[] comments = await ReadResponseAsync(
            response,
            GitHubJsonSerializerContext.Default.GitHubPullRequestReviewCommentArray,
            "pull request review comment list",
            cancellationToken);
        return comments;
    }

    public async Task<GitHubPullRequestReviewComment> ReplyToPullRequestReviewCommentAsync(
        string token,
        string owner,
        string name,
        int pullRequestNumber,
        long commentId,
        string body,
        CancellationToken cancellationToken = default)
    {
        string path =
            $"repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(name)}/pulls/{pullRequestNumber}/comments/{commentId}/replies";
        GitHubIssueCommentCreateRequest payload = new()
        {
            Body = NormalizeLineEndings(body) ?? string.Empty
        };
        using HttpRequestMessage request = CreateJsonRequest(
            HttpMethod.Post,
            path,
            token,
            payload,
            GitHubJsonSerializerContext.Default.GitHubIssueCommentCreateRequest);
        using HttpResponseMessage response =
            await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        return await ReadResponseAsync(
            response,
            GitHubJsonSerializerContext.Default.GitHubPullRequestReviewComment,
            "pull request review comment",
            cancellationToken);
    }

    public async Task<GitHubPullRequest> CreatePullRequestAsync(
        string token,
        string owner,
        string name,
        string title,
        string head,
        string @base,
        string? body,
        CancellationToken cancellationToken = default)
    {
        string path = $"repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(name)}/pulls";
        GitHubPullRequestCreateRequest payload = new()
        {
            Title = title,
            Head = head,
            Base = @base,
            Body = NormalizeLineEndings(body)
        };
        using HttpRequestMessage request = CreateJsonRequest(
            HttpMethod.Post,
            path,
            token,
            payload,
            GitHubJsonSerializerContext.Default.GitHubPullRequestCreateRequest);
        using HttpResponseMessage response =
            await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        return await ReadResponseAsync(
            response,
            GitHubJsonSerializerContext.Default.GitHubPullRequest,
            "pull request",
            cancellationToken);
    }

    public async Task<GitHubPullRequest> UpdatePullRequestAsync(
        string token,
        string owner,
        string name,
        int pullRequestNumber,
        string? title,
        string? body,
        string? state = null,
        CancellationToken cancellationToken = default)
    {
        string path =
            $"repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(name)}/pulls/{pullRequestNumber}";
        GitHubPullRequestUpdateRequest payload = new()
        {
            Title = title,
            Body = NormalizeLineEndings(body),
            State = state
        };
        using HttpRequestMessage request = CreateJsonRequest(
            HttpMethod.Patch,
            path,
            token,
            payload,
            GitHubJsonSerializerContext.Default.GitHubPullRequestUpdateRequest);
        using HttpResponseMessage response =
            await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        return await ReadResponseAsync(
            response,
            GitHubJsonSerializerContext.Default.GitHubPullRequest,
            "pull request",
            cancellationToken);
    }

    public async Task AddPullRequestReviewersAsync(
        string token,
        string owner,
        string name,
        int pullRequestNumber,
        IReadOnlyList<string> reviewers,
        CancellationToken cancellationToken = default)
    {
        if (reviewers.Count == 0)
        {
            return;
        }

        string path =
            $"repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(name)}/pulls/{pullRequestNumber}/requested_reviewers";
        GitHubPullRequestReviewersUpdateRequest payload = new()
        {
            Reviewers = reviewers
        };
        using HttpRequestMessage request = CreateJsonRequest(
            HttpMethod.Post,
            path,
            token,
            payload,
            GitHubJsonSerializerContext.Default.GitHubPullRequestReviewersUpdateRequest);
        using HttpResponseMessage response =
            await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    public async Task RemovePullRequestReviewersAsync(
        string token,
        string owner,
        string name,
        int pullRequestNumber,
        IReadOnlyList<string> reviewers,
        CancellationToken cancellationToken = default)
    {
        if (reviewers.Count == 0)
        {
            return;
        }

        string path =
            $"repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(name)}/pulls/{pullRequestNumber}/requested_reviewers";
        GitHubPullRequestReviewersUpdateRequest payload = new()
        {
            Reviewers = reviewers
        };
        using HttpRequestMessage request = CreateJsonRequest(
            HttpMethod.Delete,
            path,
            token,
            payload,
            GitHubJsonSerializerContext.Default.GitHubPullRequestReviewersUpdateRequest);
        using HttpResponseMessage response =
            await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    public async Task<IReadOnlyList<GitHubReaction>> GetPullRequestReviewCommentReactionsAsync(
        string token,
        string owner,
        string name,
        long commentId,
        int pageSize = 100,
        int pageNumber = 1,
        CancellationToken cancellationToken = default)
    {
        string path =
            $"repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(name)}/pulls/comments/{commentId}/reactions?per_page={pageSize}&page={pageNumber}";
        using HttpRequestMessage request = CreateAuthenticatedRequest(HttpMethod.Get, path, token);
        using HttpResponseMessage response =
            await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        GitHubReaction[] reactions = await ReadResponseAsync(
            response,
            GitHubJsonSerializerContext.Default.GitHubReactionArray,
            "pull request review comment reaction list",
            cancellationToken);
        return reactions;
    }

    public async Task ReactToPullRequestReviewCommentAsync(
        string token,
        string owner,
        string name,
        long commentId,
        string reactionContent,
        CancellationToken cancellationToken = default)
    {
        string path =
            $"repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(name)}/pulls/comments/{commentId}/reactions";
        GitHubReactionCreateRequest payload = new()
        {
            Content = reactionContent
        };
        using HttpRequestMessage request = CreateJsonRequest(
            HttpMethod.Post,
            path,
            token,
            payload,
            GitHubJsonSerializerContext.Default.GitHubReactionCreateRequest);
        using HttpResponseMessage response =
            await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    public async Task DeletePullRequestReviewCommentReactionAsync(
        string token,
        string owner,
        string name,
        long commentId,
        long reactionId,
        CancellationToken cancellationToken = default)
    {
        string path =
            $"repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(name)}/pulls/comments/{commentId}/reactions/{reactionId}";
        using HttpRequestMessage request = CreateAuthenticatedRequest(HttpMethod.Delete, path, token);
        using HttpResponseMessage response =
            await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    public async Task<GitHubPullRequestMergeResult> MergePullRequestAsync(
        string token,
        string owner,
        string name,
        int pullRequestNumber,
        string mergeMethod,
        string? commitTitle,
        string? commitMessage,
        CancellationToken cancellationToken = default)
    {
        string path =
            $"repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(name)}/pulls/{pullRequestNumber}/merge";
        GitHubPullRequestMergeRequest payload = new()
        {
            MergeMethod = mergeMethod,
            CommitTitle = string.IsNullOrWhiteSpace(commitTitle) ? null : commitTitle,
            CommitMessage = NormalizeLineEndings(commitMessage)
        };
        using HttpRequestMessage request = CreateJsonRequest(
            HttpMethod.Put,
            path,
            token,
            payload,
            GitHubJsonSerializerContext.Default.GitHubPullRequestMergeRequest);
        using HttpResponseMessage response =
            await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        return await ReadResponseAsync(
            response,
            GitHubJsonSerializerContext.Default.GitHubPullRequestMergeResult,
            "pull request merge result",
            cancellationToken);
    }

    public async Task<IReadOnlyList<GitHubCommit>> GetCommitsAsync(
        string token,
        string owner,
        string name,
        string? gitRef,
        int pageSize,
        int pageNumber = 1,
        CancellationToken cancellationToken = default)
    {
        string query = $"per_page={pageSize}&page={pageNumber}";
        if (!string.IsNullOrWhiteSpace(gitRef))
        {
            query = $"{query}&sha={Uri.EscapeDataString(gitRef)}";
        }

        string path = $"repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(name)}/commits?{query}";
        using HttpRequestMessage request = CreateAuthenticatedRequest(HttpMethod.Get, path, token);
        using HttpResponseMessage response =
            await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        GitHubCommit[] commits = await ReadResponseAsync(
            response,
            GitHubJsonSerializerContext.Default.GitHubCommitArray,
            "commit list",
            cancellationToken);
        return commits;
    }

    public async Task<GitHubCommit> GetCommitAsync(
        string token,
        string owner,
        string name,
        string gitRef,
        CancellationToken cancellationToken = default)
    {
        string path =
            $"repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(name)}/commits/{Uri.EscapeDataString(gitRef)}";
        using HttpRequestMessage request = CreateAuthenticatedRequest(HttpMethod.Get, path, token);
        using HttpResponseMessage response =
            await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        return await ReadResponseAsync(
            response,
            GitHubJsonSerializerContext.Default.GitHubCommit,
            "commit",
            cancellationToken);
    }

    public async Task<IReadOnlyList<GitHubCommitComment>> GetCommitCommentsAsync(
        string token,
        string owner,
        string name,
        string gitRef,
        int pageSize = 100,
        int pageNumber = 1,
        CancellationToken cancellationToken = default)
    {
        string path =
            $"repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(name)}/commits/{Uri.EscapeDataString(gitRef)}/comments?per_page={pageSize}&page={pageNumber}";
        using HttpRequestMessage request = CreateAuthenticatedRequest(HttpMethod.Get, path, token);
        using HttpResponseMessage response =
            await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        GitHubCommitComment[] comments = await ReadResponseAsync(
            response,
            GitHubJsonSerializerContext.Default.GitHubCommitCommentArray,
            "commit comment list",
            cancellationToken);
        return comments;
    }

    public async Task<GitHubRepositoryContent> GetRepositoryContentAsync(
        string token,
        string owner,
        string name,
        string path,
        string? gitRef = null,
        CancellationToken cancellationToken = default)
    {
        string requestPath = BuildRepositoryContentsPath(owner, name, path, gitRef);
        using HttpRequestMessage request = CreateAuthenticatedRequest(HttpMethod.Get, requestPath, token);
        using HttpResponseMessage response =
            await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        return await ReadResponseAsync(
            response,
            GitHubJsonSerializerContext.Default.GitHubRepositoryContent,
            "repository content",
            cancellationToken);
    }

    public async Task<IReadOnlyList<GitHubRepositoryContent>> GetRepositoryContentsAsync(
        string token,
        string owner,
        string name,
        string? path,
        string? gitRef = null,
        CancellationToken cancellationToken = default)
    {
        string requestPath = BuildRepositoryContentsPath(owner, name, path, gitRef);
        using HttpRequestMessage request = CreateAuthenticatedRequest(HttpMethod.Get, requestPath, token);
        using HttpResponseMessage response =
            await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        GitHubRepositoryContent[] contents = await ReadResponseAsync(
            response,
            GitHubJsonSerializerContext.Default.GitHubRepositoryContentArray,
            "repository content list",
            cancellationToken);
        return contents;
    }

    public async Task<GitHubBlob> GetBlobAsync(
        string token,
        string owner,
        string name,
        string sha,
        CancellationToken cancellationToken = default)
    {
        string path =
            $"repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(name)}/git/blobs/{Uri.EscapeDataString(sha)}";
        using HttpRequestMessage request = CreateAuthenticatedRequest(HttpMethod.Get, path, token);
        using HttpResponseMessage response =
            await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        return await ReadResponseAsync(
            response,
            GitHubJsonSerializerContext.Default.GitHubBlob,
            "blob",
            cancellationToken);
    }

    public async Task<GitHubCompareResult> CompareCommitsAsync(
        string token,
        string owner,
        string name,
        string @base,
        string head,
        CancellationToken cancellationToken = default)
    {
        string path =
            $"repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(name)}/compare/{Uri.EscapeDataString(@base)}...{Uri.EscapeDataString(head)}";
        using HttpRequestMessage request = CreateAuthenticatedRequest(HttpMethod.Get, path, token);
        using HttpResponseMessage response =
            await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        return await ReadResponseAsync(
            response,
            GitHubJsonSerializerContext.Default.GitHubCompareResult,
            "compare result",
            cancellationToken);
    }

    public async Task<IReadOnlyList<GitHubCheckRun>> GetCheckRunsAsync(
        string token,
        string owner,
        string name,
        string gitRef,
        int pageSize = 100,
        int pageNumber = 1,
        string? checkName = null,
        string? status = null,
        string? filter = null,
        CancellationToken cancellationToken = default)
    {
        List<string> queryParts =
        [
            $"per_page={pageSize}",
            $"page={pageNumber}"
        ];
        AddOptionalQueryParameter(queryParts, "check_name", checkName);
        AddOptionalQueryParameter(queryParts, "status", status);
        AddOptionalQueryParameter(queryParts, "filter", filter);

        string path =
            $"repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(name)}/commits/{Uri.EscapeDataString(gitRef)}/check-runs?{string.Join("&", queryParts)}";
        using HttpRequestMessage request = CreateAuthenticatedRequest(HttpMethod.Get, path, token);
        using HttpResponseMessage response =
            await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        GitHubCheckRunResponse result = await ReadResponseAsync(
            response,
            GitHubJsonSerializerContext.Default.GitHubCheckRunResponse,
            "check run list",
            cancellationToken);
        return result.CheckRuns;
    }

    public async Task<IReadOnlyList<GitHubRepository>> SearchRepositoriesAsync(
        string token,
        string query,
        int pageSize,
        int pageNumber = 1,
        CancellationToken cancellationToken = default)
    {
        GitHubRepository? exactMatch = null;
        if (TryParseRepositoryFullName(query, out string owner, out string name))
        {
            try
            {
                exactMatch = await GetRepositoryAsync(token, owner, name, cancellationToken);
            }
            catch (GitHubApiException)
            {
                exactMatch = null;
            }
            catch (HttpRequestException)
            {
                exactMatch = null;
            }
        }

        string path =
            $"search/repositories?q={Uri.EscapeDataString(query)}&per_page={pageSize}&page={pageNumber}";
        using HttpRequestMessage request = CreateAuthenticatedRequest(HttpMethod.Get, path, token);
        using HttpResponseMessage response =
            await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        GitHubRepositorySearchResponse searchResponse = await ReadResponseAsync(
            response,
            GitHubJsonSerializerContext.Default.GitHubRepositorySearchResponse,
            "repository search",
            cancellationToken);
        if (exactMatch is null)
        {
            return searchResponse.Items;
        }

        return searchResponse.Items
            .Prepend(exactMatch)
            .GroupBy(repository => repository.Id)
            .Select(group => group.First())
            .Take(pageSize)
            .ToList();
    }

    public async Task<GitHubTree> GetTreeAsync(
        string token,
        string owner,
        string name,
        string treeSha,
        bool recursive,
        CancellationToken cancellationToken = default)
    {
        string path =
            $"repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(name)}/git/trees/{Uri.EscapeDataString(treeSha)}";
        if (recursive)
        {
            path += "?recursive=1";
        }

        using HttpRequestMessage request = CreateAuthenticatedRequest(HttpMethod.Get, path, token);
        using HttpResponseMessage response =
            await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        return await ReadResponseAsync(
            response,
            GitHubJsonSerializerContext.Default.GitHubTree,
            "git tree",
            cancellationToken);
    }

    private static HttpRequestMessage CreateAuthenticatedRequest(HttpMethod method, string relativePath, string token)
    {
        HttpRequestMessage request = new(method, relativePath);
        if (!IsPublicAccessToken(token))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return request;
    }

    public static bool IsPublicAccessToken(string? token) =>
        string.Equals(token, PublicAccessToken, StringComparison.Ordinal);

    private static HttpRequestMessage CreateJsonRequest<T>(
        HttpMethod method,
        string relativePath,
        string token,
        T payload,
        JsonTypeInfo<T> jsonTypeInfo)
    {
        HttpRequestMessage request = CreateAuthenticatedRequest(method, relativePath, token);
        request.Content = JsonContent.Create(payload, jsonTypeInfo);
        return request;
    }

    private static string? NormalizeLineEndings(string? value)
    {
        return string.IsNullOrEmpty(value)
            ? value
            : value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
    }

    private static void AddOptionalQueryParameter(List<string> queryParts, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            queryParts.Add($"{name}={Uri.EscapeDataString(value)}");
        }
    }

    private static bool TryParseRepositoryFullName(string query, out string owner, out string name)
    {
        owner = string.Empty;
        name = string.Empty;

        if (string.IsNullOrWhiteSpace(query))
        {
            return false;
        }

        string trimmed = query.Trim();
        if (trimmed.Contains(' ', StringComparison.Ordinal))
        {
            return false;
        }

        string[] parts = trimmed.Split('/', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2)
        {
            return false;
        }

        owner = parts[0];
        name = parts[1];
        return owner.Length > 0 && name.Length > 0;
    }

    private static string BuildStarredRepositoriesPath(
        string basePath,
        int pageSize,
        int pageNumber,
        string? sort,
        string? direction)
    {
        List<string> queryParts =
        [
            $"per_page={pageSize}",
            $"page={pageNumber}"
        ];
        AddOptionalQueryParameter(queryParts, "sort", sort);
        AddOptionalQueryParameter(queryParts, "direction", direction);
        return $"{basePath}?{string.Join("&", queryParts)}";
    }

    private static string BuildRepositoryContentsPath(string owner, string name, string? path, string? gitRef)
    {
        string basePath = $"repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(name)}/contents";
        if (!string.IsNullOrWhiteSpace(path))
        {
            string normalizedPath = string.Join(
                "/",
                path.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.EscapeDataString));
            basePath = $"{basePath}/{normalizedPath}";
        }

        if (string.IsNullOrWhiteSpace(gitRef))
        {
            return basePath;
        }

        return $"{basePath}?ref={Uri.EscapeDataString(gitRef)}";
    }

    private static async Task<T> ReadResponseAsync<T>(
        HttpResponseMessage response,
        JsonTypeInfo<T> jsonTypeInfo,
        string payloadName,
        CancellationToken cancellationToken)
    {
        try
        {
            T? result = await response.Content.ReadFromJsonAsync(jsonTypeInfo, cancellationToken);
            return result ?? throw new GitHubApiException(HttpStatusCode.OK, $"GitHub returned an empty {payloadName} payload.");
        }
        catch (JsonException ex)
        {
            throw new GitHubApiException(HttpStatusCode.OK, $"GitHub returned an invalid {payloadName} payload: {ex.Message}");
        }
        catch (NotSupportedException ex)
        {
            throw new GitHubApiException(HttpStatusCode.OK, $"GitHub returned an unsupported {payloadName} payload: {ex.Message}");
        }
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        GitHubApiError? error = null;
        try
        {
            error = await response.Content.ReadFromJsonAsync(
                GitHubJsonSerializerContext.Default.GitHubApiError,
                cancellationToken);
        }
        catch (JsonException)
        {
        }
        catch (NotSupportedException)
        {
        }

        string message = string.IsNullOrWhiteSpace(error?.Message)
            ? $"GitHub request failed with status code {(int)response.StatusCode}."
            : error.Message;

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            throw new GitHubAuthenticationException(message);
        }

        throw new GitHubApiException(response.StatusCode, message);
    }
}
