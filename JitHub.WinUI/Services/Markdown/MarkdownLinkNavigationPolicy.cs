using System;
using System.Collections.Generic;
using System.Linq;
using MarkdownRenderer.Images;

namespace JitHub.Services.Markdown;

public static class MarkdownLinkNavigationPolicy
{
    private static readonly HashSet<string> ReservedGitHubRoutes = new(StringComparer.OrdinalIgnoreCase)
    {
        "about", "account", "apps", "codespaces", "collections", "contact",
        "customer-stories", "enterprise", "events", "explore", "features", "gist",
        "gists", "issues", "join", "login", "logout", "marketplace", "new",
        "notifications", "orgs", "organizations", "pricing", "pulls", "readme",
        "search", "security", "settings", "site", "sponsors", "stars", "topics",
        "trending",
    };

    public static bool TryResolveLaunchUri(string? value, Uri baseUri, out Uri? uri)
        => TryResolveLaunchUri(value, baseUri, documentSource: null, out uri, out _);

    public static bool TryResolveLaunchUri(
        string? value,
        Uri baseUri,
        MarkdownDocumentSource? documentSource,
        out Uri? uri,
        out bool mayNavigateInternally)
    {
        uri = null;
        mayNavigateInternally = false;
        if (string.IsNullOrWhiteSpace(value) || value.StartsWith("#", StringComparison.Ordinal))
            return false;

        bool wasAbsolute = Uri.TryCreate(value, UriKind.Absolute, out uri);
        if (!wasAbsolute &&
            !TryResolveRepositoryRelativeUri(value, documentSource, out uri) &&
            !Uri.TryCreate(baseUri, value, out uri))
        {
            uri = null;
            return false;
        }

        if (!IsAllowedLaunchUri(uri))
        {
            uri = null;
            return false;
        }

        // Absolute GitHub links and links resolved from explicit repository context may
        // use in-app routing. Relative links resolved against a generic fallback never do.
        mayNavigateInternally = wasAbsolute || documentSource?.HasRepositoryContext == true;
        return true;
    }

    private static bool TryResolveRepositoryRelativeUri(
        string value,
        MarkdownDocumentSource? source,
        out Uri? uri)
    {
        uri = null;
        if (source?.HasRepositoryContext != true)
        {
            return false;
        }

        string normalized = NormalizeRepositoryPath(value.StartsWith("/", StringComparison.Ordinal)
            ? value.TrimStart('/')
            : JoinRepositoryPath(GetDirectoryName(source.Path!), value));
        if (normalized.Length == 0 || normalized.StartsWith("../", StringComparison.Ordinal))
        {
            return false;
        }

        string escapedRef = EscapePath(source.Ref!);
        string escapedPath = EscapePath(normalized);
        uri = new Uri(
            $"https://github.com/{Uri.EscapeDataString(source.Owner!)}/{Uri.EscapeDataString(source.Repository!)}/blob/{escapedRef}/{escapedPath}",
            UriKind.Absolute);
        return true;
    }

    public static bool IsAllowedLaunchUri(Uri? uri) =>
        uri is not null &&
        (string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
         string.Equals(uri.Scheme, Uri.UriSchemeMailto, StringComparison.OrdinalIgnoreCase));

    public static MarkdownGitHubRoute ClassifyGitHubRoute(Uri? uri)
    {
        if (uri is null ||
            !uri.IsAbsoluteUri ||
            !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
        {
            return MarkdownGitHubRoute.NotInternal;
        }

        string[] segments = uri.AbsolutePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(Uri.UnescapeDataString)
            .ToArray();
        if (segments.Length == 1 && IsGitHubOwner(segments[0]))
        {
            return new MarkdownGitHubRoute(MarkdownGitHubRouteKind.User, segments[0], null);
        }

        if (segments.Length == 2 &&
            IsGitHubOwner(segments[0]) &&
            IsRepositoryName(segments[1]))
        {
            return new MarkdownGitHubRoute(MarkdownGitHubRouteKind.Repository, segments[0], segments[1]);
        }

        if (segments.Length == 4 &&
            IsGitHubOwner(segments[0]) &&
            IsRepositoryName(segments[1]) &&
            int.TryParse(segments[3], out int number) &&
            number > 0)
        {
            MarkdownGitHubRouteKind kind = segments[2].Equals("issues", StringComparison.OrdinalIgnoreCase)
                ? MarkdownGitHubRouteKind.Issue
                : segments[2].Equals("pull", StringComparison.OrdinalIgnoreCase)
                    ? MarkdownGitHubRouteKind.PullRequest
                    : MarkdownGitHubRouteKind.ExternalGitHub;
            if (kind is MarkdownGitHubRouteKind.Issue or MarkdownGitHubRouteKind.PullRequest)
            {
                return new MarkdownGitHubRoute(kind, segments[0], segments[1], number);
            }
        }

        return new MarkdownGitHubRoute(MarkdownGitHubRouteKind.ExternalGitHub, null, null);
    }

    private static bool IsGitHubOwner(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 39 || ReservedGitHubRoutes.Contains(value))
        {
            return false;
        }

        return value[0] != '-' && value[^1] != '-' &&
            value.All(static character => char.IsAsciiLetterOrDigit(character) || character == '-');
    }

    private static bool IsRepositoryName(string value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= 100 &&
        value is not "." and not ".." &&
        value.All(static character =>
            char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.');

    private static string JoinRepositoryPath(string left, string right) =>
        string.IsNullOrWhiteSpace(left) ? right : $"{left.TrimEnd('/')}/{right}";

    private static string GetDirectoryName(string path)
    {
        string normalized = NormalizeRepositoryPath(path);
        int separator = normalized.LastIndexOf('/');
        return separator < 0 ? string.Empty : normalized[..separator];
    }

    private static string NormalizeRepositoryPath(string? path)
    {
        string[] parts = (path ?? string.Empty)
            .Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        List<string> stack = [];
        foreach (string part in parts)
        {
            if (part == ".")
            {
                continue;
            }
            if (part == "..")
            {
                if (stack.Count == 0)
                {
                    return "../";
                }
                stack.RemoveAt(stack.Count - 1);
                continue;
            }
            stack.Add(part);
        }
        return string.Join('/', stack);
    }

    private static string EscapePath(string path) => string.Join('/',
        NormalizeRepositoryPath(path)
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(Uri.EscapeDataString));
}

public enum MarkdownGitHubRouteKind
{
    NotInternal,
    User,
    Repository,
    Issue,
    PullRequest,
    ExternalGitHub,
}

public readonly record struct MarkdownGitHubRoute(
    MarkdownGitHubRouteKind Kind,
    string? Owner,
    string? Repository,
    int? Number = null)
{
    public static MarkdownGitHubRoute NotInternal =>
        new(MarkdownGitHubRouteKind.NotInternal, null, null);
}
