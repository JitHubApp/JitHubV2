using System;
using JitHub.Models.GitHub;

namespace JitHub.Services;

public sealed record PullRequestCapabilities(
    bool CanEdit,
    bool CanManageMetadata,
    bool CanChangeState,
    bool CanComment,
    bool CanReact,
    bool CanSubmitReviewComment,
    bool CanApprove,
    bool CanRequestChanges,
    bool CanMerge,
    bool CanMergeCommit,
    bool CanSquashMerge,
    bool CanRebaseMerge,
    string MergeUnavailableReason);

public static class PullRequestPermissionPolicy
{
    public static PullRequestCapabilities Evaluate(
        GitHubRepository repository,
        GitHubPullRequest pullRequest,
        GitHubIssue? issue,
        string? authenticatedLogin,
        bool isPublicPreview)
    {
        if (isPublicPreview || string.IsNullOrWhiteSpace(authenticatedLogin) || repository.Archived)
        {
            return Disabled(repository.Archived
                ? "This repository is archived."
                : "Sign in with a GitHub account to manage this pull request.");
        }

        GitHubRepositoryPermissions? permissions = repository.Permissions;
        bool canRead = permissions?.Pull == true || permissions?.Push == true ||
            permissions?.Triage == true || permissions?.Maintain == true || permissions?.Admin == true;
        bool canTriage = permissions?.Triage == true || permissions?.Push == true ||
            permissions?.Maintain == true || permissions?.Admin == true;
        bool canPush = permissions?.Push == true || permissions?.Maintain == true || permissions?.Admin == true;
        bool isAuthor = string.Equals(
            pullRequest.User.Login,
            authenticatedLogin,
            StringComparison.OrdinalIgnoreCase);
        bool isLocked = issue?.Locked == true;

        bool canEdit = isAuthor || canTriage;
        bool canChangeState = !pullRequest.Merged && (isAuthor || canTriage);
        bool canComment = canRead && !isLocked;
        bool canReact = canRead && !isLocked;
        bool canReview = canRead && !isLocked &&
            string.Equals(pullRequest.State, "open", StringComparison.OrdinalIgnoreCase) &&
            !pullRequest.Merged;
        bool canSubmitReviewDecision = canReview && !isAuthor;

        bool mergeCommitAllowed = repository.AllowMergeCommit == true;
        bool squashAllowed = repository.AllowSquashMerge == true;
        bool rebaseAllowed = repository.AllowRebaseMerge == true;
        bool hasAllowedMethod = mergeCommitAllowed || squashAllowed || rebaseAllowed;
        string mergeState = pullRequest.MergeableState?.Trim().ToLowerInvariant() ?? string.Empty;
        bool mergeStateAllows = mergeState is "clean" or "unstable" or "has_hooks" or "behind";
        bool baseMergeEligible = canPush &&
            string.Equals(pullRequest.State, "open", StringComparison.OrdinalIgnoreCase) &&
            !pullRequest.Merged &&
            !pullRequest.Draft &&
            pullRequest.Mergeable == true &&
            mergeStateAllows &&
            hasAllowedMethod;

        string mergeUnavailableReason = baseMergeEligible
            ? string.Empty
            : CreateMergeUnavailableReason(
                pullRequest,
                canPush,
                hasAllowedMethod,
                mergeState);

        return new PullRequestCapabilities(
            canEdit,
            canTriage,
            canChangeState,
            canComment,
            canReact,
            canReview,
            canSubmitReviewDecision,
            canSubmitReviewDecision,
            baseMergeEligible,
            baseMergeEligible && mergeCommitAllowed,
            baseMergeEligible && squashAllowed,
            baseMergeEligible && rebaseAllowed,
            mergeUnavailableReason);
    }

    private static PullRequestCapabilities Disabled(string reason) =>
        new(false, false, false, false, false, false, false, false, false, false, false, false, reason);

    private static string CreateMergeUnavailableReason(
        GitHubPullRequest pullRequest,
        bool canPush,
        bool hasAllowedMethod,
        string mergeState)
    {
        if (pullRequest.Merged)
        {
            return "This pull request is already merged.";
        }

        if (!string.Equals(pullRequest.State, "open", StringComparison.OrdinalIgnoreCase))
        {
            return "Only open pull requests can be merged.";
        }

        if (pullRequest.Draft)
        {
            return "Mark this pull request ready for review before merging.";
        }

        if (!canPush)
        {
            return "Write permission on the base repository is required to merge.";
        }

        if (!hasAllowedMethod)
        {
            return "The repository has no supported merge method enabled.";
        }

        if (pullRequest.Mergeable is null || string.IsNullOrWhiteSpace(mergeState))
        {
            return "GitHub is still calculating mergeability.";
        }

        if (pullRequest.Mergeable == false || mergeState == "dirty")
        {
            return "Resolve merge conflicts before merging.";
        }

        if (mergeState == "blocked")
        {
            return "Branch protection requirements are not yet satisfied.";
        }

        return "GitHub does not currently allow this pull request to be merged.";
    }
}
