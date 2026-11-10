using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace JitHub.Core.Services.GitHub;

public static class GitHubQueryKeys
{
    // A representation key hashes the result type's full name, and cached
    // responses are stored under that key. The result types moved out of
    // these persisted namespaces when the source tree was reorganized; naming
    // them by their persisted namespace keeps every stored key, and so every
    // cached response, valid.
    private static readonly IReadOnlyDictionary<string, string> PersistedTypeNamespaces =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["JitHub.Core.Models.GitHub"] = "JitHub.Models.GitHub",
            ["JitHub.Core.Models.GitHub.Activities"] = "JitHub.Models.GitHub",
            ["JitHub.Core.Models.GitHub.Commits"] = "JitHub.Models.GitHub",
            ["JitHub.Core.Models.GitHub.Gists"] = "JitHub.Models.GitHub",
            ["JitHub.Core.Models.GitHub.Issues"] = "JitHub.Models.GitHub",
            ["JitHub.Core.Models.GitHub.Notifications"] = "JitHub.Models.GitHub",
            ["JitHub.Core.Models.GitHub.PullRequests"] = "JitHub.Models.GitHub",
            ["JitHub.Core.Models.GitHub.Repositories"] = "JitHub.Models.GitHub",
            ["JitHub.Core.Models.GitHub.Users"] = "JitHub.Models.GitHub",
            ["JitHub.Core.Services.Accounts"] = "JitHub.Services",
            ["JitHub.Core.Services.CodeViewer"] = "JitHub.Services.CodeViewer",
            ["JitHub.Core.Services.Dashboard"] = "JitHub.Services",
            ["JitHub.Core.Services.Gists"] = "JitHub.Services",
            ["JitHub.Core.Services.Profile"] = "JitHub.Services",
            ["JitHub.Core.Services.Repositories"] = "JitHub.Services",
            ["JitHub.Core.Services.Stars"] = "JitHub.Services",
            ["JitHub.Core.Services.Telemetry"] = "JitHub.Services",
        };

    // A namespace-qualified type name inside a full name. Generic arguments,
    // nested types and assembly names are delimited by other characters.
    private static readonly Regex QualifiedTypeName = new(@"[A-Za-z_][\w.]*", RegexOptions.CultureInvariant);

    public static string Create(string userId, HttpMethod method, string relativePath) =>
        $"{userId}:{method.Method}:{NormalizeRelativePath(relativePath)}";

    public static string Create(
        string userId,
        HttpMethod method,
        string relativePath,
        string? acceptMediaType,
        Type resultType) =>
        $"{Create(userId, method, relativePath)}:representation:{CreateRepresentationIdentity(acceptMediaType, resultType)}";

    public static string CreateDedupeKey(
        string userId,
        HttpMethod method,
        string relativePath,
        string? acceptMediaType,
        Type resultType) =>
        Create(userId, method, relativePath, acceptMediaType, resultType);

    public static string NormalizeRelativePath(string relativePath)
    {
        string trimmed = relativePath.TrimStart('/');
        int queryIndex = trimmed.IndexOf('?', StringComparison.Ordinal);
        if (queryIndex < 0)
        {
            return trimmed;
        }

        string path = trimmed[..queryIndex];
        string query = trimmed[(queryIndex + 1)..];
        string normalizedQuery = string.Join(
            "&",
            query.Split('&', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .OrderBy(static part => part, StringComparer.Ordinal));
        return string.IsNullOrWhiteSpace(normalizedQuery)
            ? path
            : $"{path}?{normalizedQuery}";
    }

    private static string CreateRepresentationIdentity(string? acceptMediaType, Type resultType)
    {
        ArgumentNullException.ThrowIfNull(resultType);
        string mediaType = string.IsNullOrWhiteSpace(acceptMediaType)
            ? "application/vnd.github+json"
            : acceptMediaType.Trim().ToLowerInvariant();
        string typeIdentity = GetPersistedTypeIdentity(resultType);
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{mediaType}\n{typeIdentity}"));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// The result type's full name, with each moved namespace replaced by the
    /// namespace its persisted cache keys were created with.
    /// </summary>
    internal static string GetPersistedTypeIdentity(Type resultType) =>
        QualifiedTypeName.Replace(resultType.FullName ?? resultType.Name, static match =>
        {
            int separator = match.Value.LastIndexOf('.');
            return separator > 0 &&
                PersistedTypeNamespaces.TryGetValue(match.Value[..separator], out string? persisted)
                    ? persisted + match.Value[separator..]
                    : match.Value;
        });
}
