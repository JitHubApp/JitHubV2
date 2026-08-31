using System;
using System.Globalization;
using System.Linq;
using JitHub.Services.Markdown;

namespace JitHub.Services;

public enum ProfileStatKind
{
    Repositories,
    Stars,
    Followers,
    Following,
    Gists
}

public enum ProfileStatDestinationKind
{
    CanonicalRepositories,
    CanonicalStars,
    CanonicalGists,
    PublicRepositoriesMode,
    PublicStarsMode,
    FollowersMode,
    FollowingMode,
    ExternalPublicGists
}

public static class ProfileNavigationPolicy
{
    public static ProfileStatDestinationKind GetStatDestination(bool authenticatedProfile, ProfileStatKind stat) =>
        stat switch
        {
            ProfileStatKind.Repositories when authenticatedProfile => ProfileStatDestinationKind.CanonicalRepositories,
            ProfileStatKind.Repositories => ProfileStatDestinationKind.PublicRepositoriesMode,
            ProfileStatKind.Stars when authenticatedProfile => ProfileStatDestinationKind.CanonicalStars,
            ProfileStatKind.Stars => ProfileStatDestinationKind.PublicStarsMode,
            ProfileStatKind.Followers => ProfileStatDestinationKind.FollowersMode,
            ProfileStatKind.Following => ProfileStatDestinationKind.FollowingMode,
            ProfileStatKind.Gists when authenticatedProfile => ProfileStatDestinationKind.CanonicalGists,
            _ => ProfileStatDestinationKind.ExternalPublicGists
        };
}

public enum ProfileReadmeRouteKind
{
    None,
    User,
    Repository,
    Issue,
    PullRequest,
    External
}

public readonly record struct ProfileReadmeRoute(
    ProfileReadmeRouteKind Kind,
    string? Owner,
    string? Repository,
    int? Number)
{
    public static ProfileReadmeRoute External => new(ProfileReadmeRouteKind.External, null, null, null);
}

public static class ProfileReadmeRouteClassifier
{
    public static ProfileReadmeRoute Classify(Uri? uri)
    {
        MarkdownGitHubRoute basic = MarkdownLinkNavigationPolicy.ClassifyGitHubRoute(uri);
        if (basic.Kind == MarkdownGitHubRouteKind.User)
        {
            return new ProfileReadmeRoute(ProfileReadmeRouteKind.User, basic.Owner, null, null);
        }

        if (basic.Kind == MarkdownGitHubRouteKind.Repository)
        {
            return new ProfileReadmeRoute(ProfileReadmeRouteKind.Repository, basic.Owner, basic.Repository, null);
        }

        if (basic.Kind is MarkdownGitHubRouteKind.Issue or MarkdownGitHubRouteKind.PullRequest)
        {
            return new ProfileReadmeRoute(
                basic.Kind == MarkdownGitHubRouteKind.Issue
                    ? ProfileReadmeRouteKind.Issue
                    : ProfileReadmeRouteKind.PullRequest,
                basic.Owner,
                basic.Repository,
                basic.Number);
        }

        if (uri is null || !uri.IsAbsoluteUri
            || !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
        {
            return ProfileReadmeRoute.External;
        }

        string[] segments = uri.AbsolutePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(Uri.UnescapeDataString)
            .ToArray();
        if (segments.Length == 4
            && int.TryParse(segments[3], NumberStyles.None, CultureInfo.InvariantCulture, out int number)
            && number > 0)
        {
            ProfileReadmeRouteKind kind = segments[2].Equals("issues", StringComparison.OrdinalIgnoreCase)
                ? ProfileReadmeRouteKind.Issue
                : segments[2].Equals("pull", StringComparison.OrdinalIgnoreCase)
                    ? ProfileReadmeRouteKind.PullRequest
                    : ProfileReadmeRouteKind.None;
            if (kind != ProfileReadmeRouteKind.None)
            {
                return new ProfileReadmeRoute(kind, segments[0], segments[1], number);
            }
        }

        return ProfileReadmeRoute.External;
    }
}
