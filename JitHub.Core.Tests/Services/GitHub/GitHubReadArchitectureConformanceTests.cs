using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace JitHub.Core.Tests.Services.GitHub;

public sealed class GitHubReadArchitectureConformanceTests
{
    [Fact]
    public void ProfileGraphQlReadsUseTheSharedAccountPartitionedQueryFacade()
    {
        string root = FindRepositoryRoot();
        string profile = Read(root, "JitHub.Core", "Services", "Profile", "GitHubProfileQueryService.cs");
        string graphQlQuery = Read(root, "JitHub.Core", "Services", "Profile", "GitHubGraphQlQueryService.cs");

        Assert.Contains("IGitHubGraphQlQueryService", profile, StringComparison.Ordinal);
        Assert.Contains("_graphQlQueryService.GetAsync", profile, StringComparison.Ordinal);
        Assert.Contains("QueryFetchPolicy.StaleFirst", profile, StringComparison.Ordinal);
        Assert.DoesNotContain("IGitHubGraphQlTransport", profile, StringComparison.Ordinal);
        Assert.DoesNotContain("RefreshGraphQlAsync", profile, StringComparison.Ordinal);
        Assert.DoesNotContain("profile.graphql.background_refresh", profile, StringComparison.Ordinal);

        Assert.Contains("IGitHubRequestQueue", graphQlQuery, StringComparison.Ordinal);
        Assert.Contains("GitHubAccountPartition.Require", graphQlQuery, StringComparison.Ordinal);
        Assert.Contains("_cacheStore.TryGetAsync", graphQlQuery, StringComparison.Ordinal);
        Assert.Contains("_cacheStore.PutAsync", graphQlQuery, StringComparison.Ordinal);
        Assert.Contains("GitHubRequestPriority.BackgroundRefresh", graphQlQuery, StringComparison.Ordinal);
        Assert.Contains("github.graphql.background_refresh", graphQlQuery, StringComparison.Ordinal);

        Assert.DoesNotContain("_httpClient.GetAsync", profile, StringComparison.Ordinal);
        Assert.Contains("new(HttpMethod.Patch, \"user\")", profile, StringComparison.Ordinal);
        Assert.Contains("SendFollowMutationAsync(accessToken, userId, login, HttpMethod.Put", profile, StringComparison.Ordinal);
        Assert.Contains("SendFollowMutationAsync(accessToken, userId, login, HttpMethod.Delete", profile, StringComparison.Ordinal);
    }

    private static string Read(string root, params string[] segments) =>
        File.ReadAllText(Path.Combine(new[] { root }.Concat(segments).ToArray()));

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Directory.Build.props")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not find the JitHub repository root.");
    }
}
