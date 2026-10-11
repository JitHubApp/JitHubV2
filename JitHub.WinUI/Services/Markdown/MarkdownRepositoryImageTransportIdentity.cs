using System;
using System.Security.Cryptography;
using System.Text;
using MarkdownRenderer.Images;

namespace JitHub.Services.Markdown;

/// <summary>
/// Hash-only identity for correlating one repository-image transport with its
/// Markdown resolution during an explicit audit. Never add raw request material
/// to this record because it is serialized into audit evidence.
/// </summary>
internal sealed record MarkdownRepositoryImageTransportIdentity(
    string SourceSha256,
    string SourceKind,
    string DocumentContextKind,
    string? DocumentPathSha256,
    string? DocumentRefIdentitySha256,
    string ResourceHostRoute,
    string ResourcePathSha256)
{
    private const int MaximumSourceLength = 4096;
    private const int MaximumDocumentInputLength = 2048;
    private const int MaximumResourcePathLength = 4096;
    private const string GitHubRawHostRoute = "github-raw";

    /// <summary>
    /// Creates a serializable identity only for bounded repository-image inputs
    /// targeting GitHub's credential-free raw host. Query and fragment data are
    /// intentionally excluded from the resource identity.
    /// </summary>
    internal static MarkdownRepositoryImageTransportIdentity? TryCreate(
        string source,
        MarkdownImageResolveContext context,
        Uri resourceUri)
    {
        if (source is null ||
            context is null ||
            resourceUri is null ||
            source.Length > MaximumSourceLength ||
            !IsAllowedResource(resourceUri) ||
            !HasBoundedDocumentInputs(context))
        {
            return null;
        }

        MarkdownImageResolutionAuditDiagnostic documentDiagnostic;
        try
        {
            documentDiagnostic = MarkdownImageResolutionAuditDiagnostic.Create(
                source,
                context,
                MarkdownImageResolution.Unavailable);
        }
        catch (ArgumentException)
        {
            // Optional audit identity must not turn malformed source context
            // into a resolver failure.
            return null;
        }
        catch (FormatException)
        {
            return null;
        }

        return new MarkdownRepositoryImageTransportIdentity(
            MarkdownImageResolutionAuditDiagnostic.HashSourceIdentity(source),
            MarkdownImageResolutionAuditDiagnostic.GetSourceKind(source),
            documentDiagnostic.DocumentContextKind,
            documentDiagnostic.DocumentPathSha256,
            documentDiagnostic.DocumentRefIdentitySha256,
            GitHubRawHostRoute,
            Hash("uri-path\0" + resourceUri.AbsolutePath));
    }

    private static bool IsAllowedResource(Uri resourceUri) =>
        resourceUri.IsAbsoluteUri &&
        resourceUri.Scheme == Uri.UriSchemeHttps &&
        resourceUri.Host.Equals("raw.githubusercontent.com", StringComparison.OrdinalIgnoreCase) &&
        resourceUri.IsDefaultPort &&
        resourceUri.UserInfo.Length == 0 &&
        resourceUri.AbsolutePath.Length <= MaximumResourcePathLength;

    private static bool HasBoundedDocumentInputs(MarkdownImageResolveContext context)
    {
        if (context.BaseUri is { IsAbsoluteUri: false } ||
            (context.BaseUri is { IsAbsoluteUri: true } baseUri &&
                baseUri.AbsolutePath.Length > MaximumDocumentInputLength) ||
            !IsWithinLimit(context.DocumentPath, MaximumDocumentInputLength))
        {
            return false;
        }

        MarkdownDocumentSource? documentSource = context.DocumentSource;
        return documentSource is null ||
            IsWithinLimit(documentSource.Path, MaximumDocumentInputLength) &&
            IsWithinLimit(documentSource.Owner, MaximumDocumentInputLength) &&
            IsWithinLimit(documentSource.Repository, MaximumDocumentInputLength) &&
            IsWithinLimit(documentSource.Ref, MaximumDocumentInputLength);
    }

    private static bool IsWithinLimit(string? value, int maximumLength) =>
        value is null || value.Length <= maximumLength;

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
