using System;
using System.Collections.Generic;
using System.Diagnostics;
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
using MarkdownRenderer.Performance;

namespace JitHub.Services.Markdown;

/// <summary>
/// Resolves images only from a pinned same-byte audit fixture. It has no online fallback.
/// This resolver is constructed only by the explicit production README audit launch mode.
/// </summary>
internal sealed partial class MarkdownSameByteAuditImageResolver :
    IMarkdownImageResolver,
    IMarkdownLeasedImageResolver,
    IMarkdownImagePrefetcher
{
    internal const int CorrectnessInputSchemaVersion = 3;
    internal const string CorrectnessInputKind = "correctness-input-v1";
    private const long MaximumManifestBytes = 4 * 1024 * 1024;
    private const long MaximumReadmeBytes = 16 * 1024 * 1024;
    private const long MaximumBrowserRenderBytes = 32 * 1024 * 1024;
    private const int MaximumAssetBytes = 64 * 1024 * 1024;
    private const int MaximumSynchronousAssetReadBytes = 8 * 1024 * 1024;
    private const long MaximumAggregateAssetBytes = 256 * 1024 * 1024;
    // Browser-free inputs are local, hash-verified files; image-heavy READMEs
    // legitimately exceed the capture-era budget. App admission still applies.
    private const long MaximumCorrectnessInputAggregateAssetBytes = 1024L * 1024 * 1024;
    private const int MaximumNonRenderedDecorativeImages = 128;
    private const int MaximumDecorativeImageUrlHashes = 16;
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

    public ValueTask<MarkdownImageResolution> ResolveAsync(
        string source,
        MarkdownImageResolveContext context,
        CancellationToken cancellationToken) =>
        ResolveLegacyCoreAsync(source, context, cancellationToken, diagnostics: null);

    /// <summary>
    /// Resolves only a captured same-byte fixture asset, reserving its manifest-validated
    /// byte length before allocating, reading, or hashing the source buffer.
    /// </summary>
    /// <remarks>
    /// The returned lease owns the reservation until its final retained handle is disposed.
    /// No network fallback is attempted. Fixture cache/index metadata is separate from this
    /// per-resolution source-byte reservation.
    /// </remarks>
    public async ValueTask<MarkdownImageResolutionLease> ResolveWithSourceLeaseAsync(
        string source,
        MarkdownImageResolveContext context,
        IMarkdownImageSourceByteAdmission admission,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(admission);
        CoreResolution result = await ResolveCoreAsync(
            source, context, admission, cancellationToken, diagnostics: null).ConfigureAwait(false);
        return CreateLease(result);
    }

    internal async ValueTask<MarkdownSameByteImageResolutionDiagnostic> ResolveWithStageDiagnosticsAsync(
        string source,
        MarkdownImageResolveContext context,
        long correlationId,
        CancellationToken cancellationToken)
    {
        var diagnostics = new ResolverStageRecorder(correlationId);
        CoreResolution result = await ResolveCoreAsync(
            source, context, null, cancellationToken, diagnostics).ConfigureAwait(false);
        return new MarkdownSameByteImageResolutionDiagnostic(result.Resolution, diagnostics.Snapshot());
    }

    /// <summary>
    /// Resolves a fixture asset with stage evidence while preserving the source reservation
    /// in the returned lease for the full duration of audit-side consumption.
    /// </summary>
    internal async ValueTask<MarkdownSameByteLeasedImageResolutionDiagnostic> ResolveWithLeasedStageDiagnosticsAsync(
        string source,
        MarkdownImageResolveContext context,
        long correlationId,
        IMarkdownImageSourceByteAdmission admission,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(admission);
        var diagnostics = new ResolverStageRecorder(correlationId);
        CoreResolution result = await ResolveCoreAsync(
            source, context, admission, cancellationToken, diagnostics).ConfigureAwait(false);
        MarkdownImageResolutionLease? lease = CreateLease(result);
        try
        {
            var diagnostic = new MarkdownSameByteLeasedImageResolutionDiagnostic(
                lease,
                diagnostics.Snapshot());
            lease = null;
            return diagnostic;
        }
        finally
        {
            lease?.Dispose();
        }
    }

    private async ValueTask<MarkdownImageResolution> ResolveLegacyCoreAsync(
        string source,
        MarkdownImageResolveContext context,
        CancellationToken cancellationToken,
        ResolverStageRecorder? diagnostics)
    {
        CoreResolution result = await ResolveCoreAsync(
            source, context, null, cancellationToken, diagnostics).ConfigureAwait(false);
        return result.Resolution;
    }

    private static MarkdownImageResolutionLease CreateLease(CoreResolution result)
    {
        // MarkdownImageResolutionLease takes ownership only after its constructor succeeds.
        // Keep an explicit rollback here so even validation/allocation failures release quota.
        try
        {
            return new MarkdownImageResolutionLease(result.Resolution, result.SourceReservation);
        }
        catch
        {
            result.SourceReservation?.Dispose();
            throw;
        }
    }

    private async ValueTask<CoreResolution> ResolveCoreAsync(
        string source,
        MarkdownImageResolveContext context,
        IMarkdownImageSourceByteAdmission? admission,
        CancellationToken cancellationToken,
        ResolverStageRecorder? diagnostics)
    {
        cancellationToken.ThrowIfCancellationRequested();
        diagnostics?.SetIndexWasCreatedAtStart(_index.IsValueCreated);
        FixtureIndex index;
        long indexWaitStarted = diagnostics is null ? 0 : Stopwatch.GetTimestamp();
        try
        {
            Task<FixtureIndex> indexTask = _index.Value;
            diagnostics?.SetIndexWasCompletedBeforeAwait(indexTask.IsCompleted);
            index = await indexTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsFixtureFailure(exception))
        {
            diagnostics?.SetFailure("index-wait");
            return new CoreResolution(
                MarkdownImageResolution.Blocked(MarkdownImageUnavailableReason.Unavailable), null);
        }
        finally
        {
            if (diagnostics is not null)
                diagnostics.SetIndexWaitMilliseconds(Stopwatch.GetElapsedTime(indexWaitStarted).TotalMilliseconds);
        }

        IReadOnlyList<string> sourceHashes;
        diagnostics?.SetCurrentStage("source-hash-lookup");
        long sourceHashStarted = diagnostics is null ? 0 : Stopwatch.GetTimestamp();
        try
        {
            sourceHashes = GetSourceUrlHashes(source, context);
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException)
        {
            diagnostics?.SetFailure("source-hash-lookup");
            return new CoreResolution(
                MarkdownImageResolution.Blocked(MarkdownImageUnavailableReason.Offline), null);
        }
        finally
        {
            if (diagnostics is not null)
                diagnostics.SetSourceHashLookupMilliseconds(Stopwatch.GetElapsedTime(sourceHashStarted).TotalMilliseconds);
        }

        diagnostics?.SetSourceHashCandidateCount(sourceHashes.Count);
        diagnostics?.SetCurrentStage("source-hash-index-lookup");
        long sourceIndexLookupStarted = diagnostics is null ? 0 : Stopwatch.GetTimestamp();
        FixtureAsset? matchedAsset = null;
        foreach (string urlHash in sourceHashes)
        {
            if (index.AssetsByUrlHash.TryGetValue(urlHash, out FixtureAsset? asset))
            {
                matchedAsset = asset;
                break;
            }
        }
        if (diagnostics is not null)
            diagnostics.SetSourceHashIndexLookupMilliseconds(Stopwatch.GetElapsedTime(sourceIndexLookupStarted).TotalMilliseconds);

        if (matchedAsset is null)
        {
            // A source that the captured browser also failed to decode is not an
            // offline-policy rejection. Preserve the failure (and its diagnostic)
            // without opening the app's offline banner and changing the viewport.
            // This does not hide an image: ImageBox independently decides whether
            // an empty-alt, unsized failed image has any visible fallback.
            bool capturedFailure = sourceHashes.Any(index.FailedDecorativeSourceHashes.Contains);
            return new CoreResolution(
                MarkdownImageResolution.Blocked(capturedFailure
                    ? MarkdownImageUnavailableReason.Unavailable
                    : MarkdownImageUnavailableReason.Offline), null);
        }

        try
        {
            VerifiedAssetRead verified = await ReadVerifiedAssetAsync(
                matchedAsset, admission, cancellationToken, diagnostics).ConfigureAwait(false);
            IDisposable? sourceReservation = verified.SourceReservation;
            try
            {
                diagnostics?.SetCurrentStage("content-type-validation");
                long contentTypeStarted = diagnostics is null ? 0 : Stopwatch.GetTimestamp();
                string contentType;
                try
                {
                    contentType = matchedAsset.MimeType.Equals("application/octet-stream", StringComparison.OrdinalIgnoreCase)
                        ? SniffImageMime(verified.Bytes)
                        : matchedAsset.MimeType;
                }
                finally
                {
                    if (diagnostics is not null)
                        diagnostics.SetContentTypeValidationMilliseconds(Stopwatch.GetElapsedTime(contentTypeStarted).TotalMilliseconds);
                }
                var result = new CoreResolution(MarkdownImageResolution.Resolved(new MarkdownImageAsset(
                    verified.Bytes,
                    contentType,
                    new Uri($"same-byte://sha256/{matchedAsset.Sha256}", UriKind.Absolute),
                    $"same-byte:{matchedAsset.Sha256}")), sourceReservation);
                sourceReservation = null;
                return result;
            }
            finally
            {
                sourceReservation?.Dispose();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (IOException)
        {
            diagnostics?.SetFailure(diagnostics.CurrentStage);
            return new CoreResolution(
                MarkdownImageResolution.Blocked(MarkdownImageUnavailableReason.Unavailable), null);
        }
        catch (UnauthorizedAccessException)
        {
            diagnostics?.SetFailure(diagnostics.CurrentStage);
            return new CoreResolution(
                MarkdownImageResolution.Blocked(MarkdownImageUnavailableReason.Unavailable), null);
        }
        catch (InvalidDataException)
        {
            diagnostics?.SetFailure(diagnostics.CurrentStage);
            return new CoreResolution(
                MarkdownImageResolution.Blocked(MarkdownImageUnavailableReason.Unavailable), null);
        }
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

        // Schema 3 (correctness-input-v1) carries the same README, asset and
        // limit guarantees without browser capture; it must not claim any.
        bool correctnessInput = manifest.SchemaVersion == CorrectnessInputSchemaVersion &&
            string.Equals(manifest.Kind, CorrectnessInputKind, StringComparison.Ordinal);
        if (manifest.Repository is null || manifest.Readme is null || manifest.Assets is null ||
            (!correctnessInput && manifest.BrowserRender is null) ||
            !(manifest.SchemaVersion == 2 || correctnessInput) || !manifest.Complete ||
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
            (correctnessInput
                ? manifest.BrowserRender is not null
                : !IsValidBrowserRender(manifest.BrowserRender!)) ||
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
                if (aggregateBytes > (correctnessInput ? MaximumCorrectnessInputAggregateAssetBytes : MaximumAggregateAssetBytes))
                    throw new InvalidDataException("Same-byte fixture exceeds the aggregate asset budget.");
            }

            var asset = new FixtureAsset(entry.Sha256, entry.ByteSize, entry.MimeType);
            if (!assetsByUrlHash.TryAdd(entry.UrlSha256, asset) &&
                assetsByUrlHash[entry.UrlSha256] != asset)
            {
                throw new InvalidDataException("Same-byte fixture maps one source key to different assets.");
            }
        }

        HashSet<string> failedDecorativeSourceHashes = correctnessInput
            ? ReadCorrectnessInputDecorativeSourceHashes(manifest)
            : ReadFailedDecorativeSourceHashes(manifest);
        return new FixtureIndex(
            manifest.Repository.ReadmePath,
            manifest.Readme.ByteSize,
            manifest.Readme.Sha256,
            assetsByUrlHash,
            failedDecorativeSourceHashes);
    }

    private static HashSet<string> ReadCorrectnessInputDecorativeSourceHashes(SameByteManifest manifest)
    {
        // Decorative-failure exclusions are browser evidence. A browser-free
        // input cannot prove one, so any claim is rejected and a failed image
        // remains an ordinary miss classified by the input's imageDemand.
        if (manifest.NonRenderedDecorativeImageExclusions is not { Count: 0 })
            throw new InvalidDataException("A correctness input cannot carry decorative-image browser evidence.");
        return new HashSet<string>(StringComparer.Ordinal);
    }

    private static HashSet<string> ReadFailedDecorativeSourceHashes(SameByteManifest manifest)
    {
        // Match the bounded evidence contract in same-byte-corpus.mjs. Never
        // treat a missing, unproven or source-view omission as a captured failure.
        List<SameByteNonRenderedDecorativeImage>? entries = manifest.NonRenderedDecorativeImageExclusions;
        if (entries is null || entries.Count > MaximumNonRenderedDecorativeImages ||
            (entries.Count != 0 && manifest.BrowserRender.Status != "passed"))
            throw new InvalidDataException("Same-byte fixture has invalid decorative-image failure evidence.");

        var hashes = new HashSet<string>(StringComparer.Ordinal);
        foreach (SameByteNonRenderedDecorativeImage entry in entries)
        {
            if (entry is null || !entry.ExplicitEmptyAlt || !entry.FailedDecode || !entry.ZeroBounds ||
                entry.UrlSha256s is not { Count: > 0 and <= MaximumDecorativeImageUrlHashes })
                throw new InvalidDataException("Same-byte fixture has unproven decorative-image failure evidence.");

            var entryHashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string hash in entry.UrlSha256s)
            {
                if (!IsSha256(hash) || !entryHashes.Add(hash))
                    throw new InvalidDataException("Same-byte fixture has invalid decorative-image source identities.");
                hashes.Add(hash.ToLowerInvariant());
            }
        }
        return hashes;
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

    private async Task<VerifiedAssetRead> ReadVerifiedAssetAsync(
        FixtureAsset asset,
        IMarkdownImageSourceByteAdmission? admission,
        CancellationToken cancellationToken,
        ResolverStageRecorder? diagnostics)
    {
        string assetsDirectory = Path.Combine(_root, "assets");
        string path = Path.Combine(assetsDirectory, asset.Sha256);
        long stageStarted = diagnostics is null ? 0 : Stopwatch.GetTimestamp();
        try
        {
            diagnostics?.SetCurrentStage("asset-stat");
            FileAttributes directoryAttributes = File.GetAttributes(assetsDirectory);
            if ((directoryAttributes & FileAttributes.Directory) == 0 ||
                (directoryAttributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException("Same-byte fixture asset directory is not a regular directory.");
            }

            var assetInfo = new FileInfo(path);
            EnsureRegularFile(assetInfo, MaximumAssetBytes);
            if (assetInfo.Length != asset.ByteSize)
                throw new InvalidDataException("Same-byte fixture asset length changed after capture.");
        }
        finally
        {
            if (diagnostics is not null)
                diagnostics.SetAssetStatMilliseconds(Stopwatch.GetElapsedTime(stageStarted).TotalMilliseconds);
        }

        bool synchronousRead = ShouldUseSynchronousAssetRead(asset.ByteSize);
        FileOptions fileOptions = FileOptions.SequentialScan;
        if (!synchronousRead)
            fileOptions |= FileOptions.Asynchronous;

        IDisposable? sourceReservation = null;
        try
        {
            if (admission is not null)
            {
                diagnostics?.SetCurrentStage("source-byte-admission");
                sourceReservation = await admission.ReserveAsync(asset.ByteSize, cancellationToken)
                    .ConfigureAwait(false)
                    ?? throw new InvalidOperationException("Source-byte admission returned no reservation.");
            }
            cancellationToken.ThrowIfCancellationRequested();

            FileStream stream;
            stageStarted = diagnostics is null ? 0 : Stopwatch.GetTimestamp();
            try
            {
                diagnostics?.SetCurrentStage("asset-open");
                stream = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    bufferSize: 64 * 1024,
                    fileOptions);
            }
            finally
            {
                if (diagnostics is not null)
                    diagnostics.SetAssetOpenMilliseconds(Stopwatch.GetElapsedTime(stageStarted).TotalMilliseconds);
            }

            byte[] bytes;
            try
            {
                stageStarted = diagnostics is null ? 0 : Stopwatch.GetTimestamp();
                try
                {
                    diagnostics?.SetCurrentStage("asset-length-check");
                    if (stream.Length != asset.ByteSize)
                        throw new InvalidDataException("Same-byte fixture asset length changed after capture.");
                }
                finally
                {
                    if (diagnostics is not null)
                        diagnostics.SetAssetLengthCheckMilliseconds(Stopwatch.GetElapsedTime(stageStarted).TotalMilliseconds);
                }

                cancellationToken.ThrowIfCancellationRequested();

                stageStarted = diagnostics is null ? 0 : Stopwatch.GetTimestamp();
                try
                {
                    diagnostics?.SetCurrentStage("asset-buffer-allocation");
                    bytes = new byte[asset.ByteSize];
                }
                finally
                {
                    if (diagnostics is not null)
                        diagnostics.SetAssetBufferAllocationMilliseconds(Stopwatch.GetElapsedTime(stageStarted).TotalMilliseconds);
                }

                stageStarted = diagnostics is null ? 0 : Stopwatch.GetTimestamp();
                try
                {
                    long readExactlyStarted = diagnostics is null ? 0 : Stopwatch.GetTimestamp();
                    try
                    {
                        diagnostics?.SetCurrentStage(synchronousRead
                            ? "asset-read-exactly-sync"
                            : "asset-read-exactly-async");
                        if (synchronousRead)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            stream.ReadExactly(bytes);
                            cancellationToken.ThrowIfCancellationRequested();
                        }
                        else
                        {
                            await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
                        }
                    }
                    finally
                    {
                        if (diagnostics is not null)
                        {
                            diagnostics.SetAssetReadExactlyMilliseconds(
                                Stopwatch.GetElapsedTime(readExactlyStarted).TotalMilliseconds);
                        }
                    }

                    long eofProbeStarted = diagnostics is null ? 0 : Stopwatch.GetTimestamp();
                    try
                    {
                        diagnostics?.SetCurrentStage("asset-eof-probe");
                        if (stream.ReadByte() != -1)
                            throw new InvalidDataException("Same-byte fixture asset exceeded its bounded length.");
                    }
                    finally
                    {
                        if (diagnostics is not null)
                            diagnostics.SetAssetEofProbeMilliseconds(Stopwatch.GetElapsedTime(eofProbeStarted).TotalMilliseconds);
                    }
                }
                finally
                {
                    if (diagnostics is not null)
                        diagnostics.SetAssetReadMilliseconds(Stopwatch.GetElapsedTime(stageStarted).TotalMilliseconds);
                }

                stageStarted = diagnostics is null ? 0 : Stopwatch.GetTimestamp();
                try
                {
                    diagnostics?.SetCurrentStage("asset-sha256-verification");
                    if (!string.Equals(Convert.ToHexString(SHA256.HashData(bytes)), asset.Sha256, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("Same-byte fixture asset hash does not match its content address.");
                    cancellationToken.ThrowIfCancellationRequested();
                }
                finally
                {
                    if (diagnostics is not null)
                        diagnostics.SetAssetSha256VerificationMilliseconds(Stopwatch.GetElapsedTime(stageStarted).TotalMilliseconds);
                }
            }
            catch
            {
                // Preserve the stage that actually failed before cleanup replaces it.
                diagnostics?.SetFailure(diagnostics.CurrentStage);
                throw;
            }
            finally
            {
                stageStarted = diagnostics is null ? 0 : Stopwatch.GetTimestamp();
                try
                {
                    diagnostics?.SetCurrentStage("asset-stream-disposal");
                    if (synchronousRead)
                        stream.Dispose();
                    else
                        await stream.DisposeAsync().ConfigureAwait(false);
                }
                finally
                {
                    if (diagnostics is not null)
                        diagnostics.SetAssetDisposeMilliseconds(Stopwatch.GetElapsedTime(stageStarted).TotalMilliseconds);
                }
            }

            var result = new VerifiedAssetRead(bytes, sourceReservation);
            sourceReservation = null;
            return result;
        }
        finally
        {
            // A failed read, hash, cancellation, or stream disposal must release admission.
            sourceReservation?.Dispose();
        }
    }

    internal static bool ShouldUseSynchronousAssetRead(int byteSize) =>
        byteSize is > 0 and <= MaximumSynchronousAssetReadBytes;

    private static void EnsureRegularFile(FileInfo info, long maximumBytes)
    {
        info.Refresh();
        if (!info.Exists || info.Length < 0 || info.Length > maximumBytes ||
            (info.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("Same-byte fixture file is missing, oversized, or redirected.");
        }
    }

    private IReadOnlyList<string> GetSourceUrlHashes(
        string source,
        MarkdownImageResolveContext context) =>
        MarkdownAuditImageUrlCandidates.GetSourceUrlHashes(
            source,
            context.BaseUri,
            context.DocumentPath,
            context.DocumentSource,
            _expectedRepository,
            _expectedCommit);

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
        IReadOnlyDictionary<string, FixtureAsset> AssetsByUrlHash,
        IReadOnlySet<string> FailedDecorativeSourceHashes);

    private sealed record FixtureAsset(string Sha256, int ByteSize, string MimeType);

    private readonly record struct CoreResolution(
        MarkdownImageResolution Resolution,
        IDisposable? SourceReservation);

    private readonly record struct VerifiedAssetRead(
        byte[] Bytes,
        IDisposable? SourceReservation);

    private sealed class SameByteManifest
    {
        public int SchemaVersion { get; init; }
        public string? Kind { get; init; }
        public bool Complete { get; init; }
        public SameByteRepository Repository { get; init; } = new();
        public SameByteReadme Readme { get; init; } = new();
        public SameByteBrowserRender BrowserRender { get; init; } = new();
        public List<SameByteAssetEntry> Assets { get; init; } = [];
        // A setter preserves the absent-property default with source-generated
        // deserialization; an explicit JSON null must still fail validation.
        public List<SameByteNonRenderedDecorativeImage> NonRenderedDecorativeImageExclusions { get; set; } = [];
    }

    private sealed class SameByteNonRenderedDecorativeImage
    {
        public List<string> UrlSha256s { get; init; } = [];
        public bool ExplicitEmptyAlt { get; init; }
        public bool FailedDecode { get; init; }
        public bool ZeroBounds { get; init; }
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

    private sealed class ResolverStageRecorder(long correlationId)
    {
        public long CorrelationId { get; } = correlationId;
        public string CurrentStage { get; private set; } = "index-wait";
        private bool _indexTaskWasCreatedAtStart;
        private bool _indexTaskWasCompletedBeforeAwait;
        private int _sourceHashCandidateCount;
        private string? _failureStage;
        private double _indexWaitMilliseconds;
        private double _sourceHashLookupMilliseconds;
        private double _sourceHashIndexLookupMilliseconds;
        private double _assetStatMilliseconds;
        private double _assetOpenMilliseconds;
        private double _assetLengthCheckMilliseconds;
        private double _assetBufferAllocationMilliseconds;
        private double _assetReadMilliseconds;
        private double _assetReadExactlyMilliseconds;
        private double _assetEofProbeMilliseconds;
        private double _assetSha256VerificationMilliseconds;
        private double _assetDisposeMilliseconds;
        private double _contentTypeValidationMilliseconds;

        public void SetIndexWasCreatedAtStart(bool value) => _indexTaskWasCreatedAtStart = value;
        public void SetIndexWasCompletedBeforeAwait(bool value) => _indexTaskWasCompletedBeforeAwait = value;
        public void SetSourceHashCandidateCount(int value) => _sourceHashCandidateCount = value;
        public void SetCurrentStage(string value) => CurrentStage = value;
        public void SetFailure(string? stage) => _failureStage ??= stage;
        public void SetIndexWaitMilliseconds(double value) => _indexWaitMilliseconds = value;
        public void SetSourceHashLookupMilliseconds(double value) => _sourceHashLookupMilliseconds = value;
        public void SetSourceHashIndexLookupMilliseconds(double value) => _sourceHashIndexLookupMilliseconds = value;
        public void SetAssetStatMilliseconds(double value) => _assetStatMilliseconds = value;
        public void SetAssetOpenMilliseconds(double value) => _assetOpenMilliseconds = value;
        public void SetAssetLengthCheckMilliseconds(double value) => _assetLengthCheckMilliseconds = value;
        public void SetAssetBufferAllocationMilliseconds(double value) => _assetBufferAllocationMilliseconds = value;
        public void SetAssetReadMilliseconds(double value) => _assetReadMilliseconds = value;
        public void SetAssetReadExactlyMilliseconds(double value) => _assetReadExactlyMilliseconds = value;
        public void SetAssetEofProbeMilliseconds(double value) => _assetEofProbeMilliseconds = value;
        public void SetAssetSha256VerificationMilliseconds(double value) => _assetSha256VerificationMilliseconds = value;
        public void SetAssetDisposeMilliseconds(double value) => _assetDisposeMilliseconds = value;
        public void SetContentTypeValidationMilliseconds(double value) => _contentTypeValidationMilliseconds = value;

        public MarkdownSameByteResolverStageEvidence Snapshot() => new(
            CorrelationId,
            _indexTaskWasCreatedAtStart,
            _indexTaskWasCompletedBeforeAwait,
            _indexWaitMilliseconds,
            _sourceHashLookupMilliseconds,
            _sourceHashCandidateCount,
            _sourceHashIndexLookupMilliseconds,
            _assetStatMilliseconds,
            _assetOpenMilliseconds,
            _assetLengthCheckMilliseconds,
            _assetBufferAllocationMilliseconds,
            _assetReadMilliseconds,
            _assetReadExactlyMilliseconds,
            _assetEofProbeMilliseconds,
            _assetSha256VerificationMilliseconds,
            _assetDisposeMilliseconds,
            _contentTypeValidationMilliseconds,
            _failureStage,
            UnattributedResolverMilliseconds: 0);
    }
}

internal sealed record MarkdownSameByteImageResolutionDiagnostic(
    MarkdownImageResolution Resolution,
    MarkdownSameByteResolverStageEvidence StageEvidence);

/// <summary>
/// Stage evidence paired with the same lease that owns the resolved fixture bytes.
/// The caller owns and must dispose <see cref="Lease"/> after its final byte access.
/// </summary>
internal sealed record MarkdownSameByteLeasedImageResolutionDiagnostic(
    MarkdownImageResolutionLease Lease,
    MarkdownSameByteResolverStageEvidence StageEvidence);

internal sealed record MarkdownSameByteResolverStageEvidence(
    long CorrelationId,
    bool IndexTaskWasCreatedAtStart,
    bool IndexTaskWasCompletedBeforeAwait,
    double IndexWaitMilliseconds,
    double SourceHashLookupMilliseconds,
    int SourceHashCandidateCount,
    double SourceHashIndexLookupMilliseconds,
    double AssetStatMilliseconds,
    double AssetOpenMilliseconds,
    double AssetLengthCheckMilliseconds,
    double AssetBufferAllocationMilliseconds,
    double AssetReadMilliseconds,
    double AssetReadExactlyMilliseconds,
    double AssetEofProbeMilliseconds,
    double AssetSha256VerificationMilliseconds,
    double AssetDisposeMilliseconds,
    double ContentTypeValidationMilliseconds,
    string? FailureStage,
    double UnattributedResolverMilliseconds);
