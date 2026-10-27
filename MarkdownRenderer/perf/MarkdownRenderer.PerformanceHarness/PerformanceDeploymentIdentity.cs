using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace MarkdownRenderer.PerformanceHarness;

/// <summary>
/// Identifies the complete private runtime output used by a performance run.
/// Symbols and XML documentation are excluded because they cannot affect the
/// executing deployment; every other file below the apphost directory is bound.
/// </summary>
internal static class PerformanceDeploymentIdentity
{
    internal const string Policy = "runtime-output-manifest-v1";
    internal const string Prefix = Policy + ":";

    private static readonly UTF8Encoding StrictUtf8NoBom = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    internal static string CaptureForExecutable(string executablePath)
        => CaptureForExecutable(executablePath, afterFirstSnapshot: null);

    internal static string CaptureForExecutable(
        string executablePath,
        Action? afterFirstSnapshot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        string fullExecutablePath = Path.GetFullPath(executablePath);
        if (!File.Exists(fullExecutablePath))
        {
            throw new FileNotFoundException(
                "The deployment executable was not found.",
                fullExecutablePath);
        }

        string deploymentDirectory = Path.GetDirectoryName(fullExecutablePath) ??
            throw new InvalidOperationException(
                "The deployment executable must have a parent directory.");
        string first = CaptureSnapshot(deploymentDirectory);
        afterFirstSnapshot?.Invoke();
        string second = CaptureSnapshot(deploymentDirectory);
        if (!string.Equals(first, second, StringComparison.Ordinal))
        {
            throw new IOException(
                "The runtime output changed while its deployment identity was being captured.");
        }

        return Prefix + second;
    }

