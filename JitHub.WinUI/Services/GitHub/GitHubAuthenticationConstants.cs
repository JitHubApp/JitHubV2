namespace JitHub.Services;

public static class GitHubAuthenticationConstants
{
    public const string PublicAccessToken = "__JITHUB_PUBLIC__";

    public static bool IsPublicAccessToken(string? token) =>
        string.Equals(token, PublicAccessToken, System.StringComparison.Ordinal);
}
