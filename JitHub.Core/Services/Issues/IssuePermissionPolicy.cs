using System;
using JitHub.Models.GitHub;

namespace JitHub.Services;

public sealed record IssueViewerCapabilities(
    bool CanCreateIssue,
    bool CanEditIssue,
    bool CanManageMetadata,
    bool CanChangeState,
    bool CanComment,
    bool CanReact,
    string? DisabledReason = null);

public static class IssuePermissionPolicy
{
    public static bool IsPermissionDenied(GitHubApiException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception is not GitHubRateLimitException &&
            exception.StatusCode == System.Net.HttpStatusCode.Forbidden;
    }

    public static IssueViewerCapabilities Evaluate(
        GitHubRepository? repository,
        GitHubIssue? issue,
        string? viewerLogin,
        bool isAuthenticated,
        bool isPublicPreview)
    {
        if (repository is null || isPublicPreview || !isAuthenticated)
        {
            return new IssueViewerCapabilities(false, false, false, false, false, false, "Sign in to modify issues.");
        }

        if (repository.Archived)
        {
            return new IssueViewerCapabilities(false, false, false, false, false, false, "Archived repositories are read-only.");
        }

        GitHubRepositoryPermissions? permissions = repository.Permissions;
        bool isRepositoryOwner = LoginEquals(repository.Owner.Login, viewerLogin);
        bool hasWrite = isRepositoryOwner || permissions is { Admin: true } or { Maintain: true } or { Push: true };
        bool hasTriage = hasWrite || permissions?.Triage == true;
        bool canCreate = repository.HasIssues is not false;
        if (issue is null)
        {
            return new IssueViewerCapabilities(canCreate, false, false, false, false, false);
        }

        bool isAuthor = LoginEquals(issue.User?.Login, viewerLogin);
        bool canParticipate = !issue.Locked || hasWrite;
        return new IssueViewerCapabilities(
            canCreate,
            isAuthor || hasWrite,
            hasTriage,
            isAuthor || hasTriage,
            canParticipate,
            canParticipate,
            issue.Locked && !hasWrite ? "This issue is locked." : null);
    }

    private static bool LoginEquals(string? left, string? right) =>
        !string.IsNullOrWhiteSpace(left) &&
        !string.IsNullOrWhiteSpace(right) &&
        string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);
}
