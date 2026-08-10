using System;
using System.Threading;
using System.Threading.Tasks;

namespace MarkdownRenderer.Images;

/// <summary>
/// Context supplied to markdown image resolvers.
/// </summary>
/// <param name="BaseUri">Optional document base URI used to resolve relative image sources.</param>
/// <param name="DocumentPath">Optional source document path, when the host can provide one.</param>
/// <param name="AllowThirdPartyRemoteImages">True only after the host or user explicitly permits third-party images.</param>
/// <param name="DocumentSource">Canonical source identity for repository-backed documents.</param>
public sealed record MarkdownImageResolveContext(
    Uri? BaseUri,
    string? DocumentPath = null,
    bool AllowThirdPartyRemoteImages = false,
    MarkdownDocumentSource? DocumentSource = null);

/// <summary>
/// Stable identity and repository context for one logical Markdown document.
/// The document ID must change when a recycled host displays a different issue,
/// comment, pull request, commit, README, or editor preview.
/// </summary>
public sealed record MarkdownDocumentSource(
    string DocumentId,
    string? Owner = null,
    string? Repository = null,
    string? Ref = null,
    string? Path = null)
{
    /// <summary>Returns true only when complete repository-relative image context is available.</summary>
    public bool HasRepositoryContext =>
        !string.IsNullOrWhiteSpace(Owner) &&
        !string.IsNullOrWhiteSpace(Repository) &&
        !string.IsNullOrWhiteSpace(Ref) &&
        !string.IsNullOrWhiteSpace(Path);

    /// <summary>Returns a normalized identity suitable for per-document consent.</summary>
    public string GetConsentIdentity() => string.IsNullOrWhiteSpace(DocumentId)
        ? string.Empty
        : DocumentId.Trim();
}

/// <summary>
/// Explains why an image resolver deliberately declined to expose image bytes.
/// Hosts can use this value to offer an explicit, privacy-preserving recovery action.
/// </summary>
public enum MarkdownImageUnavailableReason
{
    /// <summary>No unavailable state was reported.</summary>
    None,
    /// <summary>The source could not be resolved or decoded.</summary>
    Unavailable,
    /// <summary>The host's remote-content policy blocked the source.</summary>
    RemoteContentBlocked,
    /// <summary>The source used insecure HTTP.</summary>
    InsecureRemoteContent,
    /// <summary>The host is offline.</summary>
    Offline,
    /// <summary>Automatic loading was suppressed on a metered connection.</summary>
    MeteredConnection,
}

/// <summary>
/// Image bytes supplied by a host-specific resolver.
/// </summary>
/// <param name="Bytes">The decoded image bytes.</param>
/// <param name="ContentType">Optional image MIME type, such as image/png or image/svg+xml.</param>
/// <param name="ResolvedUri">Optional canonical URI used for diagnostics and cache identity.</param>
/// <param name="CacheKey">
/// Optional host-partitioned in-process cache identity. Authenticated hosts must include the
/// account/session partition so private image resources can never cross account boundaries.
/// </param>
public sealed record MarkdownImageAsset(
    byte[] Bytes,
    string? ContentType = null,
    Uri? ResolvedUri = null,
    string? CacheKey = null);

/// <summary>
/// Host resolution result. A handled result with no asset deliberately prevents the renderer
/// from bypassing host authentication, caching, or content policy through its default loader.
/// </summary>
public readonly record struct MarkdownImageResolution(
    bool IsHandled,
    MarkdownImageAsset? Asset,
    MarkdownImageUnavailableReason UnavailableReason = MarkdownImageUnavailableReason.None)
{
    /// <summary>Allows the renderer to use its built-in source loader.</summary>
    public static MarkdownImageResolution NotHandled => new(false, null);

    /// <summary>Prevents fallback because the host owns the source but could not provide it.</summary>
    public static MarkdownImageResolution Unavailable =>
        new(true, null, MarkdownImageUnavailableReason.Unavailable);

    /// <summary>Prevents fallback and records why the host blocked the source.</summary>
    public static MarkdownImageResolution Blocked(MarkdownImageUnavailableReason reason) =>
        new(true, null, reason == MarkdownImageUnavailableReason.None
            ? MarkdownImageUnavailableReason.Unavailable
            : reason);

    /// <summary>Returns bytes supplied by the host resolver.</summary>
    public static MarkdownImageResolution Resolved(MarkdownImageAsset asset) =>
        new(true, asset ?? throw new ArgumentNullException(nameof(asset)));
}

/// <summary>
/// Resolves markdown image sources that need host-specific behavior, such as
/// authenticated repository assets. Return <see cref="MarkdownImageResolution.NotHandled"/>
/// only when the renderer may use its built-in URI loader.
/// </summary>
public interface IMarkdownImageResolver
{
    /// <summary>
    /// Resolves an image source and explicitly reports whether the host owns it.
    /// </summary>
    ValueTask<MarkdownImageResolution> ResolveAsync(
        string source,
        MarkdownImageResolveContext context,
        CancellationToken cancellationToken);
}
