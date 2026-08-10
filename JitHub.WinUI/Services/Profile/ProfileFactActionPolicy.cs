using System;
using System.Linq;
using JitHub.Services.Markdown;

namespace JitHub.Services;

public static class ProfileFactActionPolicy
{
    public static ProfileFactAction? CreateWebsite(string? value)
    {
        string text = Normalize(value);
        if (text.Length == 0)
        {
            return null;
        }

        string candidate = text.Contains("://", StringComparison.Ordinal)
            ? text
            : $"https://{text}";
        return Create(candidate, text, "Open website", "Copy website");
    }

    public static ProfileFactAction? CreateEmail(string? value)
    {
        string email = Normalize(value);
        if (email.Length == 0 ||
            email.Any(static character => char.IsWhiteSpace(character) || character is '\r' or '\n') ||
            email.Count(static character => character == '@') != 1)
        {
            return null;
        }

        return Create($"mailto:{email}", email, "Send email", "Copy email address");
    }

    public static ProfileFactAction? CreateTwitter(string? value)
    {
        string username = Normalize(value).TrimStart('@');
        if (username.Length is < 1 or > 15 ||
            username.Any(static character => !char.IsAsciiLetterOrDigit(character) && character != '_'))
        {
            return null;
        }

        return Create(
            $"https://x.com/{Uri.EscapeDataString(username)}",
            $"@{username}",
            "Open Twitter profile",
            "Copy Twitter username");
    }

    private static ProfileFactAction? Create(
        string uriText,
        string copyValue,
        string openLabel,
        string copyLabel)
    {
        return Uri.TryCreate(uriText, UriKind.Absolute, out Uri? uri) &&
            MarkdownLinkNavigationPolicy.IsAllowedLaunchUri(uri)
                ? new ProfileFactAction(uri, copyValue, openLabel, copyLabel)
                : null;
    }

    private static string Normalize(string? value) => value?.Trim() ?? string.Empty;
}

public sealed record ProfileFactAction(
    Uri LaunchUri,
    string CopyValue,
    string OpenLabel,
    string CopyLabel);
