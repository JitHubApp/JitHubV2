using System.Security.Cryptography;

namespace MarkdownRenderer.PerformanceHarness;

internal static class PerformanceArtifactContract
{
    private static readonly string[] ExpectedFileNames =
    [
        "MarkdownRenderer.PerformanceHarness.exe",
        "MarkdownRenderer.PerformanceHarness.dll",
        "MarkdownRenderer.dll",
        "MarkdownRenderer.Core.dll",
        "MarkdownRenderer.PerformanceHarness.runtimeconfig.json",
    ];

    internal static bool TryValidateStored(
        BuildArtifactHashes? artifacts,
        out string failure)
    {
        if (artifacts is null || artifacts.HashAlgorithm != "SHA-256")
        {
            failure = "Build artifact evidence must declare SHA-256.";
            return false;
        }

        BuildArtifactHash?[] entries =
        [
            artifacts.Executable,
            artifacts.PerformanceHarnessDll,
            artifacts.MarkdownRendererDll,
            artifacts.MarkdownRendererCoreDll,
            artifacts.RuntimeConfig,
        ];
        for (int index = 0; index < entries.Length; index++)
        {
            BuildArtifactHash? artifact = entries[index];
            if (artifact is null ||
                artifact.FileName != ExpectedFileNames[index] ||
                string.IsNullOrWhiteSpace(artifact.Path) ||
                !Path.IsPathFullyQualified(artifact.Path) ||
                !string.Equals(
                    Path.GetFileName(artifact.Path),
                    ExpectedFileNames[index],
                    StringComparison.OrdinalIgnoreCase) ||
                !IsSha256(artifact.Sha256))
            {
                failure = $"Build artifact evidence is invalid for '{ExpectedFileNames[index]}'.";
                return false;
            }
        }

        failure = string.Empty;
        return true;
    }

    internal static bool TryValidateCurrentFiles(
        BuildArtifactHashes? artifacts,
        out string failure)
    {
        if (!TryValidateStored(artifacts, out failure))
            return false;

        BuildArtifactHash[] entries =
        [
            artifacts!.Executable,
            artifacts.PerformanceHarnessDll,
            artifacts.MarkdownRendererDll,
            artifacts.MarkdownRendererCoreDll,
            artifacts.RuntimeConfig,
        ];
        try
        {
            foreach (BuildArtifactHash artifact in entries)
            {
                if (!File.Exists(artifact.Path) ||
                    !string.Equals(
                        artifact.Sha256,
                        ComputeFileSha256(artifact.Path),
                        StringComparison.OrdinalIgnoreCase))
                {
                    failure = $"Current build artifact does not match '{artifact.FileName}'.";
                    return false;
                }
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            failure = $"Build artifact evidence could not be verified: {exception.Message}";
            return false;
        }

        failure = string.Empty;
        return true;
    }

    internal static bool HaveIdenticalHashes(
        BuildArtifactHashes? candidate,
        BuildArtifactHashes? reference)
        => candidate is not null &&
           reference is not null &&
           string.Equals(candidate.HashAlgorithm, reference.HashAlgorithm, StringComparison.Ordinal) &&
           HashEquals(candidate.Executable, reference.Executable) &&
           HashEquals(candidate.PerformanceHarnessDll, reference.PerformanceHarnessDll) &&
           HashEquals(candidate.MarkdownRendererDll, reference.MarkdownRendererDll) &&
           HashEquals(candidate.MarkdownRendererCoreDll, reference.MarkdownRendererCoreDll) &&
           HashEquals(candidate.RuntimeConfig, reference.RuntimeConfig);

    private static string ComputeFileSha256(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static bool HashEquals(BuildArtifactHash? left, BuildArtifactHash? right)
        => left is not null &&
           right is not null &&
           IsSha256(left.Sha256) &&
           string.Equals(left.Sha256, right.Sha256, StringComparison.OrdinalIgnoreCase);

    private static bool IsSha256(string? value)
        => value is { Length: 64 } &&
           value.All(static character =>
               character is >= '0' and <= '9' or
               >= 'a' and <= 'f' or
               >= 'A' and <= 'F');
}
