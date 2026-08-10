using System;

namespace JitHub.Services;

public static class GitHubAccountPartition
{
    public static string Require(string? accountPartition, string parameterName = "userId")
    {
        string normalized = accountPartition?.Trim() ?? string.Empty;
        if (normalized.Length == 0 ||
            normalized.Equals("current", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("anonymous", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "A stable authenticated account partition is required before using the GitHub query cache.",
                parameterName);
        }

        return normalized;
    }

    public static string Resolve(string accessToken, string? accountPartition, string parameterName = "userId") =>
        GitHubAuthenticationConstants.IsPublicAccessToken(accessToken)
            ? "public"
            : Require(accountPartition, parameterName);
}
