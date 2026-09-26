using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using MarkdownRenderer.PerformanceHarness;
using Xunit;

namespace MarkdownRenderer.Tests;

public sealed class PerformanceDeploymentIdentityTests
{
    [Fact]
    public void CaptureUsesTheFrozenCanonicalRecursiveManifest()
    {
        using var deployment = DeploymentDirectory.Create();
        string executable = deployment.Write(
            "MarkdownRenderer.PerformanceHarness.exe",
            "host");
        string nested = deployment.Write("nested/a.bin", "nested");
        string library = deployment.Write("z.dll", "library");
        _ = deployment.Write("ignored.PDB", "symbols");
        _ = deployment.Write("nested/ignored.Xml", "documentation");

        string[] entries = [executable, nested, library];
        string[] manifestLines = entries
            .Select(path => new
            {
                RelativePath = Path.GetRelativePath(deployment.Path, path).Replace('\\', '/'),
                Length = new FileInfo(path).Length,
                Sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))),
            })
            .OrderBy(static entry => entry.RelativePath, StringComparer.Ordinal)
            .Select(static entry => string.Create(
                CultureInfo.InvariantCulture,
                $"{entry.RelativePath}|{entry.Length}|{entry.Sha256}"))
            .Prepend(PerformanceDeploymentIdentity.Policy)
            .ToArray();
        string payload = string.Join('\n', manifestLines);
        string expected = PerformanceDeploymentIdentity.Prefix +
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));

        Assert.Equal(expected, PerformanceDeploymentIdentity.CaptureForExecutable(executable));
    }

    [Fact]
    public void SymbolsAndXmlDoNotChangeIdentityButRuntimeDependenciesDo()
    {
        using var deployment = DeploymentDirectory.Create();
        string executable = deployment.Write(
            "MarkdownRenderer.PerformanceHarness.exe",
            "host");
        string symbols = deployment.Write("symbols.pdb", "first symbols");
        string documentation = deployment.Write("docs.XML", "first docs");
        string dependency = deployment.Write("nested/Markdig.dll", "first dependency");
        string original = PerformanceDeploymentIdentity.CaptureForExecutable(executable);

        File.WriteAllText(symbols, "different symbols");
        File.WriteAllText(documentation, "different docs");
        Assert.Equal(original, PerformanceDeploymentIdentity.CaptureForExecutable(executable));

        File.WriteAllText(dependency, "different dependency");
        string changed = PerformanceDeploymentIdentity.CaptureForExecutable(executable);
        Assert.NotEqual(original, changed);
        Assert.False(PerformanceDeploymentIdentity.TryValidateCurrent(
            original,
            executable,
            out _));
        Assert.True(PerformanceDeploymentIdentity.TryValidateCurrent(
            changed,
            executable,
            out string failure),
            failure);
    }

    [Fact]
    public void CaptureRejectsADeploymentThatChangesBetweenStabilityPasses()
    {
        using var deployment = DeploymentDirectory.Create();
        string executable = deployment.Write(
            "MarkdownRenderer.PerformanceHarness.exe",
            "host");
        string dependency = deployment.Write("nested/dependency.dll", "before");

        IOException exception = Assert.Throws<IOException>(() =>
            PerformanceDeploymentIdentity.CaptureForExecutable(
                executable,
                () => File.WriteAllText(dependency, "after")));

        Assert.Contains("changed", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(PerformanceDeploymentIdentity.IsWellFormed(
            PerformanceDeploymentIdentity.CaptureForExecutable(executable)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("build-label")]
    [InlineData("runtime-output-manifest-v1:abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789")]
    [InlineData("runtime-output-manifest-v2:ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789")]
    public void StoredIdentityRequiresTheExactPolicyAndUppercaseSha256(string identity)
        => Assert.False(PerformanceDeploymentIdentity.IsWellFormed(identity));

    [Fact]
    public void OptionsRejectReportsInsideTheRuntimeDeploymentDirectory()
    {
        string executable = Environment.ProcessPath ??
            throw new InvalidOperationException("The test process path is unavailable.");
        string deploymentDirectory = Path.GetDirectoryName(executable)!;
        string insideOutput = Path.Combine(
            deploymentDirectory,
            $"performance-{Guid.NewGuid():N}.json");

        Assert.Throws<ArgumentException>(() => PerformanceOptions.Parse(
        [
            "--output", insideOutput, "--baseline",
        ]));
        Assert.Throws<ArgumentException>(() => PerformanceOptions.Parse(
        [
            "--output", NewTemporaryPath(), "--reference", executable,
        ]));
    }

    [Fact]
    public void ContainmentResolvesAnExistingLinkParentIntoTheDeployment()
    {
        using var deployment = DeploymentDirectory.Create();
        using var outside = DeploymentDirectory.Create();
        string executable = deployment.Write(
            "MarkdownRenderer.PerformanceHarness.exe",
            "host");
        _ = deployment.Write("nested/dependency.dll", "dependency");
        string link = Path.Combine(outside.Path, "deployment-link");
        string nestedLink = Path.Combine(outside.Path, "nested-deployment-link");
        Directory.CreateSymbolicLink(link, deployment.Path);
        Directory.CreateSymbolicLink(nestedLink, Path.Combine(link, "nested"));
        try
        {
            Assert.True(PerformanceDeploymentIdentity.IsPathWithinDeploymentDirectory(
                Path.Combine(link, "report.json"),
                executable));
            Assert.True(PerformanceDeploymentIdentity.IsPathWithinDeploymentDirectory(
                Path.Combine(nestedLink, "report.json"),
                executable));
        }
        finally
        {
            Directory.Delete(nestedLink);
            Directory.Delete(link);
        }
    }

    [Fact]
    public void CaptureRejectsChildDirectoryReparsePoints()
    {
        using var deployment = DeploymentDirectory.Create();
        using var outside = DeploymentDirectory.Create();
        string executable = deployment.Write(
            "MarkdownRenderer.PerformanceHarness.exe",
            "host");
        _ = outside.Write("external-runtime/dependency.dll", "dependency");
        string link = Path.Combine(deployment.Path, "linked-runtime");
        Directory.CreateSymbolicLink(
            link,
            Path.Combine(outside.Path, "external-runtime"));
        try
        {
            IOException exception = Assert.Throws<IOException>(() =>
                PerformanceDeploymentIdentity.CaptureForExecutable(executable));
            Assert.Contains("reparse point", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(link);
        }
    }

    private static string NewTemporaryPath()
        => Path.Combine(
            Path.GetTempPath(),
            $"markdown-renderer-deployment-identity-{Guid.NewGuid():N}.json");

    private sealed class DeploymentDirectory : IDisposable
    {
        private DeploymentDirectory(string path) => Path = path;

        internal string Path { get; }

        internal static DeploymentDirectory Create()
        {
            string path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"markdown-renderer-deployment-{Guid.NewGuid():N}");
            Directory.CreateDirectory(path);
            return new DeploymentDirectory(path);
        }

        internal string Write(string relativePath, string content)
        {
            string path = System.IO.Path.Combine(
                Path,
                relativePath.Replace('/', System.IO.Path.DirectorySeparatorChar));
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
            return path;
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }
}
