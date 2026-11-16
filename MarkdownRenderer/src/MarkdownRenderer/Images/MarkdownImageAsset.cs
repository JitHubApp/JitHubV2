using System;

namespace MarkdownRenderer.Images;

/// <summary>
/// Image bytes supplied by a host-specific resolver.
/// </summary>
/// <param name="Bytes">The decoded image bytes.</param>
/// <param name="ContentType">Optional image MIME type, such as image/png or image/svg+xml.</param>
/// <param name="ResolvedUri">
/// Optional canonical URI used for diagnostics and cache identity. Its file extension does not
/// override the format identified by <paramref name="Bytes"/> and <paramref name="ContentType"/>.
/// </param>
/// <param name="CacheKey">
/// Optional host-partitioned in-process cache identity. Authenticated hosts must include the
/// account/session partition so private image resources can never cross account boundaries.
/// </param>
public sealed record MarkdownImageAsset(
    byte[] Bytes,
    string? ContentType = null,
    Uri? ResolvedUri = null,
    string? CacheKey = null);
