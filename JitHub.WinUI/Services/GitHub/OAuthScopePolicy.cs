using System;
using System.Collections.Generic;
using System.Linq;

namespace JitHub.Services;

internal static class OAuthScopePolicy
{
    private static readonly string[] BaselineScopes = ["user", "repo", "notifications"];

    public static IReadOnlyList<string> BuildRequestedScopes(
        IReadOnlyCollection<string>? additionalScopes = null)
    {
        List<string> scopes = [.. BaselineScopes];
        if (additionalScopes is null)
        {
            return scopes;
        }

        foreach (string scope in additionalScopes)
        {
            string normalizedScope = Normalize(scope, nameof(additionalScopes));
            if (normalizedScope.Length > 0 && !scopes.Contains(normalizedScope, StringComparer.Ordinal))
            {
                scopes.Add(normalizedScope);
            }
        }

        return scopes;
    }

    public static bool HasAll(
        IReadOnlySet<string> grantedScopes,
        IEnumerable<string> requiredScopes)
    {
        ArgumentNullException.ThrowIfNull(grantedScopes);
        ArgumentNullException.ThrowIfNull(requiredScopes);

        foreach (string scope in requiredScopes)
        {
            string normalizedScope = Normalize(scope, nameof(requiredScopes));
            if (normalizedScope.Length > 0 && !grantedScopes.Contains(normalizedScope))
            {
                return false;
            }
        }

        return true;
    }

    private static string Normalize(string? scope, string parameterName)
    {
        string normalizedScope = scope?.Trim() ?? string.Empty;
        if (normalizedScope.Any(character =>
                !char.IsLetterOrDigit(character) && character is not '_' and not ':' and not '-'))
        {
            throw new ArgumentException($"Invalid OAuth scope '{scope}'.", parameterName);
        }

        return normalizedScope;
    }
}
