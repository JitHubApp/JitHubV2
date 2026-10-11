using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MarkdownRenderer.Images;
using MarkdownRenderer.Performance;

namespace JitHub.Services.Markdown;

/// <summary>
/// Records resolver outcomes only during an explicit audit or lifecycle test launch.
/// It never changes the wrapped resolver's result or error behavior.
/// </summary>
internal class MarkdownAuditImageResolver(IMarkdownImageResolver inner) :
    IMarkdownImageSourceByteAdmittedResolver,
    IMarkdownImagePrefetcher
{
    private const string SameByteStageTraceVariable = "JITHUB_MARKDOWN_SAMEBYTE_RESOLVER_STAGE_TRACE";
    private const string ImageResolutionEvidencePathVariable = "JITHUB_MARKDOWN_IMAGE_RESOLUTION_EVIDENCE_PATH";
    private const long MaximumSameByteStageTraceRequests = 64;
    private static long _sameByteStageCorrelationSequence;

    /// <summary>
    /// Creates an audit wrapper that preserves the optional source-lease capability only when
    /// the wrapped resolver explicitly provides it.
    /// </summary>
    internal static IMarkdownImageResolver Create(IMarkdownImageResolver inner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        return inner is IMarkdownLeasedImageResolver leased
            ? new LeasedMarkdownAuditImageResolver(leased)
            : new MarkdownAuditImageResolver(inner);
    }

    public ValueTask PrefetchAsync(
        IReadOnlyList<string> sources,
        MarkdownImageResolveContext context,
        CancellationToken cancellationToken) =>
        inner is IMarkdownImagePrefetcher prefetcher
            ? prefetcher.PrefetchAsync(sources, context, cancellationToken)
            : ValueTask.CompletedTask;

    public ValueTask<MarkdownImageResolution> ResolveAsync(
        string source,
        MarkdownImageResolveContext context,
        CancellationToken cancellationToken) =>
        ResolveCoreAsync(source, context, null, cancellationToken);

    public ValueTask<MarkdownImageResolution> ResolveWithSourceByteAdmissionAsync(
        string source,
        MarkdownImageResolveContext context,
        IMarkdownImageSourceByteAdmission admission,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(admission);
        return ResolveCoreAsync(source, context, admission, cancellationToken);
    }

    private async ValueTask<MarkdownImageResolution> ResolveCoreAsync(
        string source,
        MarkdownImageResolveContext context,
        IMarkdownImageSourceByteAdmission? admission,
        CancellationToken cancellationToken)
    {
        DateTimeOffset startedAt = DateTimeOffset.UtcNow;
        long started = Stopwatch.GetTimestamp();
        MarkdownImageResolution resolution;
        MarkdownSameByteResolverStageEvidence? stageEvidence = null;
        bool canTraceSameByte = admission is null &&
            inner is MarkdownSameByteAuditImageResolver &&
            MarkdownLifecycleAutomationBridge.IsEvidenceEnabled &&
            !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ImageResolutionEvidencePathVariable)) &&
            string.Equals(
                Environment.GetEnvironmentVariable(SameByteStageTraceVariable),
                "1",
                StringComparison.Ordinal);
        long correlationId = canTraceSameByte
            ? Interlocked.Increment(ref _sameByteStageCorrelationSequence)
            : 0;
        if (canTraceSameByte && correlationId is > 0 and <= MaximumSameByteStageTraceRequests &&
            inner is MarkdownSameByteAuditImageResolver sameByteResolver)
        {
            MarkdownSameByteImageResolutionDiagnostic diagnostic = await sameByteResolver
                .ResolveWithStageDiagnosticsAsync(source, context, correlationId, cancellationToken)
                .ConfigureAwait(false);
            resolution = diagnostic.Resolution;
            stageEvidence = diagnostic.StageEvidence;
        }
        else
        {
            resolution = admission is not null &&
                inner is IMarkdownImageSourceByteAdmittedResolver admitted
                ? await admitted.ResolveWithSourceByteAdmissionAsync(
                    source, context, admission, cancellationToken).ConfigureAwait(false)
                : await inner.ResolveAsync(
                    source, context, cancellationToken).ConfigureAwait(false);
        }
        double elapsedMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        if (stageEvidence is not null)
        {
            double measuredStageMilliseconds =
                stageEvidence.IndexWaitMilliseconds +
                stageEvidence.SourceHashLookupMilliseconds +
                stageEvidence.SourceHashIndexLookupMilliseconds +
                stageEvidence.AssetStatMilliseconds +
                stageEvidence.AssetOpenMilliseconds +
                stageEvidence.AssetLengthCheckMilliseconds +
                stageEvidence.AssetBufferAllocationMilliseconds +
                stageEvidence.AssetReadMilliseconds +
                stageEvidence.AssetSha256VerificationMilliseconds +
                stageEvidence.AssetDisposeMilliseconds +
                stageEvidence.ContentTypeValidationMilliseconds;
            stageEvidence = stageEvidence with
            {
                UnattributedResolverMilliseconds = elapsedMilliseconds - measuredStageMilliseconds,
            };
        }

        MarkdownLifecycleAutomationBridge.RecordImageResolution(
            source,
            resolution,
            startedAt,
            elapsedMilliseconds,
            stageEvidence,
            context);
        return resolution;
    }

    private sealed class LeasedMarkdownAuditImageResolver(
        IMarkdownLeasedImageResolver leasedInner) :
        MarkdownAuditImageResolver(leasedInner),
        IMarkdownLeasedImageResolver
    {
        public async ValueTask<MarkdownImageResolutionLease> ResolveWithSourceLeaseAsync(
            string source,
            MarkdownImageResolveContext context,
            IMarkdownImageSourceByteAdmission admission,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(admission);
            DateTimeOffset startedAt = DateTimeOffset.UtcNow;
            long started = Stopwatch.GetTimestamp();
            MarkdownImageResolutionLease? lease = null;
            try
            {
                bool canTraceSameByte = MarkdownLifecycleAutomationBridge.IsEvidenceEnabled &&
                    !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ImageResolutionEvidencePathVariable)) &&
                    string.Equals(
                        Environment.GetEnvironmentVariable(SameByteStageTraceVariable),
                        "1",
                        StringComparison.Ordinal);
                long correlationId = canTraceSameByte
                    ? Interlocked.Increment(ref _sameByteStageCorrelationSequence)
                    : 0;
                MarkdownSameByteResolverStageEvidence? stageEvidence = null;
                if (canTraceSameByte && correlationId is > 0 and <= MaximumSameByteStageTraceRequests &&
                    leasedInner is MarkdownSameByteAuditImageResolver sameByteResolver)
                {
                    MarkdownSameByteLeasedImageResolutionDiagnostic diagnostic = await sameByteResolver
                        .ResolveWithLeasedStageDiagnosticsAsync(
                            source, context, correlationId, admission, cancellationToken)
                        .ConfigureAwait(false);
                    lease = diagnostic.Lease;
                    stageEvidence = diagnostic.StageEvidence;
                }
                else
                {
                    lease = await leasedInner.ResolveWithSourceLeaseAsync(
                        source, context, admission, cancellationToken).ConfigureAwait(false);
                }

                double elapsedMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                if (stageEvidence is not null)
                {
                    double measuredStageMilliseconds =
                        stageEvidence.IndexWaitMilliseconds +
                        stageEvidence.SourceHashLookupMilliseconds +
                        stageEvidence.SourceHashIndexLookupMilliseconds +
                        stageEvidence.AssetStatMilliseconds +
                        stageEvidence.AssetOpenMilliseconds +
                        stageEvidence.AssetLengthCheckMilliseconds +
                        stageEvidence.AssetBufferAllocationMilliseconds +
                        stageEvidence.AssetReadMilliseconds +
                        stageEvidence.AssetSha256VerificationMilliseconds +
                        stageEvidence.AssetDisposeMilliseconds +
                        stageEvidence.ContentTypeValidationMilliseconds;
                    stageEvidence = stageEvidence with
                    {
                        UnattributedResolverMilliseconds = elapsedMilliseconds - measuredStageMilliseconds,
                    };
                }

                MarkdownLifecycleAutomationBridge.RecordImageResolution(
                    source,
                    lease.Resolution,
                    startedAt,
                    elapsedMilliseconds,
                    stageEvidence,
                    context);

                MarkdownImageResolutionLease result = lease;
                lease = null;
                return result;
            }
            finally
            {
                // The wrapper returns the same ownership handle. If diagnostics fail after
                // resolution, do not strand its source-byte reservation.
                lease?.Dispose();
            }
        }
    }
}

