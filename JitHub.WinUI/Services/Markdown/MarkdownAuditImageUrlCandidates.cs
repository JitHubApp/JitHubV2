using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using MarkdownRenderer.Images;

namespace JitHub.Services.Markdown;

/// <summary>
/// The URL keys under which an offline README audit corpus stores an image.
/// The app's offline audit resolver and the host-side corpus acquisition both
/// use this one implementation, so a stored asset is always keyed exactly as
/// the resolver will look it up.
/// </summary>
public static class MarkdownAuditImageUrlCandidates
{
    /// <summary>
    /// Returns the normalized SHA-256 URL keys an authored image source can
    /// resolve through: the authored absolute URL, the base-relative URL, the
    /// GitHub source and raw URLs, and, for a relative same-repository asset,
    /// the raw URL at the corpus commit.
    /// </summary>
    public static IReadOnlyList<string> GetSourceUrlHashes(
        string source,
        Uri? baseUri,
        string? documentPath,
        MarkdownDocumentSource? documentSource,
        string expectedRepository,
        string? expectedCommit)
    {
        var hashes = new HashSet<string>(StringComparer.Ordinal);
        AddUrlHash(source, hashes);
        if (Uri.TryCreate(source, UriKind.RelativeOrAbsolute, out Uri? parsed) &&
            !parsed.IsAbsoluteUri && baseUri is not null &&
            Uri.TryCreate(baseUri, parsed, out Uri? based))
        {
            AddUrlHash(based.AbsoluteUri, hashes);
        }

        GitHubMarkdownImageReference reference;
        bool resolved = documentSource is not null
            ? GitHubMarkdownImageUrlResolver.TryResolve(source, documentSource, out reference)
            : GitHubMarkdownImageUrlResolver.TryResolve(
                source,
                baseUri,
                documentPath,
                out reference);
        if (resolved)
        {
            AddUrlHash(reference.SourceUri.AbsoluteUri, hashes);
            AddUrlHash(GitHubMarkdownImageUrlResolver.CreateRawUri(reference).AbsoluteUri, hashes);

            // A same-byte corpus is pinned to the README commit, while the
            // live document context may still name a branch such as "main".
            // Relative assets in that same repository are therefore also
            // looked up at the corpus commit. Do not rewrite absolute URLs or
            // GitHub's deliberate ../assets-branch convention: those identify
            // an explicitly different resource ref.
            if (Uri.TryCreate(source, UriKind.Relative, out _) &&
                documentSource is { HasRepositoryContext: true } &&
                string.Equals(reference.Ref, documentSource.Ref, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(
                    $"{reference.Owner}/{reference.Repository}",
                    expectedRepository,
                    StringComparison.OrdinalIgnoreCase) &&
                IsCommitSha(expectedCommit) &&
                !string.Equals(reference.Ref, expectedCommit, StringComparison.OrdinalIgnoreCase))
            {
                AddUrlHash(
                    GitHubMarkdownImageUrlResolver.CreateRawUri(reference with { Ref = expectedCommit!.ToLowerInvariant() }).AbsoluteUri,
                    hashes);
            }
        }
        return hashes.ToArray();
    }

    /// <summary>
    /// Returns the normalized URL key for one absolute http(s) URL, or null
    /// when the value cannot be a corpus key.
    /// </summary>
    public static string? HashUrl(string? source)
    {
        var hashes = new HashSet<string>(StringComparer.Ordinal);
        AddUrlHash(source, hashes);
        return hashes.SingleOrDefault();
    }

    private static bool IsCommitSha(string? value) =>
        value is { Length: 40 } && value.All(char.IsAsciiHexDigit);

    private static void AddUrlHash(string? source, HashSet<string> hashes)
    {
        if (!Uri.TryCreate(source, UriKind.Absolute, out Uri? uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
            uri.UserInfo.Length > 0)
        {
            return;
        }

        var builder = new UriBuilder(uri) { Fragment = string.Empty };
        string[] segments = builder.Path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (builder.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) &&
            segments.Length >= 5 &&
            (segments[2].Equals("blob", StringComparison.OrdinalIgnoreCase) ||
             segments[2].Equals("raw", StringComparison.OrdinalIgnoreCase)) &&
            builder.Query.TrimStart('?').Equals("raw=true", StringComparison.Ordinal))
        {
            builder.Query = string.Empty;
        }

        hashes.Add(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.Uri.AbsoluteUri)))
            .ToLowerInvariant());
    }
}
