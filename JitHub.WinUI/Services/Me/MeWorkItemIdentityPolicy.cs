using System;

namespace JitHub.Services;

public static class MeWorkItemIdentityPolicy
{
    public static string? ResolveLogin(
        string? authenticatedLogin,
        string? activeLogin,
        string? activePartition,
        string currentPartition)
    {
        if (!string.IsNullOrWhiteSpace(authenticatedLogin))
        {
            return authenticatedLogin;
        }

        return string.Equals(activePartition, currentPartition, StringComparison.Ordinal) &&
               !string.IsNullOrWhiteSpace(activeLogin)
            ? activeLogin
            : null;
    }
}

public static class MeWorkItemRequestGuard
{
    public static bool IsCurrent(
        int expectedSelectionVersion,
        int currentSelectionVersion,
        int expectedSectionVersion,
        int currentSectionVersion,
        bool isCancellationRequested,
        PullRequestWorkspaceSection expectedSection,
        PullRequestWorkspaceSection currentSection,
        string expectedItemKey,
        string? currentItemKey) =>
        expectedSelectionVersion == currentSelectionVersion &&
        expectedSectionVersion == currentSectionVersion &&
        !isCancellationRequested &&
        expectedSection == currentSection &&
        string.Equals(expectedItemKey, currentItemKey, StringComparison.Ordinal);
}
