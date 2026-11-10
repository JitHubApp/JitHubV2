using System;

namespace JitHub.Models.CodeViewer;

[WinRT.GeneratedBindableCustomProperty]
public sealed partial class RepoFileCacheEntry
{
    public required string Sha { get; init; }
    public required long ByteLength { get; init; }
    public required bool IsBinary { get; init; }
    public required byte[] Bytes { get; init; }
    /// <summary>Populated lazily for text files.</summary>
    public string? Text { get; init; }
    /// <summary>e.g. "utf-8", "base64"</summary>
    public string? Encoding { get; init; }
    public DateTimeOffset CachedAt { get; init; }
}
