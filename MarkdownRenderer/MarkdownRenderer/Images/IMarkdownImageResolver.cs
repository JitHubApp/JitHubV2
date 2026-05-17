using System;
using System.Threading;
using System.Threading.Tasks;

namespace MarkdownRenderer.Images;

/// <summary>
/// Context supplied to markdown image resolvers.
/// </summary>
/// <param name="BaseUri">Optional document base URI used to resolve relative image sources.</param>
/// <param name="DocumentPath">Optional source document path, when the host can provide one.</param>
public sealed record MarkdownImageResolveContext(Uri? BaseUri, string? DocumentPath = null);

/// <summary>
/// Image bytes supplied by a host-specific resolver.
/// </summary>
/// <param name="Bytes">The decoded image bytes.</param>
/// <param name="ContentType">Optional image MIME type, such as image/png or image/svg+xml.</param>
/// <param name="ResolvedUri">Optional canonical URI used for diagnostics and cache identity.</param>
public sealed record MarkdownImageAsset(byte[] Bytes, string? ContentType = null, Uri? ResolvedUri = null);

/// <summary>
/// Resolves markdown image sources that need host-specific behavior, such as
/// authenticated repository assets. Return null to let the renderer use its
/// built-in public URI/data URI loading.
/// </summary>
public interface IMarkdownImageResolver
{
    /// <summary>
    /// Resolves an image source to bytes, or null when the resolver does not own it.
    /// </summary>
    ValueTask<MarkdownImageAsset?> ResolveAsync(
        string source,
        MarkdownImageResolveContext context,
        CancellationToken cancellationToken);
}
