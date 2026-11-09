using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace JitHub.Services;

/// <summary>
/// File naming and best-effort file operations shared by the image cache store's parts.
/// </summary>
internal static class GitHubImageCacheFiles
{
    /// <summary>Names an entry's files by the hash of its cache key, so keys never reach the file system.</summary>
    internal static string HashKey(string cacheKey)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(cacheKey));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    internal static bool IsPayloadPath(string path) =>
        !path.EndsWith(".meta", StringComparison.OrdinalIgnoreCase) &&
        !path.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase);

    internal static long CalculatePayloadBytes(string rootPath) => Directory.Exists(rootPath)
        ? Directory
            .EnumerateFiles(rootPath, "*", SearchOption.TopDirectoryOnly)
            .Where(static path => IsPayloadPath(path))
            .Sum(static path => TryGetLength(path))
        : 0;

    internal static long TryGetLength(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch (FileNotFoundException)
        {
            return 0;
        }
    }

    internal static string? ExtractAccountPartition(string cacheKey)
    {
        int separator = cacheKey.IndexOf(':');
        return separator > 0 ? cacheKey[..separator] : null;
    }

    internal static string NormalizeExtension(string extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
        {
            return ".img";
        }

        string normalized = extension.Trim();
        if (!normalized.StartsWith(".", StringComparison.Ordinal))
        {
            normalized = "." + normalized;
        }

        return normalized.Length > 12 ? ".img" : normalized.ToLowerInvariant();
    }

    /// <summary>Deletes a file and records anything that remains as a clear residual.</summary>
    internal static void TryDeleteStrict(string path, List<CacheClearResidual> residuals)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            if (File.Exists(path))
            {
                residuals.Add(new CacheClearResidual(path, "The file still exists after deletion."));
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            residuals.Add(new CacheClearResidual(
                path,
                $"{exception.GetType().Name}: {exception.Message}"));
        }
    }

    /// <summary>Deletes a file if it exists. A file that cannot be deleted is left for cap enforcement.</summary>
    internal static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception)
        {
            // Best effort: a reader may still hold the file open. Unreferenced
            // generations are reclaimed by the next cap enforcement or clear.
        }
    }
}
