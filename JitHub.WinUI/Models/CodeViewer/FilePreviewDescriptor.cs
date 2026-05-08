namespace JitHub.Models.CodeViewer;

public sealed record FilePreviewDescriptor(
    RepoFilePreviewKind Kind,
    string LanguageId,
    string? ImageMimeType,
    bool IsLikelyBinary);