/// <summary>
/// Privacy-preserving context for a failed image resolution in an explicit audit.
/// No source URL, query, fragment, local path, or repository identifier is serialized.
/// </summary>
internal sealed record MarkdownImageResolutionAuditDiagnostic(
    string FailureClass,
    string DocumentContextKind,
    string SourceKind,
    string RouteClass,
    string? DocumentPathSha256,
    string? DocumentRefIdentitySha256,
    string? AttemptedUriHost,
    string? AttemptedUriPathSha256)
{
    private const int MaximumInputLength = 4096;
    private const int MaximumDocumentPathLength = 2048;
    private const int MaximumUriPathLength = 4096;

    internal string GetDocumentBudgetIdentity()
    {
        // The values are already one-way hashes of normalized document identity.
        // Path-only context still separates nested README pages; missing context
        // is deliberately grouped into a single bounded bucket.
        return DocumentPathSha256 is null && DocumentRefIdentitySha256 is null
            ? "missing\0" + DocumentContextKind
            : string.Concat(DocumentPathSha256 ?? "-", "\0", DocumentRefIdentitySha256 ?? "-");
    }

    internal static string HashSourceIdentity(string source) => Hash(source);

    internal static string? HashResolvedUriIdentity(Uri? resolvedUri) =>
        resolvedUri is null ? null : Hash(resolvedUri.AbsoluteUri);

    internal static string[] GetSourceMatchSha256(string source)
    {
        if (source.Length > MaximumInputLength)
            return Array.Empty<string>();

        string normalized = NormalizeImageSourceForComparison(source);
        var hashes = new List<string>(8)
        {
            HashImageSourceMatch("normalized", normalized.ToLowerInvariant()),
        };

        string candidatePath = normalized.TrimStart('.', '/');
        if (Uri.TryCreate(normalized, UriKind.Absolute, out Uri? uri))
        {
            try
            {
                hashes.Add(HashImageSourceMatch(
                    "request-uri",
                    uri.GetComponents(UriComponents.HttpRequestUrl, UriFormat.Unescaped).ToLowerInvariant()));
            }
            catch (UriFormatException)
            {
            }

            try
            {
                candidatePath = NormalizeImageSourceForComparison(uri.AbsolutePath).TrimStart('/');
            }
            catch (UriFormatException)
            {
                // Uri.AbsolutePath should already be escaped, but malformed
                // inputs must not make optional audit diagnostics fail image
                // resolution.
            }
        }

        // Keep the matcher bounded. The first suffix is the complete path for
        // relative Markdown sources; subsequent suffixes let the audit match
        // it against GitHub's repository/ref-qualified browser URL.
        if (candidatePath.Length > 0)
        {
            hashes.Add(HashImageSourceMatch("relative-suffix", candidatePath.ToLowerInvariant()));
            int separator = candidatePath.IndexOf('/');
            int suffixCount = 1;
            while (separator >= 0 && suffixCount < 64)
            {
                int suffixStart = separator + 1;
                if (suffixStart < candidatePath.Length)
                {
                    hashes.Add(HashImageSourceMatch(
                        "relative-suffix",
                        candidatePath[suffixStart..].ToLowerInvariant()));
                    suffixCount++;
                }
                separator = candidatePath.IndexOf('/', suffixStart);
            }
        }

        return hashes.ToArray();
    }

    internal static string GetSourceKind(string source) =>
        source.Length > MaximumInputLength ? "oversized" : ClassifySource(source);

    public static MarkdownImageResolutionAuditDiagnostic Create(
        string? source,
        MarkdownImageResolveContext? context,
        MarkdownImageResolution resolution)
    {
        source ??= string.Empty;
        MarkdownImageResolveContext actualContext = context ?? new(null);
        MarkdownDocumentSource? documentSource = actualContext.DocumentSource;
        bool hasRepositoryContext = documentSource?.HasRepositoryContext == true;
        string documentContextKind = hasRepositoryContext
            ? "repository"
            : actualContext.BaseUri is not null
                ? "base-uri-only"
                : "missing";
        string? documentPathSha256;
        if (TryNormalizeRepositoryPath(
                documentSource?.Path ?? actualContext.DocumentPath,
                out string normalizedPath))
        {
            documentPathSha256 = Hash("document-path\0" + normalizedPath);
        }
        else
        {
            // Base-URI-only hosts still need independent budgets per document.
            // Hash only normalized origin/path components; never include userinfo,
            // query strings, or fragments in the audit identity.
            documentPathSha256 = HashBaseUriDocumentPath(actualContext.BaseUri);
        }
        string? documentRefIdentitySha256 = hasRepositoryContext
            ? Hash(string.Concat(
                "document-ref\0",
                documentSource!.Owner!.Trim().ToLowerInvariant(),
                "\0",
                documentSource.Repository!.Trim().ToLowerInvariant(),
                "\0",
                documentSource.Ref!.Trim()))
            : null;

        if (source.Length > MaximumInputLength)
        {
            return new MarkdownImageResolutionAuditDiagnostic(
                GetFailureClass(resolution, documentContextKind, "oversized-source"),
                documentContextKind,
                "oversized",
                "oversized-source",
                documentPathSha256,
                documentRefIdentitySha256,
                null,
                null);
        }

        string sourceKind = ClassifySource(source);
        string routeClass;
        Uri? attemptedUri = null;
        bool repositoryImageResolved = documentSource is not null
            ? GitHubMarkdownImageUrlResolver.TryResolve(source, documentSource, out GitHubMarkdownImageReference reference)
            : GitHubMarkdownImageUrlResolver.TryResolve(
                source,
                actualContext.BaseUri,
                actualContext.DocumentPath,
                out reference);
        if (repositoryImageResolved)
        {
            routeClass = ClassifyResolvedRepositoryRoute(source);
            attemptedUri = GitHubMarkdownImageUrlResolver.CreateRawUri(reference);
        }
        else if (IsRootRelativeRawRoute(source))
        {
            routeClass = "github-root-relative-raw-rejected";
        }
        else if (!Uri.TryCreate(source, UriKind.Absolute, out attemptedUri))
        {
            routeClass = source.StartsWith("//", StringComparison.Ordinal)
                ? "network-path-unresolved"
                : !hasRepositoryContext && actualContext.BaseUri is null
                    ? "relative-missing-document-context"
                    : source.StartsWith("/", StringComparison.Ordinal)
                        ? "repository-root-relative-unresolved"
                        : "repository-relative-unresolved";
        }
        else if (attemptedUri.UserInfo.Length > 0 ||
            (attemptedUri.Scheme != Uri.UriSchemeHttps && attemptedUri.Scheme != Uri.UriSchemeHttp))
        {
            routeClass = "absolute-uri-rejected";
            attemptedUri = null;
        }
        else
        {
            routeClass = "absolute-uri";
        }

        string? attemptedHost = null;
        string? attemptedPathSha256 = null;
        if (attemptedUri is { IsAbsoluteUri: true } uri &&
            (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp) &&
            uri.UserInfo.Length == 0 && uri.AbsolutePath.Length <= MaximumUriPathLength)
        {
            try
            {
                string host = uri.IdnHost;
                if (host.Length is > 0 and <= 253)
                {
                    attemptedHost = host.ToLowerInvariant();
                    attemptedPathSha256 = Hash("uri-path\0" + uri.AbsolutePath);
                }
            }
            catch (UriFormatException)
            {
                // Invalid hosts have no safe request identity to publish.
            }
        }

        return new MarkdownImageResolutionAuditDiagnostic(
            GetFailureClass(resolution, documentContextKind, routeClass),
            documentContextKind,
            sourceKind,
            routeClass,
            documentPathSha256,
            documentRefIdentitySha256,
            attemptedHost,
            attemptedPathSha256);
    }

    private static string GetFailureClass(
        MarkdownImageResolution resolution,
        string documentContextKind,
        string routeClass)
    {
        if (resolution.Asset is not null)
            return "resolved";

        if (resolution.UnavailableReason is MarkdownImageUnavailableReason.RemoteContentBlocked or
            MarkdownImageUnavailableReason.InsecureRemoteContent or
            MarkdownImageUnavailableReason.MeteredConnection)
        {
            return "policy";
        }

        if (routeClass == "relative-missing-document-context" ||
            (documentContextKind == "missing" && routeClass == "repository-root-relative-unresolved"))
        {
            return "missing-document-context";
        }

        if (routeClass == "github-root-relative-raw-rejected")
            return "route-rejected";

        if (!resolution.IsHandled)
            return "not-handled";

        return resolution.UnavailableReason == MarkdownImageUnavailableReason.Offline
            ? "offline-or-fixture-miss"
            : "fetch-or-resolver-miss";
    }

    private static string ClassifySource(string source)
    {
        if (source.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            return "data";
        if (source.StartsWith("//", StringComparison.Ordinal))
            return "network-path";
        if (Uri.TryCreate(source, UriKind.Absolute, out Uri? uri))
        {
            if (uri.Scheme == Uri.UriSchemeHttps)
                return "absolute-https";
            if (uri.Scheme == Uri.UriSchemeHttp)
                return "absolute-http";
            return "absolute-other-scheme";
        }
        return source.StartsWith("/", StringComparison.Ordinal)
            ? "root-relative"
            : "relative";
    }

    private static string ClassifyResolvedRepositoryRoute(string source)
    {
        if (IsRootRelativeRawRoute(source))
            return "github-root-relative-raw";
        if (Uri.TryCreate(source, UriKind.Absolute, out Uri? uri))
        {
            string[] segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) &&
                segments.Length > 2 && segments[2].Equals("raw", StringComparison.OrdinalIgnoreCase))
            {
                return "github-absolute-raw";
            }
            return "repository-absolute";
        }
        return source.StartsWith("/", StringComparison.Ordinal)
            ? "repository-root-relative"
            : "repository-relative";
    }

    private static bool IsRootRelativeRawRoute(string source)
    {
        if (!source.StartsWith("/", StringComparison.Ordinal) || source.StartsWith("//", StringComparison.Ordinal))
            return false;

        int queryIndex = source.IndexOf('?');
        int fragmentIndex = source.IndexOf('#');
        int suffixIndex = queryIndex < 0
            ? fragmentIndex
            : fragmentIndex < 0
                ? queryIndex
                : Math.Min(queryIndex, fragmentIndex);
        int pathLength = suffixIndex < 0 ? source.Length : suffixIndex;
        int ownerEnd = source.IndexOf('/', 1);
        if (ownerEnd < 0 || ownerEnd >= pathLength)
            return false;
        int repositoryStart = ownerEnd + 1;
        int repositoryEnd = source.IndexOf('/', repositoryStart);
        if (repositoryEnd < 0 || repositoryEnd >= pathLength)
            return false;
        int rawStart = repositoryEnd + 1;
        int rawEnd = source.IndexOf('/', rawStart);
        if (rawEnd < 0 || rawEnd >= pathLength || rawEnd == rawStart || rawEnd - rawStart > 64)
            return false;
        int refStart = rawEnd + 1;
        int pathStart = source.IndexOf('/', refStart);
        if (pathStart <= refStart || pathStart + 1 >= pathLength)
            return false;

        try
        {
            return Uri.UnescapeDataString(source.Substring(rawStart, rawEnd - rawStart))
                .Equals("raw", StringComparison.OrdinalIgnoreCase);
        }
        catch (UriFormatException)
        {
            return false;
        }
    }

    private static bool TryNormalizeRepositoryPath(string? path, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(path) || path.Length > MaximumDocumentPathLength)
            return false;

        string candidate = path.Trim().Replace('\\', '/');
        if (candidate.StartsWith("/", StringComparison.Ordinal) ||
            candidate.StartsWith("//", StringComparison.Ordinal) ||
            candidate.Contains(':') ||
            candidate.Any(char.IsControl))
        {
            return false;
        }

        string[] segments = candidate.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Any(static segment => segment is "." or ".."))
            return false;

        normalized = string.Join('/', segments);
        return normalized.Length > 0;
    }

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static string HashImageSourceMatch(string kind, string value) =>
        Hash(string.Concat("image-source-match\0", kind, "\0", value));

    private static string NormalizeImageSourceForComparison(string source)
    {
        string trimmed = source.Trim().Replace('\\', '/');
        try
        {
            return Uri.UnescapeDataString(trimmed).Replace('\\', '/');
        }
        catch (UriFormatException)
        {
            return trimmed;
        }
    }

    private static string? HashBaseUriDocumentPath(Uri? baseUri)
    {
        if (baseUri is not { IsAbsoluteUri: true } ||
            baseUri.AbsolutePath.Length > MaximumDocumentPathLength)
        {
            return null;
        }

        string port = baseUri.IsDefaultPort ? string.Empty : ":" + baseUri.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return Hash(string.Concat(
            "document-base-uri-path\0",
            baseUri.Scheme.ToLowerInvariant(),
            "\0",
            baseUri.IdnHost.ToLowerInvariant(),
            port,
            "\0",
            baseUri.AbsolutePath));
    }
}

