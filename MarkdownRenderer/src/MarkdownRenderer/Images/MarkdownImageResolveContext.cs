using System;

namespace MarkdownRenderer.Images;

/// <summary>
/// Context supplied to markdown image resolvers.
/// </summary>
/// <param name="BaseUri">Optional document base URI used to resolve relative image sources.</param>
/// <param name="DocumentPath">Optional source document path, when the host can provide one.</param>
/// <param name="AllowThirdPartyRemoteImages">True when the host's policy or the user explicitly permits third-party HTTPS images.</param>
/// <param name="DocumentSource">Canonical source identity for repository-backed documents.</param>
public sealed record MarkdownImageResolveContext(
    Uri? BaseUri,
    string? DocumentPath = null,
    bool AllowThirdPartyRemoteImages = false,
    MarkdownDocumentSource? DocumentSource = null);
