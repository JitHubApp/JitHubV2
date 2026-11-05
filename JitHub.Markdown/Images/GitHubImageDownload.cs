using System;

namespace JitHub.Markdown.Images;

public sealed record GitHubImageDownload(
    byte[]? Bytes,
    string? ContentType,
    string? ETag = null,
    DateTimeOffset? LastModified = null,
    bool IsNotModified = false,
    IDisposable? SourceByteLease = null);
