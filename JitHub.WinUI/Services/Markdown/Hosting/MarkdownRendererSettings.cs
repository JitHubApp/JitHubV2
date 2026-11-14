using System;
using MarkdownRenderer.Images;

namespace JitHub.WinUI.Services.Markdown.Hosting;

/// <summary>The host-controlled settings a Markdown host applies to its renderer.</summary>
/// <param name="IsSelectionEnabled">Whether readers can select text.</param>
/// <param name="IsCodeBlockCopyEnabled">Whether code blocks offer a copy button.</param>
/// <param name="IsSyntaxHighlightingEnabled">Whether code blocks are syntax highlighted.</param>
/// <param name="ImageResolver">The host's image resolver.</param>
/// <param name="ImageBaseUri">The base URI for relative image sources.</param>
/// <param name="DocumentPath">The document's repository path, if any.</param>
/// <param name="DocumentSource">The document's source, if any.</param>
/// <param name="AllowThirdPartyRemoteImages">Whether secure third-party images may load.</param>
internal sealed record MarkdownRendererSettings(
    bool IsSelectionEnabled,
    bool IsCodeBlockCopyEnabled,
    bool IsSyntaxHighlightingEnabled,
    IMarkdownImageResolver ImageResolver,
    Uri ImageBaseUri,
    string? DocumentPath,
    MarkdownDocumentSource? DocumentSource,
    bool AllowThirdPartyRemoteImages);
