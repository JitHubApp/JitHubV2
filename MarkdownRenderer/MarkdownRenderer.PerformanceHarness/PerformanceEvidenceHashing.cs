using System.Security.Cryptography;
using MarkdownRenderer.Controls;

namespace MarkdownRenderer.PerformanceHarness;

internal static class PerformanceEvidenceHashing
{
    internal static BuildArtifactHashes CaptureBuildArtifacts()
    {
        string executablePath = Environment.ProcessPath ??
            throw new InvalidOperationException("The performance executable path is unavailable.");
        return new BuildArtifactHashes
        {
            Executable = CaptureArtifact(executablePath),
            PerformanceHarnessDll = CaptureArtifact(typeof(PerformanceEvidenceHashing).Assembly.Location),
            MarkdownRendererDll = CaptureArtifact(typeof(MarkdownScrollView).Assembly.Location),
            MarkdownRendererCoreDll = CaptureArtifact(typeof(MarkdownEngine).Assembly.Location),
            RuntimeConfig = CaptureArtifact(Path.ChangeExtension(executablePath, ".runtimeconfig.json")),
        };
    }

    internal static BuildArtifactHash CaptureArtifact(string path)
    {
        string fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException("A performance evidence artifact was not found.", fullPath);

        return new BuildArtifactHash
        {
            FileName = Path.GetFileName(fullPath),
            Path = fullPath,
            Sha256 = ComputeFileSha256(fullPath),
        };
    }

    internal static string ComputeFileSha256(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}
