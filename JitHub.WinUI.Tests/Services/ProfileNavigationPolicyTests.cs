using System;
using JitHub.Services;
using Xunit;

namespace JitHub.WinUI.Tests.Services;

public sealed class ProfileNavigationPolicyTests
{
    [Theory]
    [InlineData(ProfileStatKind.Repositories, ProfileStatDestinationKind.CanonicalRepositories)]
    [InlineData(ProfileStatKind.Stars, ProfileStatDestinationKind.CanonicalStars)]
    [InlineData(ProfileStatKind.Gists, ProfileStatDestinationKind.CanonicalGists)]
    public void AuthenticatedLibraryStatsRouteToCanonicalWorkspaces(
        ProfileStatKind stat,
        ProfileStatDestinationKind expected) =>
        Assert.Equal(expected, ProfileNavigationPolicy.GetStatDestination(true, stat));

    [Theory]
    [InlineData("https://github.com/octocat", ProfileReadmeRouteKind.User, null, null)]
    [InlineData("https://github.com/octocat/Hello-World", ProfileReadmeRouteKind.Repository, "Hello-World", null)]
    [InlineData("https://github.com/octocat/Hello-World/issues/42", ProfileReadmeRouteKind.Issue, "Hello-World", 42)]
    [InlineData("https://github.com/octocat/Hello-World/pull/17", ProfileReadmeRouteKind.PullRequest, "Hello-World", 17)]
    [InlineData("https://github.com/octocat/Hello-World/actions", ProfileReadmeRouteKind.External, null, null)]
    [InlineData("https://example.com/octocat", ProfileReadmeRouteKind.External, null, null)]
    public void ReadmeClassifierSeparatesInternalDestinationsFromExternalLinks(
        string value,
        ProfileReadmeRouteKind kind,
        string? repository,
        int? number)
    {
        ProfileReadmeRoute route = ProfileReadmeRouteClassifier.Classify(new Uri(value));

        Assert.Equal(kind, route.Kind);
        Assert.Equal(repository, route.Repository);
        Assert.Equal(number, route.Number);
    }
}
