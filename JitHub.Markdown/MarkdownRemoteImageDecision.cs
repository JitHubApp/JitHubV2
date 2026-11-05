using MarkdownRenderer.Images;

namespace JitHub.Markdown;

public readonly record struct MarkdownRemoteImageDecision(
    MarkdownRemoteImageAccess Access,
    MarkdownImageUnavailableReason UnavailableReason,
    bool IsThirdParty);
