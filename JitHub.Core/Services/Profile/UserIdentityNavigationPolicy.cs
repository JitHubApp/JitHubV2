using System;
using System.Collections.Generic;

namespace JitHub.Services;

public readonly record struct UserIdentityPresentation(string DisplayName, string? AuthenticatedLogin);

public static class UserIdentityNavigationPolicy
{
    private static readonly HashSet<string> UnavailableIdentitySentinels = new(StringComparer.OrdinalIgnoreCase)
    {
        "[deleted]",
        "a user",
        "anonymous",
        "deleted",
        "deleted user",
        "ghost",
        "someone",
        "somebody",
        "unavailable",
        "unknown",
        "unknown user"
    };

    public static bool CanNavigate(string? login)
    {
        string value = login?.Trim() ?? string.Empty;
        return value.Length > 0 &&
            value.Length <= 39 &&
            !UnavailableIdentitySentinels.Contains(value) &&
            !value.EndsWith("[bot]", StringComparison.OrdinalIgnoreCase) &&
            value[0] != '-' &&
            value[^1] != '-' &&
            IsGitHubLogin(value);
    }

    public static string? GetRoutableLogin(string? login)
    {
        string value = login?.Trim() ?? string.Empty;
        return CanNavigate(value) ? value : null;
    }

    public static UserIdentityPresentation CreatePresentation(
        string? login,
        string? displayName,
        string fallbackDisplayName)
    {
        string routeCandidate = login?.Trim() ?? string.Empty;
        string display = routeCandidate.Length > 0
            ? routeCandidate
            : displayName?.Trim() ?? string.Empty;
        if (display.Length == 0)
            display = fallbackDisplayName;

        return new UserIdentityPresentation(display, GetRoutableLogin(routeCandidate));
    }

    private static bool IsGitHubLogin(string value)
    {
        foreach (char character in value)
        {
            if (!char.IsAsciiLetterOrDigit(character) && character != '-')
                return false;
        }

        return true;
    }
}