    private static string CaptureSnapshot(string deploymentDirectory)
    {
        DeploymentFile[] files = EnumerateDeploymentFiles(deploymentDirectory)
            .OrderBy(static file => file.RelativePath, StringComparer.Ordinal)
            .ToArray();
        var lines = new List<string>(files.Length + 1) { Policy };
        foreach (DeploymentFile file in files)
        {
            using FileStream stream = new(
                file.FullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 128 * 1024,
                FileOptions.SequentialScan);
            long length = stream.Length;
            string sha256 = Convert.ToHexString(SHA256.HashData(stream));
            if (stream.Length != length)
            {
                throw new IOException(
                    $"Runtime file '{file.RelativePath}' changed while it was being hashed.");
            }
            lines.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"{file.RelativePath}|{length}|{sha256}"));
        }

        byte[] payload = StrictUtf8NoBom.GetBytes(string.Join('\n', lines));
        return Convert.ToHexString(SHA256.HashData(payload));
    }

    internal static bool IsWellFormed(string? identity)
        => IsUpperHexIdentity(identity);

    internal static bool TryValidateCurrent(
        string? identity,
        string executablePath,
        out string failure)
    {
        if (!IsWellFormed(identity))
        {
            failure = $"Deployment identity must use '{Prefix}<64 uppercase SHA-256>'.";
            return false;
        }

        try
        {
            string current = CaptureForExecutable(executablePath);
            if (!string.Equals(identity, current, StringComparison.Ordinal))
            {
                failure = "The current runtime output does not match the captured deployment identity.";
                return false;
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            failure = $"The current runtime output identity could not be verified: {exception.Message}";
            return false;
        }

        failure = string.Empty;
        return true;
    }

    internal static bool IsPathWithinDeploymentDirectory(
        string path,
        string executablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        string deploymentDirectory = Path.GetDirectoryName(Path.GetFullPath(executablePath)) ??
            throw new InvalidOperationException(
                "The deployment executable must have a parent directory.");
        string fullPath = Path.GetFullPath(path);
        string fullDeploymentDirectory = Path.GetFullPath(deploymentDirectory);
        return IsPathWithinDirectory(fullPath, fullDeploymentDirectory) ||
               IsPathWithinDirectory(
                   ResolveExistingReparsePoints(fullPath),
                   ResolveExistingReparsePoints(fullDeploymentDirectory));
    }

    private static IEnumerable<DeploymentFile> EnumerateDeploymentFiles(
        string deploymentDirectory)
    {
        var pendingDirectories = new Stack<string>();
        pendingDirectories.Push(deploymentDirectory);
        while (pendingDirectories.Count > 0)
        {
            string directory = pendingDirectories.Pop();
            foreach (string path in Directory.EnumerateFileSystemEntries(directory))
            {
                FileAttributes attributes = File.GetAttributes(path);
                string relativePath = NormalizeRelativePath(deploymentDirectory, path);
                bool isDirectory = (attributes & FileAttributes.Directory) != 0;
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new IOException(
                        $"The runtime output contains unsupported reparse point '{relativePath}'.");
                }
                if (!isDirectory && IsExcluded(path))
                    continue;

                if (isDirectory)
                {
                    pendingDirectories.Push(path);
                }
                else
                {
                    yield return new DeploymentFile(path, relativePath);
                }
            }
        }
    }

    private static bool IsPathWithinDirectory(string path, string directory)
    {
        string relativePath = Path.GetRelativePath(directory, path);
        return relativePath.Length == 0 ||
               (!Path.IsPathRooted(relativePath) &&
                !string.Equals(relativePath, "..", StringComparison.Ordinal) &&
                !relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
                !relativePath.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal));
    }

    private static string ResolveExistingReparsePoints(string path)
    {
        string pendingPath = Path.GetFullPath(path);
        var visitedPaths = new HashSet<string>(
            OperatingSystem.IsWindows()
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal);
        for (int redirectCount = 0; redirectCount < 64; redirectCount++)
        {
            if (!visitedPaths.Add(pendingPath))
            {
                throw new IOException(
                    $"A reparse-point cycle was detected while resolving '{path}'.");
            }

            string root = Path.GetPathRoot(pendingPath) ??
                throw new ArgumentException("The path must have a root.", nameof(path));
            string current = root;
            string[] segments = pendingPath[root.Length..].Split(
                [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                StringSplitOptions.RemoveEmptyEntries);
            bool redirected = false;
            for (int index = 0; index < segments.Length; index++)
            {
                string candidate = Path.Combine(current, segments[index]);
                FileSystemInfo? entry = Directory.Exists(candidate)
                    ? new DirectoryInfo(candidate)
                    : File.Exists(candidate) ? new FileInfo(candidate) : null;
                if (entry is null)
                {
                    for (; index < segments.Length; index++)
                        current = Path.Combine(current, segments[index]);
                    return Path.GetFullPath(current);
                }

                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    FileSystemInfo target = entry.ResolveLinkTarget(returnFinalTarget: false) ??
                        throw new IOException(
                            $"The reparse-point target could not be resolved for '{entry.FullName}'.");
                    pendingPath = target.FullName;
                    for (int remainder = index + 1; remainder < segments.Length; remainder++)
                        pendingPath = Path.Combine(pendingPath, segments[remainder]);
                    pendingPath = Path.GetFullPath(pendingPath);
                    redirected = true;
                    break;
                }

                current = entry.FullName;
            }

            if (!redirected)
                return Path.GetFullPath(current);
        }

        throw new IOException(
            $"Too many reparse-point redirects were encountered while resolving '{path}'.");
    }

    private static bool IsExcluded(string path)
    {
        string extension = Path.GetExtension(path);
        return extension.Equals(".pdb", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".xml", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeRelativePath(string directory, string path)
        => Path.GetRelativePath(directory, path).Replace('\\', '/');

    private static bool IsUpperHexIdentity(string? identity)
    {
        if (identity is null ||
            identity.Length != Prefix.Length + 64 ||
            !identity.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        foreach (char character in identity.AsSpan(Prefix.Length))
        {
            if (character is not (>= '0' and <= '9') and not (>= 'A' and <= 'F'))
                return false;
        }

        return true;
    }

    private readonly record struct DeploymentFile(
        string FullPath,
        string RelativePath);
}
