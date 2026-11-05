using MarkdownRenderer.Images;

namespace JitHub.Services.Markdown;

public readonly record struct MarkdownRemoteImageDecision(
    MarkdownRemoteImageAccess Access,
    MarkdownImageUnavailableReason UnavailableReason,
    bool IsThirdParty);
