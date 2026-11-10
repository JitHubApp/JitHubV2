namespace JitHub.Models.CodeViewer;

[WinRT.GeneratedBindableCustomProperty]
public sealed partial class RepoFileBlob
{
    public string? Sha { get; init; }
    public string? Encoding { get; init; }
    public byte[]? Bytes { get; init; }
    public string? Text { get; init; }
    public bool IsBinary { get; init; }
}
