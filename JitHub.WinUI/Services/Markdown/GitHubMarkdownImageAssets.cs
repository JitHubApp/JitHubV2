using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using JitHub.Markdown;
using JitHub.Markdown.Images;
using MarkdownRenderer.Images;

namespace JitHub.Services.Markdown;

/// <summary>
/// Stateless source classification and byte/asset conversions used by
/// <see cref="GitHubMarkdownImageResolver"/>.
/// </summary>
internal static class GitHubMarkdownImageAssets
{
    internal static bool IsSmallGitHubAvatar(string source)
    {
        if (!Uri.TryCreate(source, UriKind.Absolute, out Uri? uri) ||
            !uri.Host.Equals("avatars.githubusercontent.com", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        foreach (string parameter in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int separator = parameter.IndexOf('=');
            if (separator <= 0 ||
                !parameter[..separator].Equals("s", StringComparison.OrdinalIgnoreCase) ||
                !int.TryParse(parameter[(separator + 1)..], out int size))
            {
                continue;
            }

            return size is > 0 and <= 256;
        }

        return false;
    }

    internal static bool IsAbsoluteRawGitHubSource(
        string source,
        GitHubMarkdownImageReference reference) =>
        Uri.TryCreate(source, UriKind.Absolute, out Uri? sourceUri) &&
        sourceUri.Host.Equals("raw.githubusercontent.com", StringComparison.OrdinalIgnoreCase) &&
        reference.SourceUri.Host.Equals("raw.githubusercontent.com", StringComparison.OrdinalIgnoreCase);

    internal static Task<GitHubMarkdownImageCandidate?> ReadCachedMarkdownImageAsync(
        GitHubCachedImage? cachedImage,
        Uri resolvedUri,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (cachedImage?.Bytes is not { Length: > 0 } bytes)
        {
            return Task.FromResult<GitHubMarkdownImageCandidate?>(null);
        }

        return Task.FromResult<GitHubMarkdownImageCandidate?>(
            GitHubMarkdownImageCandidate.Resolved(new MarkdownImageAsset(
                bytes,
                cachedImage.ContentType,
                resolvedUri,
                CacheKey: cachedImage.FilePath), sourceOwner: null));
    }

    internal static Task<GitHubMarkdownImageCandidate?> ReadCachedMarkdownImageAsync(
        GitHubImageSourceLease? sourceLease,
        Uri resolvedUri,
        CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (sourceLease is null)
                return Task.FromResult<GitHubMarkdownImageCandidate?>(null);

            GitHubCachedImage cachedImage = sourceLease.Image;
            if (cachedImage.Bytes is not { Length: > 0 } bytes)
                return Task.FromResult<GitHubMarkdownImageCandidate?>(null);

            var candidate = GitHubMarkdownImageCandidate.Resolved(
                new MarkdownImageAsset(
                    bytes,
                    cachedImage.ContentType,
                    resolvedUri,
                    CacheKey: cachedImage.FilePath),
                sourceLease);
            sourceLease = null;
            return Task.FromResult<GitHubMarkdownImageCandidate?>(candidate);
        }
        finally
        {
            sourceLease?.Dispose();
        }
    }

    internal static Uri CreateRawFallbackUri(Uri sourceUri)
    {
        var builder = new UriBuilder(sourceUri);
        string existing = builder.Query.TrimStart('?');
        builder.Query = string.IsNullOrEmpty(existing)
            ? "jithub-raw-fallback=1"
            : existing + "&jithub-raw-fallback=1";
        return builder.Uri;
    }

    internal static string? GuessImageContentType(string path)
    {
        string extension = System.IO.Path.GetExtension(path);
        return extension.ToLowerInvariant() switch
        {
            ".svg" => "image/svg+xml",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".bmp" => "image/bmp",
            ".webp" => "image/webp",
            ".avif" => "image/avif",
            ".ico" => "image/x-icon",
            ".tif" or ".tiff" => "image/tiff",
            _ => null,
        };
    }

    internal static byte[] DecodeGitHubContent(string? content, string? encoding)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return Array.Empty<byte>();
        }

        if (string.Equals(encoding, "base64", StringComparison.OrdinalIgnoreCase))
        {
            long encodedCharacters = 0;
            foreach (char character in content)
            {
                if (character is not (' ' or '\t' or '\r' or '\n'))
                    encodedCharacters++;
            }
            if ((encodedCharacters / 4) * 3 > GitHubImageService.MaxImageBytes + 2L)
                throw new InvalidDataException("Repository image exceeds the bounded source limit.");
            // FromBase64String already ignores Base64 whitespace. Avoid an
            // additional full-length normalized string alongside the API
            // response and decoded image bytes.
            byte[] decoded = Convert.FromBase64String(content);
            if (decoded.Length > GitHubImageService.MaxImageBytes)
                throw new InvalidDataException("Repository image exceeds the bounded source limit.");
            return decoded;
        }

        if (Encoding.UTF8.GetByteCount(content) > GitHubImageService.MaxImageBytes)
            throw new InvalidDataException("Repository image exceeds the bounded source limit.");
        return Encoding.UTF8.GetBytes(content);
    }
}
