using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using MarkdownRenderer.Images;

namespace JitHub.Services.Markdown;

/// <summary>
/// Resolves images only from a pinned same-byte audit fixture. It has no online fallback.
/// This resolver is constructed only by the explicit production README audit launch mode.
/// </summary>
internal sealed partial class MarkdownSameByteAuditImageResolver :
    IMarkdownImageResolver,
    IMarkdownImagePrefetcher
{
    private const long MaximumManifestBytes = 4 * 1024 * 1024;
    private const long MaximumReadmeBytes = 16 * 1024 * 1024;
    private const long MaximumBrowserRenderBytes = 32 * 1024 * 1024;
    private const int MaximumAssetBytes = 64 * 1024 * 1024;
    private const long MaximumAggregateAssetBytes = 256 * 1024 * 1024;
    private readonly string _root;
    private readonly string _expectedRepository;
    private readonly string _expectedCommit;
    private readonly Lazy<Task<FixtureIndex>> _index;

    public MarkdownSameByteAuditImageResolver(
        string corpusDirectory,
        string expectedRepository,
        string expectedCommit,
        string expectedReadmeGitBlobSha1)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(corpusDirectory);
        _root = Path.GetFullPath(corpusDirectory);
        // Missing/malformed audit pin configuration is an unavailable fixture,
        // not a reason to crash the renderer during control construction.
        _expectedRepository = expectedRepository?.Trim() ?? string.Empty;
        _expectedCommit = expectedCommit?.Trim() ?? string.Empty;
        _expectedReadmeGitBlobSha1 = expectedReadmeGitBlobSha1?.Trim() ?? string.Empty;
        _index = new Lazy<Task<FixtureIndex>>(LoadIndexAsync, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    private readonly string _expectedReadmeGitBlobSha1;

    public async ValueTask PrefetchAsync(
        IReadOnlyList<string> sources,
        MarkdownImageResolveContext context,
        CancellationToken cancellationToken)
    {
        // The ordinary resolver prefetches online bytes. Same-byte mode must
        // never reach that path, even speculatively.
        try
        {
            _ = await _index.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsFixtureFailure(exception))
        {
            // Prefetch is advisory. The authoritative resolution returns a
            // handled, unavailable result and still cannot use a network path.
        }
    }

    public async ValueTask<MarkdownImageResolution> ResolveAsync(
        string source,
        MarkdownImageResolveContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        FixtureIndex index;
        try
        {
            index = await _index.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsFixtureFailure(exception))
        {
            return MarkdownImageResolution.Blocked(MarkdownImageUnavailableReason.Unavailable);
        }
        IReadOnlyList<string> sourceHashes;
        try
        {
            sourceHashes = GetSourceUrlHashes(source, context);
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException)
        {
            return MarkdownImageResolution.Blocked(MarkdownImageUnavailableReason.Offline);
        }

        foreach (string urlHash in sourceHashes)
        {
            if (!index.AssetsByUrlHash.TryGetValue(urlHash, out FixtureAsset? asset))
                continue;

            try
            {
                byte[] bytes = await ReadVerifiedAssetAsync(asset, cancellationToken).ConfigureAwait(false);
                string contentType = asset.MimeType.Equals("application/octet-stream", StringComparison.OrdinalIgnoreCase)
                    ? SniffImageMime(bytes)
                    : asset.MimeType;
                return MarkdownImageResolution.Resolved(new MarkdownImageAsset(
                    bytes,
                    contentType,
                    new Uri($"same-byte://sha256/{asset.Sha256}", UriKind.Absolute),
                    $"same-byte:{asset.Sha256}"));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (IOException)
            {
                return MarkdownImageResolution.Blocked(MarkdownImageUnavailableReason.Unavailable);
            }
            catch (UnauthorizedAccessException)
            {
                return MarkdownImageResolution.Blocked(MarkdownImageUnavailableReason.Unavailable);
            }
            catch (InvalidDataException)
            {
                return MarkdownImageResolution.Blocked(MarkdownImageUnavailableReason.Unavailable);
            }
        }

        return MarkdownImageResolution.Blocked(MarkdownImageUnavailableReason.Offline);
    }

    /// <summary>
    /// Supplies the exact captured README bytes to the audit's production
    /// repository path. The app must not render independently fetched or
    /// server-rendered text while claiming a same-byte comparison.
    /// </summary>
    internal async ValueTask<byte[]> LoadPinnedReadmeBytesAsync(
        string expectedPath,
        CancellationToken cancellationToken)
    {
        FixtureIndex index = await _index.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(expectedPath) ||
            !string.Equals(index.ReadmePath, expectedPath.Replace('\\', '/'), StringComparison.Ordinal))
        {
            throw new InvalidDataException("Same-byte fixture README path does not match the opened repository file.");
        }

        byte[] bytes = await ReadBoundedFileAsync(
            Path.Combine(_root, "readme.md"), MaximumReadmeBytes, cancellationToken).ConfigureAwait(false);
        if (bytes.LongLength != index.ReadmeByteSize ||
            !string.Equals(
                Convert.ToHexString(SHA256.HashData(bytes)),
                index.ReadmeSha256,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Same-byte fixture README changed after its manifest was admitted.");
        }
        return bytes;
    }

    internal async ValueTask<string> GetPinnedReadmePathAsync(CancellationToken cancellationToken)
    {
        FixtureIndex index = await _index.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
        return index.ReadmePath;
    }

    private async Task<FixtureIndex> LoadIndexAsync()
    {
        string manifestPath = Path.Combine(_root, "manifest.json");
        byte[] manifestBytes = await ReadBoundedFileAsync(manifestPath, MaximumManifestBytes)
            .ConfigureAwait(false);
        SameByteManifest manifest = JsonSerializer.Deserialize(
            manifestBytes,
            SameByteFixtureJsonContext.Default.SameByteManifest)
            ?? throw new InvalidDataException("Same-byte fixture manifest is empty.");

        if (manifest.Repository is null || manifest.Readme is null || manifest.Assets is null ||
            manifest.BrowserRender is null || manifest.SchemaVersion != 2 || !manifest.Complete ||
            !string.Equals(manifest.Repository.FullName, _expectedRepository, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(manifest.Repository.CommitSha, _expectedCommit, StringComparison.OrdinalIgnoreCase) ||
            manifest.Readme.ByteSize < 0 ||
            manifest.Readme.ByteSize > MaximumReadmeBytes ||
            !string.Equals(
                manifest.Repository.ReadmeGitBlobSha1,
                _expectedReadmeGitBlobSha1,
                StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(manifest.Repository.ReadmePath) ||
            !string.Equals(manifest.Readme.File, "readme.md", StringComparison.Ordinal) ||
            !IsSha256(manifest.Readme.Sha256) ||
            !IsValidBrowserRender(manifest.BrowserRender) ||
            manifest.Assets.Count > 100_000)
        {
            throw new InvalidDataException("Same-byte fixture does not match this repository audit request.");
        }

        await ValidateReadmeAsync(manifest.Readme).ConfigureAwait(false);

        var assetsByUrlHash = new Dictionary<string, FixtureAsset>(StringComparer.Ordinal);
        var assetSizes = new Dictionary<string, int>(StringComparer.Ordinal);
        long aggregateBytes = 0;
        foreach (SameByteAssetEntry entry in manifest.Assets)
        {
            if (entry is null || !IsSha256(entry.UrlSha256) || !IsSha256(entry.Sha256) ||
                entry.ByteSize <= 0 || entry.ByteSize > MaximumAssetBytes ||
                !(IsImageMime(entry.MimeType) ||
                  string.Equals(entry.MimeType, "application/octet-stream", StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidDataException("Same-byte fixture contains an invalid asset entry.");
            }

            if (assetSizes.TryGetValue(entry.Sha256, out int previousSize))
            {
                if (previousSize != entry.ByteSize)
                    throw new InvalidDataException("Same-byte fixture maps an asset hash to conflicting sizes.");
            }
            else
            {
                assetSizes.Add(entry.Sha256, entry.ByteSize);
                aggregateBytes = checked(aggregateBytes + entry.ByteSize);
                if (aggregateBytes > MaximumAggregateAssetBytes)
                    throw new InvalidDataException("Same-byte fixture exceeds the aggregate asset budget.");
            }

            var asset = new FixtureAsset(entry.Sha256, entry.ByteSize, entry.MimeType);
            if (!assetsByUrlHash.TryAdd(entry.UrlSha256, asset) &&
                assetsByUrlHash[entry.UrlSha256] != asset)
            {
                throw new InvalidDataException("Same-byte fixture maps one source key to different assets.");
            }
        }

        return new FixtureIndex(
            manifest.Repository.ReadmePath,
            manifest.Readme.ByteSize,
            manifest.Readme.Sha256,
            assetsByUrlHash);
    }

    private static async Task<byte[]> ReadBoundedFileAsync(
        string path,
        long maximumBytes,
        CancellationToken cancellationToken = default)
    {
        FileInfo info = new(path);
        EnsureRegularFile(info, maximumBytes);
        int expectedLength = checked((int)info.Length);
        await using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 16 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length != expectedLength)
            throw new InvalidDataException("Same-byte fixture file changed while it was opened.");

        byte[] bytes = new byte[expectedLength];
        await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        if (stream.ReadByte() != -1)
            throw new InvalidDataException("Same-byte fixture file exceeded its bounded length.");
        return bytes;
    }

    private async Task ValidateReadmeAsync(SameByteReadme readme)
    {
        string readmePath = Path.Combine(_root, "readme.md");
        FileInfo info = new(readmePath);
        EnsureRegularFile(info, MaximumReadmeBytes);
        if (info.Length != readme.ByteSize)
            throw new InvalidDataException("Same-byte fixture README length does not match its manifest.");

        using IncrementalHash gitBlobHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        using IncrementalHash contentHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] prefix = Encoding.ASCII.GetBytes($"blob {readme.ByteSize.ToString(CultureInfo.InvariantCulture)}\0");
        gitBlobHash.AppendData(prefix);
        await using FileStream stream = new(
            readmePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        byte[] buffer = new byte[64 * 1024];
        long total = 0;
        while (true)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(), CancellationToken.None).ConfigureAwait(false);
            if (read == 0)
                break;
            total = checked(total + read);
            if (total > MaximumReadmeBytes || total > readme.ByteSize)
                throw new InvalidDataException("Same-byte fixture README exceeded its pinned size.");
            gitBlobHash.AppendData(buffer, 0, read);
            contentHash.AppendData(buffer, 0, read);
        }

        string gitBlobSha1 = Convert.ToHexString(gitBlobHash.GetHashAndReset());
        string sha256 = Convert.ToHexString(contentHash.GetHashAndReset());
        if (total != readme.ByteSize ||
            !string.Equals(gitBlobSha1, _expectedReadmeGitBlobSha1, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(sha256, readme.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Same-byte fixture README failed its pinned hash checks.");
        }
    }

    private async Task<byte[]> ReadVerifiedAssetAsync(
        FixtureAsset asset,
        CancellationToken cancellationToken)
    {
        string assetsDirectory = Path.Combine(_root, "assets");
        FileAttributes directoryAttributes = File.GetAttributes(assetsDirectory);
        if ((directoryAttributes & FileAttributes.Directory) == 0 ||
            (directoryAttributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("Same-byte fixture asset directory is not a regular directory.");
        }

        string path = Path.Combine(assetsDirectory, asset.Sha256);
        EnsureRegularFile(new FileInfo(path), MaximumAssetBytes);
        await using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length != asset.ByteSize)
            throw new InvalidDataException("Same-byte fixture asset length changed after capture.");

        byte[] bytes = new byte[asset.ByteSize];
        await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        if (stream.ReadByte() != -1 ||
            !string.Equals(Convert.ToHexString(SHA256.HashData(bytes)), asset.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Same-byte fixture asset hash does not match its content address.");
        }
        return bytes;
    }

    private static void EnsureRegularFile(FileInfo info, long maximumBytes)
    {
        info.Refresh();
        if (!info.Exists || info.Length < 0 || info.Length > maximumBytes ||
            (info.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("Same-byte fixture file is missing, oversized, or redirected.");
        }
    }

    private static IReadOnlyList<string> GetSourceUrlHashes(
        string source,
        MarkdownImageResolveContext context)
    {
        var hashes = new HashSet<string>(StringComparer.Ordinal);
        AddUrlHash(source, hashes);
        if (Uri.TryCreate(source, UriKind.RelativeOrAbsolute, out Uri? parsed) &&
            !parsed.IsAbsoluteUri && context.BaseUri is not null &&
            Uri.TryCreate(context.BaseUri, parsed, out Uri? based))
        {
            AddUrlHash(based.AbsoluteUri, hashes);
        }

        GitHubMarkdownImageReference reference;
        bool resolved = context.DocumentSource is not null
            ? GitHubMarkdownImageUrlResolver.TryResolve(source, context.DocumentSource, out reference)
            : GitHubMarkdownImageUrlResolver.TryResolve(
                source,
                context.BaseUri,
                context.DocumentPath,
                out reference);
        if (resolved)
        {
            AddUrlHash(reference.SourceUri.AbsoluteUri, hashes);
            AddUrlHash(GitHubMarkdownImageUrlResolver.CreateRawUri(reference).AbsoluteUri, hashes);
        }
        return hashes.ToArray();
    }

    private static void AddUrlHash(string? source, HashSet<string> hashes)
    {
        if (!Uri.TryCreate(source, UriKind.Absolute, out Uri? uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
            uri.UserInfo.Length > 0)
        {
            return;
        }

        var builder = new UriBuilder(uri) { Fragment = string.Empty };
        string[] segments = builder.Path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (builder.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) &&
            segments.Length >= 5 &&
            (segments[2].Equals("blob", StringComparison.OrdinalIgnoreCase) ||
             segments[2].Equals("raw", StringComparison.OrdinalIgnoreCase)) &&
            builder.Query.TrimStart('?').Equals("raw=true", StringComparison.Ordinal))
        {
            builder.Query = string.Empty;
        }

        hashes.Add(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.Uri.AbsoluteUri)))
            .ToLowerInvariant());
    }

    private static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(character => char.IsAsciiHexDigit(character));

    private static bool IsValidBrowserRender(SameByteBrowserRender render) =>
        render.Status switch
        {
            "passed" =>
                string.Equals(render.File, "rendered.html", StringComparison.Ordinal) &&
                render.ByteSize is > 0 and <= MaximumBrowserRenderBytes &&
                IsSha256(render.Sha256),
            "not-applicable" =>
                string.Equals(render.Reason, "github-source-view", StringComparison.Ordinal) &&
                string.IsNullOrEmpty(render.File) && render.ByteSize == 0 &&
                string.IsNullOrEmpty(render.Sha256),
            _ => false,
        };

    private static bool IsImageMime(string value)
    {
        if (value is null || !value.StartsWith("image/", StringComparison.OrdinalIgnoreCase) || value.Length > 128)
            return false;
        return value.AsSpan("image/".Length).ToString().All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '.' or '+' or '-');
    }

    private static string SniffImageMime(ReadOnlySpan<byte> bytes)
    {
        // Edge can deliver image bytes with the generic octet-stream header.
        // Admit that declaration only after the content-addressed bytes prove
        // a supported image type; never pass a generic binary MIME onward.
        if (bytes.Length >= 8 && bytes[0] == 137 && bytes[1] == 80 &&
            bytes[2] == 78 && bytes[3] == 71 && bytes[4] == 13 &&
            bytes[5] == 10 && bytes[6] == 26 && bytes[7] == 10)
            return "image/png";
        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
            return "image/jpeg";
        if (bytes.StartsWith("GIF87a"u8) || bytes.StartsWith("GIF89a"u8))
            return "image/gif";
        if (bytes.Length >= 12 && bytes.StartsWith("RIFF"u8) &&
            bytes.Slice(8, 4).SequenceEqual("WEBP"u8))
            return "image/webp";
        if (bytes.StartsWith("BM"u8))
            return "image/bmp";
        if (bytes.Length >= 4 && bytes[0] == 0 && bytes[1] == 0 &&
            bytes[2] == 1 && bytes[3] == 0)
            return "image/x-icon";
        if (bytes.Length >= 12 && bytes.Slice(4, 4).SequenceEqual("ftyp"u8) &&
            (bytes.Slice(8, 4).SequenceEqual("avif"u8) ||
             bytes.Slice(8, 4).SequenceEqual("avis"u8)))
            return "image/avif";
        ReadOnlySpan<byte> prefix = bytes[..Math.Min(bytes.Length, 1_024)];
        if (prefix.IndexOf("<svg"u8) >= 0)
            return "image/svg+xml";

        throw new InvalidDataException("Generic binary fixture content is not a supported image.");
    }

    private static bool IsFixtureFailure(Exception exception) =>
        exception is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or
            ArgumentException or NotSupportedException or OverflowException;

    private sealed record FixtureIndex(
        string ReadmePath,
        long ReadmeByteSize,
        string ReadmeSha256,
        IReadOnlyDictionary<string, FixtureAsset> AssetsByUrlHash);

    private sealed record FixtureAsset(string Sha256, int ByteSize, string MimeType);

    private sealed class SameByteManifest
    {
        public int SchemaVersion { get; init; }
        public bool Complete { get; init; }
        public SameByteRepository Repository { get; init; } = new();
        public SameByteReadme Readme { get; init; } = new();
        public SameByteBrowserRender BrowserRender { get; init; } = new();
        public List<SameByteAssetEntry> Assets { get; init; } = [];
    }

    private sealed class SameByteRepository
    {
        public string FullName { get; init; } = string.Empty;
        public string CommitSha { get; init; } = string.Empty;
        public string ReadmePath { get; init; } = string.Empty;
        public string ReadmeGitBlobSha1 { get; init; } = string.Empty;
    }

    private sealed class SameByteReadme
    {
        public string File { get; init; } = string.Empty;
        public long ByteSize { get; init; }
        public string Sha256 { get; init; } = string.Empty;
    }

    private sealed class SameByteBrowserRender
    {
        public string Status { get; init; } = string.Empty;
        public string? Reason { get; init; }
        public string? File { get; init; }
        public long ByteSize { get; init; }
        public string? Sha256 { get; init; }
    }

    private sealed class SameByteAssetEntry
    {
        public string UrlSha256 { get; init; } = string.Empty;
        public string Sha256 { get; init; } = string.Empty;
        public int ByteSize { get; init; }
        public string MimeType { get; init; } = string.Empty;
    }

    [JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
    [JsonSerializable(typeof(SameByteManifest))]
    private sealed partial class SameByteFixtureJsonContext : JsonSerializerContext
    {
    }
}
