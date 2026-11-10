using MarkdownRenderer.Images;

namespace JitHub.Markdown.Images.RemoteContent;

public readonly record struct MarkdownRemoteImageDecision(
    MarkdownRemoteImageAccess Access,
    MarkdownImageUnavailableReason UnavailableReason,
    bool IsThirdParty);
