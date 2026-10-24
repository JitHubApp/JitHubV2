using Xunit;

namespace JitHub.WinUI.Tests.Services;

// These checks cover wiring that is not compiled by the pure headless project.
// They do not replace the shipping app build or a live diagnostic replay.
public sealed class MarkdownRepositoryImageTransportSourceContractTests
{
    [Fact]
    public void TransportTracingIsExplicitlyOptInAndPreservedSeparatelyFromResolutionEvidence()
    {
        string source = ReadSource("JitHub.WinUI.Automation", "ReadmeAuditProbe.cs");
        Assert.Contains("JITHUB_README_AUDIT_IMAGE_TRANSPORT_DIAGNOSTICS", source, StringComparison.Ordinal);
        Assert.Contains("\"JITHUB_MARKDOWN_IMAGE_TRANSPORT_EVIDENCE_PATH\"", source, StringComparison.Ordinal);
        Assert.Contains("? imageTransportEvidence", source, StringComparison.Ordinal);
        Assert.Contains("PreserveEvidenceFile(imageTransportEvidence, Path.Combine(output, \"image-transport.ndjson\"))", source, StringComparison.Ordinal);
        Assert.Contains("imageResolutionEvidence, imageTransportEvidence, rasterPreparationEvidence", source, StringComparison.Ordinal);
        Assert.Contains("ImageTransportDiagnosticsEnabled = imageTransportDiagnosticsEnabled", source, StringComparison.Ordinal);
        Assert.Contains("if (native.ImageTransportDiagnosticsEnabled)", source, StringComparison.Ordinal);
        Assert.Contains("exclude this case from release performance qualification", source, StringComparison.Ordinal);
    }

    [Fact]
    public void RepositoryFallbackAndBothSourceOwnershipPathsUseTheObserverSeams()
    {
        string source = ReadSource("JitHub.WinUI", "Services", "GitHubService.cs");
        Assert.Contains("source, context, rawUri, MarkdownRepositoryImageTransportStage.RawImageServiceResult", source, StringComparison.Ordinal);
        Assert.Contains("observeOutcome: rawObserver", source, StringComparison.Ordinal);
        Assert.Contains("fetcher = MarkdownRepositoryImageTransportObserver.WrapFetcher(", source, StringComparison.Ordinal);
        Assert.Contains("source, context, rawUri, MarkdownRepositoryImageTransportStage.RepositoryFetcher", source, StringComparison.Ordinal);
    }

    private static string ReadSource(params string[] segments)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "JitHub.slnx")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return File.ReadAllText(Path.Combine(new[] { directory.FullName }.Concat(segments).ToArray()));
    }
}
