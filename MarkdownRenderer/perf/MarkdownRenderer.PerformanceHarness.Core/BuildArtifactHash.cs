namespace MarkdownRenderer.PerformanceHarness;

public sealed class BuildArtifactHash
{
    public string FileName { get; init; } = string.Empty;
    public string Path { get; init; } = string.Empty;
    public string Sha256 { get; init; } = string.Empty;
}
