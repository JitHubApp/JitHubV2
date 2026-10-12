using System;
using System.IO;
using Xunit;

namespace JitHub.WinUI.Tests.Services;

public sealed class NativeViewportCoverageFailureSourceContractTests
{
    [Fact]
    public void InvalidCaptureProfileIsPreservedWithoutWaivingCoverageFailure()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "JitHub.slnx")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        string source = File.ReadAllText(Path.Combine(directory!.FullName,
            "JitHub.WinUI.Automation", "ReadmeAuditProbe.cs"));
        int check = source.IndexOf("EnsureNativeViewportCoverage(tiles);", StringComparison.Ordinal);
        int record = source.IndexOf("WriteNativeViewportCoverageFailure(output, tiles);", check, StringComparison.Ordinal);
        Assert.True(check >= 0 && record > check);
        Assert.Contains("catch (InvalidOperationException)", source[check..record], StringComparison.Ordinal);
        Assert.StartsWith("WriteNativeViewportCoverageFailure(output, tiles);\n            throw;",
            source[record..].Replace("\r\n", "\n"));
        int helper = source.IndexOf("private static void WriteNativeViewportCoverageFailure(", StringComparison.Ordinal);
        int validator = source.IndexOf("private static void EnsureNativeViewportCoverage(", helper, StringComparison.Ordinal);
        string body = source[helper..validator];
        Assert.Contains("native-viewport-coverage-failure.json", body, StringComparison.Ordinal);
        Assert.Contains("tiles.Take(MaximumTraversalViewports)", body, StringComparison.Ordinal);
        Assert.Contains("tile.RendererDocumentTopDip", body, StringComparison.Ordinal);
        Assert.Contains("tile.ScrollTopViewportUnits", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Uri", body, StringComparison.Ordinal);
    }
}
