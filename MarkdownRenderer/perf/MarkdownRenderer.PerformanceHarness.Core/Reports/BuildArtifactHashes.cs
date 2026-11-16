namespace MarkdownRenderer.PerformanceHarness.Core.Reports;

public sealed class BuildArtifactHashes
{
    public string HashAlgorithm { get; init; } = "SHA-256";
    public BuildArtifactHash Executable { get; init; } = new();
    public BuildArtifactHash PerformanceHarnessDll { get; init; } = new();
    public BuildArtifactHash MarkdownRendererDll { get; init; } = new();
    public BuildArtifactHash RuntimeConfig { get; init; } = new();
}
