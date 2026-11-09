using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace JitHub.Services;

/// <summary>
/// Reads and atomically writes an image cache entry's manifest, which names
/// its current payload generation and holds its HTTP validators. The manifest
/// replace is the commit point for a new generation.
/// </summary>
internal sealed class GitHubImageCacheManifestFile
{
    private readonly string _imageRootPath;

    internal GitHubImageCacheManifestFile(string imageRootPath)
    {
        _imageRootPath = imageRootPath;
    }

    internal async Task<GitHubImageCacheManifest> ReadAsync(
        string prefix,
        CancellationToken cancellationToken)
    {
        string path = Path.Combine(_imageRootPath, $"{prefix}.meta");
        if (!File.Exists(path))
        {
            return new GitHubImageCacheManifest(null, new GitHubImageCacheWriteMetadata(null, null, null));
        }

        try
        {
            string[] lines = await File.ReadAllLinesAsync(path, cancellationToken).ConfigureAwait(false);
            if (string.Equals(lines.ElementAtOrDefault(0), "v3", StringComparison.Ordinal))
            {
                string? payloadFileName = Decode(lines.ElementAtOrDefault(1));
                return new GitHubImageCacheManifest(
                    IsSafePayloadFileName(prefix, payloadFileName) ? payloadFileName : null,
                    new GitHubImageCacheWriteMetadata(
                        Decode(lines.ElementAtOrDefault(2)),
                        DateTimeOffset.TryParse(lines.ElementAtOrDefault(3), out DateTimeOffset modified) ? modified : null,
                        Decode(lines.ElementAtOrDefault(4))),
                    Decode(lines.ElementAtOrDefault(5)));
            }

            if (string.Equals(lines.ElementAtOrDefault(0), "v2", StringComparison.Ordinal))
            {
                string? payloadFileName = Decode(lines.ElementAtOrDefault(1));
                return new GitHubImageCacheManifest(
                    IsSafePayloadFileName(prefix, payloadFileName) ? payloadFileName : null,
                    new GitHubImageCacheWriteMetadata(
                        Decode(lines.ElementAtOrDefault(2)),
                        DateTimeOffset.TryParse(lines.ElementAtOrDefault(3), out DateTimeOffset modified) ? modified : null,
                        Decode(lines.ElementAtOrDefault(4))));
            }

            // Legacy v1 sidecars did not include a payload pointer. Keep them readable while
            // new writes move to generation manifests.
            return new GitHubImageCacheManifest(
                null,
                new GitHubImageCacheWriteMetadata(
                    Decode(lines.ElementAtOrDefault(0)),
                    DateTimeOffset.TryParse(lines.ElementAtOrDefault(1), out DateTimeOffset legacyModified) ? legacyModified : null,
                    Decode(lines.ElementAtOrDefault(2))));
        }
        catch (IOException)
        {
            return new GitHubImageCacheManifest(null, new GitHubImageCacheWriteMetadata(null, null, null));
        }
        catch (FormatException)
        {
            return new GitHubImageCacheManifest(null, new GitHubImageCacheWriteMetadata(null, null, null));
        }
    }

    internal static async Task<GitHubImageCacheManifest> ReadStrictAsync(
        string manifestPath,
        CancellationToken cancellationToken)
    {
        string[] lines = await File.ReadAllLinesAsync(manifestPath, cancellationToken).ConfigureAwait(false);
        if (string.Equals(lines.ElementAtOrDefault(0), "v3", StringComparison.Ordinal))
        {
            if (lines.Length < 6)
            {
                throw new InvalidDataException("The partitioned image manifest is incomplete.");
            }

            return new GitHubImageCacheManifest(
                Decode(lines[1]),
                new GitHubImageCacheWriteMetadata(
                    Decode(lines[2]),
                    DateTimeOffset.TryParse(lines[3], out DateTimeOffset modified) ? modified : null,
                    Decode(lines[4])),
                Decode(lines[5]));
        }

        if (string.Equals(lines.ElementAtOrDefault(0), "v2", StringComparison.Ordinal))
        {
            if (lines.Length < 5)
            {
                throw new InvalidDataException("The image manifest is incomplete.");
            }

            return new GitHubImageCacheManifest(
                Decode(lines[1]),
                new GitHubImageCacheWriteMetadata(
                    Decode(lines[2]),
                    DateTimeOffset.TryParse(lines[3], out DateTimeOffset modified) ? modified : null,
                    Decode(lines[4])));
        }

        if (lines.Length < 3)
        {
            throw new InvalidDataException("The legacy image manifest is incomplete.");
        }

        return new GitHubImageCacheManifest(
            null,
            new GitHubImageCacheWriteMetadata(
                Decode(lines[0]),
                DateTimeOffset.TryParse(lines[1], out DateTimeOffset legacyModified) ? legacyModified : null,
                Decode(lines[2])));
    }

    internal async Task WriteAtomicallyAsync(
        string prefix,
        GitHubImageCacheManifest manifest,
        CancellationToken cancellationToken)
    {
        string path = Path.Combine(_imageRootPath, $"{prefix}.meta");
        string temporaryPath = Path.Combine(_imageRootPath, $"{prefix}.{Guid.NewGuid():N}.meta.tmp");
        string[] lines =
        [
            "v3",
            Encode(manifest.PayloadFileName),
            Encode(manifest.Metadata.ETag),
            manifest.Metadata.LastModified?.ToString("O") ?? string.Empty,
            Encode(manifest.Metadata.ContentType),
            Encode(manifest.AccountPartition)
        ];
        try
        {
            await using (FileStream stream = new(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (StreamWriter writer = new(stream, Encoding.UTF8))
            {
                foreach (string line in lines)
                {
                    await writer.WriteLineAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
                }

                await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            GitHubImageCacheFiles.TryDelete(temporaryPath);
        }
    }

    internal string? ResolvePayloadPath(string prefix, GitHubImageCacheManifest manifest)
    {
        if (IsSafePayloadFileName(prefix, manifest.PayloadFileName))
        {
            return Path.Combine(_imageRootPath, manifest.PayloadFileName!);
        }

        string stablePath = Path.Combine(_imageRootPath, $"{prefix}.img");
        if (File.Exists(stablePath))
        {
            return stablePath;
        }

        return Directory
            .EnumerateFiles(_imageRootPath, $"{prefix}.*", SearchOption.TopDirectoryOnly)
            .FirstOrDefault(path =>
                GitHubImageCacheFiles.IsPayloadPath(path) &&
                string.Equals(Path.GetFileNameWithoutExtension(path), prefix, StringComparison.OrdinalIgnoreCase));
    }

    internal static bool IsSafePayloadFileName(string prefix, string? payloadFileName) =>
        !string.IsNullOrWhiteSpace(payloadFileName) &&
        string.Equals(Path.GetFileName(payloadFileName), payloadFileName, StringComparison.Ordinal) &&
        payloadFileName.StartsWith(prefix + ".", StringComparison.OrdinalIgnoreCase) &&
        payloadFileName.EndsWith(".img", StringComparison.OrdinalIgnoreCase);

    private static string Encode(string? value) => string.IsNullOrWhiteSpace(value)
        ? string.Empty
        : Convert.ToBase64String(Encoding.UTF8.GetBytes(value));

    private static string? Decode(string? value) => string.IsNullOrWhiteSpace(value)
        ? null
        : Encoding.UTF8.GetString(Convert.FromBase64String(value));
}
