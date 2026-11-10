using System;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;

namespace JitHub.Services;

public static class GitHubQueryKeys
{
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
        string typeIdentity = resultType.FullName ?? resultType.Name;
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{mediaType}\n{typeIdentity}"));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
