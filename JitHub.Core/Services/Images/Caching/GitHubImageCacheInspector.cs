using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JitHub.Core.Services.Storage;

namespace JitHub.Core.Services.Images.Caching;

/// <summary>
/// Classifies the image cache directory for the cache registry: active
/// payloads, manifests, orphaned generations and integrity problems. The
/// caller holds every store gate so the view is consistent.
/// </summary>
internal sealed class GitHubImageCacheInspector
{
    private readonly string _imageRootPath;
    private readonly GitHubImageCacheManifestFile _manifests;

    internal GitHubImageCacheInspector(string imageRootPath, GitHubImageCacheManifestFile manifests)
    {
        _imageRootPath = imageRootPath;
        _manifests = manifests;
    }

    internal async Task<CacheStoreInspection> InspectAsync(CancellationToken cancellationToken)
    {
        if (!Directory.Exists(_imageRootPath))
        {
            return new CacheStoreInspection(
                CacheOwnerHealth.Healthy,
                PhysicalBytes: 0,
                LogicalBytes: 0,
                OrphanBytes: 0,
                new Dictionary<string, long>());
        }

        ManifestScan scan = await ScanManifestsAsync(cancellationToken).ConfigureAwait(false);
        long physicalBytes = 0;
        long orphanBytes = 0;
        foreach (string path in Directory.EnumerateFiles(_imageRootPath, "*", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            long length = GitHubImageCacheFiles.TryGetLength(path);
            physicalBytes += length;
            if (!scan.ActiveFiles.Contains(path))
            {
                orphanBytes += length;
            }
        }

        CacheOwnerHealth health = scan.IntegrityProblems.Count > 0
            ? CacheOwnerHealth.Unhealthy
            : orphanBytes > 0
                ? CacheOwnerHealth.Degraded
                : CacheOwnerHealth.Healthy;
        string? detail = CacheInspectionDetail.Format(
            scan.IntegrityProblems.Concat(
                orphanBytes > 0
                    ? (string[])["Unreferenced image generations are awaiting cleanup."]
                    : (string[])[]));

        return new CacheStoreInspection(
            health,
            physicalBytes,
            scan.ActivePayloadBytes,
            orphanBytes,
            new Dictionary<string, long>
            {
                [CacheMetricKeys.ActivePayloadBytes] = scan.ActivePayloadBytes,
                [CacheMetricKeys.ManifestBytes] = scan.ManifestBytes,
                [CacheMetricKeys.OrphanBytes] = orphanBytes
            },
            detail);
    }

    private async Task<ManifestScan> ScanManifestsAsync(CancellationToken cancellationToken)
    {
        ManifestScan scan = new();
        foreach (string manifestPath in Directory.EnumerateFiles(_imageRootPath, "*.meta", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            scan.ManifestBytes += GitHubImageCacheFiles.TryGetLength(manifestPath);
            scan.ActiveFiles.Add(manifestPath);
            string prefix = Path.GetFileNameWithoutExtension(manifestPath);
            try
            {
                GitHubImageCacheManifest manifest = await GitHubImageCacheManifestFile
                    .ReadStrictAsync(manifestPath, cancellationToken)
                    .ConfigureAwait(false);
                if (manifest.PayloadFileName is not null &&
                    !GitHubImageCacheManifestFile.IsSafePayloadFileName(prefix, manifest.PayloadFileName))
                {
                    scan.IntegrityProblems.Add("An image manifest contains an unsafe payload identity.");
                    continue;
                }

                string? payloadPath = _manifests.ResolvePayloadPath(prefix, manifest);
                if (payloadPath is null || !File.Exists(payloadPath))
                {
                    scan.IntegrityProblems.Add("An image manifest references a missing payload.");
                    continue;
                }

                scan.ActiveFiles.Add(payloadPath);
                scan.ActivePayloadBytes += GitHubImageCacheFiles.TryGetLength(payloadPath);
            }
            catch (Exception exception) when (exception is IOException or FormatException or InvalidDataException)
            {
                scan.IntegrityProblems.Add("An image manifest is corrupt.");
            }
        }

        return scan;
    }

    private sealed class ManifestScan
    {
        public HashSet<string> ActiveFiles { get; } = new(StringComparer.OrdinalIgnoreCase);

        public List<string> IntegrityProblems { get; } = [];

        public long ActivePayloadBytes { get; set; }

        public long ManifestBytes { get; set; }
    }
}