/// <summary>
/// Bounds nested failure diagnostics per document instead of allowing one large
/// README to consume the evidence allowance for every later audit case.
/// The bridge clears this budget when a new launch configuration is installed.
/// </summary>
internal sealed class MarkdownImageResolutionAuditBudget
{
    private readonly object _gate = new();
    private readonly Dictionary<string, int> _counts = new(StringComparer.Ordinal);
    private readonly int _maximumDocuments;
    private readonly int _maximumDiagnosticsPerDocument;

    internal MarkdownImageResolutionAuditBudget(
        int maximumDocuments = 500,
        int maximumDiagnosticsPerDocument = 512)
    {
        if (maximumDocuments <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumDocuments));
        if (maximumDiagnosticsPerDocument <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumDiagnosticsPerDocument));

        _maximumDocuments = maximumDocuments;
        _maximumDiagnosticsPerDocument = maximumDiagnosticsPerDocument;
    }

    internal bool TryReserve(MarkdownImageResolutionAuditDiagnostic diagnostic)
    {
        ArgumentNullException.ThrowIfNull(diagnostic);
        string documentIdentity = diagnostic.GetDocumentBudgetIdentity();
        lock (_gate)
        {
            if (_counts.TryGetValue(documentIdentity, out int count))
            {
                if (count >= _maximumDiagnosticsPerDocument)
                    return false;

                _counts[documentIdentity] = count + 1;
                return true;
            }

            if (_counts.Count >= _maximumDocuments)
                return false;

            _counts.Add(documentIdentity, 1);
            return true;
        }
    }

    internal void Reset()
    {
        lock (_gate)
            _counts.Clear();
    }
}
