using System;
using JitHub.Models.GitHub;

namespace JitHub.Services;

public readonly record struct PullRequestIdentityPresentation(
    string DisplayName,
    string? ProfileLogin,
    string AvatarUrl,
    string AutomationInstanceId)
{
    public bool IsProfileAvailable => ProfileLogin is not null;
}

public static class PullRequestIdentityProjection
{
    public static PullRequestIdentityPresentation Create(
        GitHubPullRequestReview review,
        string fallbackDisplayName,
        string automationPrefix,
        string? deterministicContext = null)
    {
        ArgumentNullException.ThrowIfNull(review);
        string stableScope = PullRequestReviewAutomationIdentity.CreateScope(
            automationPrefix,
            review.Id,
            review.NodeId,
            reviewId: null,
            position: null,
            originalPosition: null,
            createdAt: review.SubmittedAt ?? DateTimeOffset.MinValue,
            deterministicContext);
        return Create(review.User, fallbackDisplayName, stableScope);
    }

    public static PullRequestIdentityPresentation Create(
        GitHubPullRequestReviewComment comment,
        string fallbackDisplayName,
        string automationPrefix,
        string? deterministicContext = null)
    {
        ArgumentNullException.ThrowIfNull(comment);
        string stableScope = PullRequestReviewAutomationIdentity.CreateScope(
            automationPrefix,
            comment.Id,
            comment.NodeId,
            comment.PullRequestReviewId,
            comment.Position,
            comment.OriginalPosition,
            comment.CreatedAt,
            deterministicContext);
        return Create(comment.User, fallbackDisplayName, stableScope);
    }

    public static PullRequestIdentityPresentation Create(
        GitHubActor? actor,
        string fallbackDisplayName,
        string stableScope)
    {
        UserIdentityPresentation identity = UserIdentityNavigationPolicy.CreatePresentation(
            actor?.Login,
            displayName: null,
            fallbackDisplayName);

        return new PullRequestIdentityPresentation(
            identity.DisplayName,
            identity.AuthenticatedLogin,
            actor?.AvatarUrl ?? string.Empty,
            NormalizeStableScope(stableScope));
    }

    private static string NormalizeStableScope(string? stableScope)
    {
        string source = string.IsNullOrWhiteSpace(stableScope) ? "unknown" : stableScope.Trim();
        char[] result = new char[source.Length];
        for (int index = 0; index < source.Length; index++)
        {
            char character = source[index];
            result[index] = char.IsLetterOrDigit(character) || character is '-' or '_'
                ? character
                : '_';
        }

        return new string(result);
    }
}
