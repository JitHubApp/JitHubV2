using System;

namespace JitHub.Models.NavArgs;

internal static class RepositoryBranchNavigationPolicy
{
    public static string ResolveTargetBranch(
        bool followsDefaultBranch,
        string? requestedBranch,
        string? currentDefaultBranch) =>
        followsDefaultBranch
            ? currentDefaultBranch ?? string.Empty
            : requestedBranch ?? string.Empty;

    public static string? ResolveRouteIdentityBranch(
        bool followsDefaultBranch,
        string? requestedBranch,
        string? repositoryDefaultBranch) =>
        followsDefaultBranch
            ? null
            : requestedBranch ?? repositoryDefaultBranch;

    public static bool ShouldRealign(
        bool followsDefaultBranch,
        string? currentBranch,
        string? resolvedBranch) =>
        followsDefaultBranch &&
        !string.IsNullOrWhiteSpace(resolvedBranch) &&
        !string.Equals(currentBranch, resolvedBranch, StringComparison.Ordinal);

    public static string? ResolveAlignmentBranch(
        bool followsDefaultBranch,
        string? selectedBranch,
        string? refreshedDefaultBranch) =>
        followsDefaultBranch
            ? refreshedDefaultBranch
            : selectedBranch ?? refreshedDefaultBranch;
}
