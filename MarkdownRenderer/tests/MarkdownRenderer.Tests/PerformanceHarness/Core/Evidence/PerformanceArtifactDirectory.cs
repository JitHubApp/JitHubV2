using System.Security.Cryptography;
using MarkdownRenderer.PerformanceHarness.Core.Deployment;
using MarkdownRenderer.PerformanceHarness.Core.Reports;

namespace MarkdownRenderer.Tests.PerformanceHarness.Core.Evidence;

/// <summary>
/// A temporary directory holding stand-in harness build artifacts, with their
/// captured SHA-256 hashes, so regression evaluator tests can bind a report to
/// files whose content they control and then change.
/// </summary>
internal sealed class PerformanceArtifactDirectory : IDisposable
{
    private PerformanceArtifactDirectory(string path, BuildArtifactHashes hashes)
    {
        Path = path;
        Hashes = hashes;
    }

    internal BuildArtifactHashes Hashes { get; }

    internal string Identity => PerformanceDeploymentIdentity.CaptureForExecutable(
        Hashes.Executable.Path);

    private string Path { get; }

    internal static PerformanceArtifactDirectory Create(string content)
    {
        string directory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"markdown-renderer-artifacts-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        BuildArtifactHash Capture(string fileName)
        {
            string path = System.IO.Path.Combine(directory, fileName);
            File.WriteAllText(path, $"{content}:{fileName}");
            return new BuildArtifactHash
            {
                FileName = fileName,
                Path = path,
                Sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))),
            };
        }

        return new PerformanceArtifactDirectory(
            directory,
            new BuildArtifactHashes
            {
                Executable = Capture("MarkdownRenderer.PerformanceHarness.exe"),
                PerformanceHarnessDll = Capture("MarkdownRenderer.PerformanceHarness.dll"),
                MarkdownRendererDll = Capture("MarkdownRenderer.dll"),
                RuntimeConfig = Capture(
                    "MarkdownRenderer.PerformanceHarness.runtimeconfig.json"),
            });
    }

    internal BuildArtifactHashes WithExecutableHash(string sha256)
        => new()
        {
            Executable = new BuildArtifactHash
            {
                FileName = Hashes.Executable.FileName,
                Path = Hashes.Executable.Path,
                Sha256 = sha256,
            },
            PerformanceHarnessDll = Hashes.PerformanceHarnessDll,
            MarkdownRendererDll = Hashes.MarkdownRendererDll,
            RuntimeConfig = Hashes.RuntimeConfig,
        };

    internal BuildArtifactHashes WithRuntimeConfigHash(string sha256)
        => new()
        {
            Executable = Hashes.Executable,
            PerformanceHarnessDll = Hashes.PerformanceHarnessDll,
            MarkdownRendererDll = Hashes.MarkdownRendererDll,
            RuntimeConfig = new BuildArtifactHash
            {
                FileName = Hashes.RuntimeConfig.FileName,
                Path = Hashes.RuntimeConfig.Path,
                Sha256 = sha256,
            },
        };

    internal void DeleteFiles()
    {
        if (Directory.Exists(Path))
            Directory.Delete(Path, recursive: true);
    }

    internal void MutateRuntimeConfig()
        => File.AppendAllText(Hashes.RuntimeConfig.Path, ":mutated");

    internal void WriteDependency(string relativePath, string content)
    {
        string path = System.IO.Path.Combine(
            Path,
            relativePath.Replace('/', System.IO.Path.DirectorySeparatorChar));
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    public void Dispose() => DeleteFiles();
}
