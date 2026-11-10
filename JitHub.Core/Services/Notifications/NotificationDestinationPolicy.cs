using System;
using System.Linq;
using JitHub.Models.GitHub;

namespace JitHub.Services;

public enum NotificationInternalDestinationKind
{
    Issue,
    PullRequest,
    Commit
}

public readonly record struct NotificationInternalDestination(
    NotificationInternalDestinationKind Kind,
    int Number,
    string GitRef);

public static class NotificationDestinationPolicy
{
    public static bool TryResolveInternal(
        GitHubNotificationThread notification,
        out NotificationInternalDestination destination)
    {
        ArgumentNullException.ThrowIfNull(notification);
        destination = default;
        if (!TryParseSubjectPath(notification, out string resource, out string identifier))
        {
            return false;
        }

        string type = notification.Subject.Type?.Trim() ?? string.Empty;
        if (string.Equals(type, "Issue", StringComparison.Ordinal) &&
            string.Equals(resource, "issues", StringComparison.Ordinal) &&
            int.TryParse(identifier, out int issueNumber) &&
            issueNumber > 0)
        {
            destination = new NotificationInternalDestination(
                NotificationInternalDestinationKind.Issue,
                issueNumber,
                string.Empty);
            return true;
        }

        if (string.Equals(type, "PullRequest", StringComparison.Ordinal) &&
            string.Equals(resource, "pulls", StringComparison.Ordinal) &&
            int.TryParse(identifier, out int pullRequestNumber) &&
            pullRequestNumber > 0)
        {
            destination = new NotificationInternalDestination(
                NotificationInternalDestinationKind.PullRequest,
                pullRequestNumber,
                string.Empty);
            return true;
        }

        if (string.Equals(type, "Commit", StringComparison.Ordinal) &&
            string.Equals(resource, "commits", StringComparison.Ordinal) &&
            IsCommitIdentifier(identifier))
        {
            destination = new NotificationInternalDestination(
                NotificationInternalDestinationKind.Commit,
                0,
                identifier);
            return true;
        }

        return false;
    }

    public static Uri? ResolveWebUri(GitHubNotificationThread notification)
    {
        ArgumentNullException.ThrowIfNull(notification);
        string fullName = notification.Repository.FullName.Trim();
        string[] parts = fullName.Split('/', 2, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2)
        {
            return null;
        }

        string repositoryUrl = $"https://github.com/{Uri.EscapeDataString(parts[0])}/{Uri.EscapeDataString(parts[1])}";
        string segment = Uri.EscapeDataString(LastPathSegment(notification.Subject.Url));
        string destination = notification.Subject.Type?.Trim() switch
        {
            "Release" => $"{repositoryUrl}/releases",
            "Discussion" when !string.IsNullOrWhiteSpace(segment) => $"{repositoryUrl}/discussions/{segment}",
            // A Check Suite subject URL ends in the suite id, not a commit SHA.
            // GitHub's notification payload does not contain enough information
            // to construct a truthful commit/checks route without another read.
            "CheckSuite" => $"{repositoryUrl}/actions",
            "WorkflowRun" when !string.IsNullOrWhiteSpace(segment) => $"{repositoryUrl}/actions/runs/{segment}",
            "Repository" => repositoryUrl,
            "RepositoryVulnerabilityAlert" => $"{repositoryUrl}/security/dependabot",
            "SecurityAdvisory" => $"{repositoryUrl}/security/advisories",
            "Deployment" => $"{repositoryUrl}/deployments",
            "RepositoryInvitation" => "https://github.com/notifications",
            _ => "https://github.com/notifications"
        };
        return Uri.TryCreate(destination, UriKind.Absolute, out Uri? uri) ? uri : null;
    }

    private static bool TryParseSubjectPath(
        GitHubNotificationThread notification,
        out string resource,
        out string identifier)
    {
        resource = string.Empty;
        identifier = string.Empty;
        if (!Uri.TryCreate(notification.Subject.Url, UriKind.Absolute, out Uri? uri) ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(uri.Host, "api.github.com", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string[] path = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (path.Length != 5 ||
            !string.Equals(path[0], "repos", StringComparison.Ordinal) ||
            !string.Equals(
                $"{Uri.UnescapeDataString(path[1])}/{Uri.UnescapeDataString(path[2])}",
                notification.Repository.FullName,
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        resource = path[3];
        identifier = Uri.UnescapeDataString(path[4]);
        return true;
    }

    private static bool IsCommitIdentifier(string value) =>
        value.Length is >= 7 and <= 64 && value.All(static character => Uri.IsHexDigit(character));

    private static string LastPathSegment(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return string.Empty;
        }

        string trimmed = url.Trim().TrimEnd('/');
        int slash = trimmed.LastIndexOf('/');
        return slash >= 0 && slash < trimmed.Length - 1
            ? trimmed[(slash + 1)..]
            : trimmed;
    }
}
