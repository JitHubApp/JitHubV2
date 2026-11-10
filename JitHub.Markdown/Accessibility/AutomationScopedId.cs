using System;
using System.Security.Cryptography;
using System.Text;

namespace JitHub.Markdown.Accessibility;

/// <summary>
/// Builds UI Automation ids that are readable and stable for one scope: a
/// sanitized prefix and scope plus a short digest of the full scope, so two
/// scopes that sanitize alike still get different ids.
/// </summary>
public static class AutomationScopedId
{
    /// <summary>Creates the automation id for <paramref name="prefix"/> within <paramref name="scope"/>.</summary>
    /// <param name="prefix">The element kind, such as <c>MarkdownForm</c>.</param>
    /// <param name="scope">The owning item's identity, or <see langword="null"/> when it has none.</param>
    /// <param name="suffix">The element's role within the scope, if any.</param>
    /// <returns>The automation id.</returns>
    public static string Create(string prefix, string? scope, string? suffix = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);

        string normalizedScope = string.IsNullOrWhiteSpace(scope) ? "unknown" : scope.Trim();
        string readableScope = Sanitize(normalizedScope, 36);
        string digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedScope)))[..12];
        string id = $"{Sanitize(prefix, 36)}_{readableScope}_{digest}";
        return string.IsNullOrWhiteSpace(suffix)
            ? id
            : $"{id}_{Sanitize(suffix, 28)}";
    }

    private static string Sanitize(string value, int maximumLength)
    {
        StringBuilder result = new(Math.Min(value.Length, maximumLength));
        foreach (char character in value)
        {
            if (result.Length == maximumLength)
            {
                break;
            }

            if (char.IsLetterOrDigit(character))
            {
                result.Append(character);
            }
            else if (result.Length > 0 && result[^1] != '_')
            {
                result.Append('_');
            }
        }

        return result.Length == 0 ? "item" : result.ToString().TrimEnd('_');
    }
}
