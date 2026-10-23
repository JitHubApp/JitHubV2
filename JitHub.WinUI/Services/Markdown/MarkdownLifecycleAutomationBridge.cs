using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;
#if MARKDOWN_LAYOUT_DIAGNOSTICS
using MarkdownRenderer.Diagnostics;
#endif
using MarkdownRenderer.Images;

namespace JitHub.Services.Markdown;

/// <summary>
/// File-backed coordination for isolated Markdown lifecycle and production-content audits.
/// Production behavior is unchanged unless an explicit automation launch mode is enabled.
/// </summary>
internal static partial class MarkdownLifecycleAutomationBridge
{
    private const string FixtureVariable = "JITHUB_MARKDOWN_LIFECYCLE_FIXTURE";
    private const string TargetHostVariable = "JITHUB_MARKDOWN_LIFECYCLE_HOST";
    private const string AppReadyPathVariable = "JITHUB_MARKDOWN_APP_READY_PATH";
    private const string HostReadyPathVariable = "JITHUB_MARKDOWN_HOST_READY_PATH";
    private const string RuntimeSettingsPathVariable = "JITHUB_MARKDOWN_RUNTIME_SETTINGS_PATH";
    private const string LinkEvidencePathVariable = "JITHUB_MARKDOWN_LINK_EVIDENCE_PATH";
    private const string ImageEvidencePathVariable = "JITHUB_MARKDOWN_IMAGE_EVIDENCE_PATH";
    private const string ImageResolutionEvidencePathVariable = "JITHUB_MARKDOWN_IMAGE_RESOLUTION_EVIDENCE_PATH";
    private const string ImageTransportEvidencePathVariable = "JITHUB_MARKDOWN_IMAGE_TRANSPORT_EVIDENCE_PATH";
    private const string RasterPreparationEvidencePathVariable = "JITHUB_MARKDOWN_RASTER_PREPARATION_EVIDENCE_PATH";
    private const string SvgStageEvidencePathVariable = "JITHUB_MARKDOWN_SVG_STAGE_EVIDENCE_PATH";
    private const string SvgWorkerEvidencePathVariable = "JITHUB_MARKDOWN_SVG_WORKER_EVIDENCE_PATH";
    private const string SvgPreflightEvidencePathVariable = "JITHUB_MARKDOWN_SVG_PREFLIGHT_EVIDENCE_PATH";
    private const string RenderFailureEvidencePathVariable = "JITHUB_MARKDOWN_RENDER_FAILURE_EVIDENCE_PATH";
    private const string RenderCompleteEvidencePathVariable = "JITHUB_MARKDOWN_RENDER_COMPLETE_EVIDENCE_PATH";
    internal const string AdmittedRenderCompleteSuffix = ".admitted.json";
    private const string FirstViewportImagesReadyEvidencePathVariable = "JITHUB_MARKDOWN_FIRST_VIEWPORT_IMAGES_READY_EVIDENCE_PATH";
    private const string FirstViewportImagesReadyProgressPathVariable = "JITHUB_MARKDOWN_FIRST_VIEWPORT_IMAGES_READY_PROGRESS_PATH";
    private const string CaptureRequestPathVariable = "JITHUB_MARKDOWN_CAPTURE_REQUEST_PATH";
    private const string CaptureResponsePathVariable = "JITHUB_MARKDOWN_CAPTURE_RESPONSE_PATH";
    private const string ShutdownStagePathVariable = "JITHUB_MARKDOWN_SHUTDOWN_STAGE_PATH";
    private const string HighContrastVariable = "JITHUB_AUTOMATION_HIGH_CONTRAST";
    private const string ResourceMapAbsentVariable = "JITHUB_AUTOMATION_RESOURCE_MAP_ABSENT";
    private const string ResourceMapEvidencePathVariable = "JITHUB_AUTOMATION_RESOURCE_MAP_EVIDENCE_PATH";
    private const string SameByteReplayCorpusPathVariable = "JITHUB_README_AUDIT_SAME_BYTE_CORPUS";
    private const string SameByteReplayReadmeShaVariable = "JITHUB_README_AUDIT_SAME_BYTE_README_SHA";
    private const string SameByteReadmeSourceEvidencePathVariable = "JITHUB_README_AUDIT_NATIVE_SOURCE_EVIDENCE_PATH";
    private const int ParsePipelineBeginStage = 0;
    private const int ParsePipelineEngineParseAndCacheStage = 1;
    private const int ParsePipelineLegacyParseAndDocumentStage = 2;
    private const int ParsePipelineProgressiveScenePlanStage = 3;
    private const int ParsePipelineStyleRoleDemandStage = 4;
    private const int ParsePipelineSessionTotalStage = 5;
    internal const double MaximumAuditScrollViewportFraction = 0.95;

    private static readonly object SignalGate = new();
    private static readonly object ParsePipelineTimingGate = new();
    private static readonly object SameByteReadmeSourceGate = new();
    private static readonly MarkdownSameByteAuditResolverRegistry SameByteFixtureResolvers = new();
    private static readonly MarkdownImageResolutionAuditBudget ImageResolutionAuditBudget = new();
    private static readonly MarkdownImageResolutionAuditBudget ImageTransportAuditBudget = new();
    private static readonly MarkdownImageResolutionAuditBudget ImageTransportSuccessSampleBudget = new(maximumDiagnosticsPerDocument: 32);
    private static readonly MarkdownImageResolutionAuditBudget ImageTransportSuccessLimitNotices = new(maximumDiagnosticsPerDocument: 1);
    private static readonly MarkdownImageResolutionAuditBudget ImageTransportFailureLimitNotices = new(maximumDiagnosticsPerDocument: 1);
    private static readonly HashSet<string> ImageTransportDocuments = new(StringComparer.Ordinal);
    private static long _imageTransportGeneration;
    private static long _imageTransportOperationSequence;
    private static bool _imageTransportDocumentLimitNotified;
#if MARKDOWN_LAYOUT_DIAGNOSTICS
    private const int MaximumImageActivationTraceRecords = 64;
    private const int MaximumSvgWorkerFontCatalogPhaseRecords = 64;
    private static readonly List<ImageActivationTraceSignal> ImageActivationTraceRecords =
        new(MaximumImageActivationTraceRecords);
    private static readonly List<SvgWorkerFontCatalogPhaseSignal> SvgWorkerFontCatalogPhaseRecords =
        new(MaximumSvgWorkerFontCatalogPhaseRecords);
    private static int _imageActivationTraceRecordsWritten;
    private static int _svgWorkerFontCatalogPhaseRecordsWritten;
    private static bool _deferredSvgStageEvidenceFlushed;
#endif
    private static SameByteReadmeSourceIdentity? _sameByteReadmeSourceIdentity;
    private static SameByteReadmeSourceAdmission? _sameByteReadmeSourceAdmission;
    private static SameByteReadmeSourceAdmissionFailure? _sameByteReadmeSourceAdmissionFailure;
    // Generation and host of the latest source-admission event, success or
    // failure. The viewer records one at the first completion after every
    // Markdown assignment, so a later render-complete that still reports this
    // generation proves no assignment happened in between (T3-010).
    private static long _sameByteAdmissionEventGeneration;
    private static string? _sameByteAdmissionEventHost;
    private static string? _signaledHost;
    private static long _markdownViewerLoadCount;
    private static long _markdownViewerUnloadCount;
    private static long _markdownViewerLiveCount;
    private static string? _shutdownAuditDetails;
    private static bool _launchFixtureEnabled;
    private static bool _productionAuditEnabled;
    private static bool _offlineSameBytePublicAuditEnabled;
    private static JitHub.WinUI.OfflineSameByteReadmeAuditPin? _offlineSameByteReadmeAuditPin;
    private static string? _launchTargetHost;
    private static int _parsePipelineOwnerIdentity;
    private static bool _hasParsePipelineTiming;
    private static double _engineParseAndCacheMilliseconds;
    private static double _legacyParseAndDocumentMilliseconds;
    private static double _progressiveScenePlanMilliseconds;
    private static double _styleRoleDemandMilliseconds;
    private static double _parseSessionTotalMilliseconds;

    public static bool IsEnabled => _launchFixtureEnabled || IsOne(FixtureVariable);

    public static bool IsEvidenceEnabled => IsEnabled || _productionAuditEnabled;

    public static bool IsProductionAuditEnabled => _productionAuditEnabled;

    internal static bool IsOfflineSameBytePublicAuditEnabled =>
        _productionAuditEnabled && _offlineSameBytePublicAuditEnabled;

    internal static JitHub.WinUI.OfflineSameByteReadmeAuditPin? OfflineSameByteReadmeAuditPin =>
        IsOfflineSameBytePublicAuditEnabled ? _offlineSameByteReadmeAuditPin : null;

    public static bool IsFirstViewportImagesReadyEvidenceEnabled => _productionAuditEnabled &&
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(FirstViewportImagesReadyEvidencePathVariable));

    internal static bool IsFirstViewportImagesReadyProgressEnabled =>
        IsFirstViewportImagesReadyEvidenceEnabled &&
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(FirstViewportImagesReadyProgressPathVariable));

    internal static void RecordFirstViewportImagesReadyProgress(FirstViewportImagesReadyProgress progress)
    {
        if (!IsFirstViewportImagesReadyProgressEnabled)
            return;

        FirstViewportImagesReadyEvidenceWriter.TryQueueProgressWrite(
            Environment.GetEnvironmentVariable(FirstViewportImagesReadyProgressPathVariable),
            auditEnabled: true,
            progress);
    }

    public static bool IsShutdownAuditDiagnosticsEnabled => IsEvidenceEnabled &&
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ShutdownStagePathVariable));

    public static bool IsSameByteReplayEnabled =>
        _productionAuditEnabled &&
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(SameByteReplayCorpusPathVariable));

    internal static bool IsSameByteAdmissionTraceEnabled =>
        IsSameByteReplayEnabled &&
        string.Equals(Environment.GetEnvironmentVariable("JITHUB_MARKDOWN_SOURCE_ADMISSION_TRACE"), "1", StringComparison.Ordinal);

    internal static void TraceSameByteAdmission(string message)
    {
        if (!IsSameByteAdmissionTraceEnabled)
            return;

        string? automationRoot = Environment.GetEnvironmentVariable("JITHUB_AUTOMATION_DATA_ROOT");
        if (string.IsNullOrWhiteSpace(automationRoot))
            return;

        try
        {
            string logDirectory = Path.Combine(Path.GetFullPath(automationRoot), "Local", "logs");
            Directory.CreateDirectory(logDirectory);
            File.AppendAllText(
                Path.Combine(logDirectory, "startup-phases.log"),
                $"{DateTimeOffset.UtcNow:O}\t{Environment.ProcessId}\tsame-byte.{message}{Environment.NewLine}");
        }
        catch
        {
            // Diagnostic tracing must never change application behavior.
        }
    }

    public static string? SameByteReplayCorpusPath => IsSameByteReplayEnabled
        ? Environment.GetEnvironmentVariable(SameByteReplayCorpusPathVariable)
        : null;

    public static string? SameByteReplayReadmeSha => IsSameByteReplayEnabled
        ? Environment.GetEnvironmentVariable(SameByteReplayReadmeShaVariable)
        : null;

    internal static MarkdownSameByteAuditImageResolver GetSameByteFixtureResolver(
        string corpusDirectory,
        string repository,
        string commit,
        string readmeGitBlobSha1)
    {
        if (!IsSameByteReplayEnabled)
            throw new InvalidOperationException("The same-byte fixture is unavailable outside production audit mode.");

        return SameByteFixtureResolvers.GetOrCreate(
            corpusDirectory, repository, commit, readmeGitBlobSha1);
    }

    private static void ClearSameByteReadmeSourceIdentity()
    {
        lock (SameByteReadmeSourceGate)
        {
            _sameByteReadmeSourceIdentity = null;
            _sameByteReadmeSourceAdmission = null;
            _sameByteReadmeSourceAdmissionFailure = null;
        }
    }

    private static bool IsGitBlobSha1(string? value)
    {
        if (value is null || value.Length != 40)
            return false;

        foreach (char character in value)
        {
            if (!Uri.IsHexDigit(character))
                return false;
        }

        return true;
    }

    public static void RecordSameByteReadmeSource(
        ReadOnlySpan<byte> bytes,
        string decodedMarkdown,
        string readmeGitBlobSha1)
    {
        if (!IsSameByteReplayEnabled)
            return;

        string expectedReadmeGitBlobSha1 = SameByteReplayReadmeSha ?? string.Empty;
        if (!IsGitBlobSha1(readmeGitBlobSha1) ||
            !string.Equals(readmeGitBlobSha1, expectedReadmeGitBlobSha1, StringComparison.OrdinalIgnoreCase))
        {
            if (IsSameByteAdmissionTraceEnabled)
                TraceSameByteAdmission("source-cleared:git-blob-mismatch");
            ClearSameByteReadmeSourceIdentity();
            return;
        }

        byte[] decodedBytes = Encoding.UTF8.GetBytes(decodedMarkdown);
        var identity = new SameByteReadmeSourceIdentity(
            bytes.Length,
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
            decodedBytes.Length,
            Convert.ToHexString(SHA256.HashData(decodedBytes)).ToLowerInvariant(),
            readmeGitBlobSha1)
        {
            DecodedMarkdownReference = new WeakReference<string>(decodedMarkdown),
        };
        lock (SameByteReadmeSourceGate)
        {
            SameByteReadmeSourceIdentity? previous = _sameByteReadmeSourceIdentity;
            bool isSameSource = previous is not null &&
                previous.RawByteSize == identity.RawByteSize &&
                string.Equals(previous.RawSha256, identity.RawSha256, StringComparison.OrdinalIgnoreCase) &&
                previous.DecodedTextUtf8ByteSize == identity.DecodedTextUtf8ByteSize &&
                string.Equals(previous.DecodedTextSha256, identity.DecodedTextSha256, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(previous.ReadmeGitBlobSha1, identity.ReadmeGitBlobSha1, StringComparison.OrdinalIgnoreCase);
            if (isSameSource)
            {
                // Early and ordinary README paths may load the same pinned
                // blob independently. Keep both its identity and any
                // generation-matched admission stable across that duplicate.
                identity = previous!;
                identity.DecodedMarkdownReference ??= new WeakReference<string>(decodedMarkdown);
                identity.DecodedMarkdownReference.SetTarget(decodedMarkdown);
            }
            else
            {
                _sameByteReadmeSourceAdmission = null;
                _sameByteReadmeSourceAdmissionFailure = null;
            }
            _sameByteReadmeSourceIdentity = identity;
        }

        if (IsSameByteAdmissionTraceEnabled)
            TraceSameByteAdmission($"source-recorded:{identity.ReadmeGitBlobSha1}:{identity.RawByteSize}");

        WriteSignal(
            Environment.GetEnvironmentVariable(SameByteReadmeSourceEvidencePathVariable),
            new SameByteReadmeSourceSignal(
                identity.RawByteSize,
                identity.RawSha256,
                identity.DecodedTextUtf8ByteSize,
                identity.DecodedTextSha256,
                identity.ReadmeGitBlobSha1),
            MarkdownLifecycleJsonContext.Default.SameByteReadmeSourceSignal);
    }

    internal static SameByteReadmeSourceIdentity? MatchSameByteReadmeSource(
        string markdown,
        string? expectedReadmeGitBlobSha1,
        out string failureReason)
    {
        if (!IsSameByteReplayEnabled)
        {
            failureReason = "audit-disabled";
            return null;
        }

        if (!IsGitBlobSha1(expectedReadmeGitBlobSha1))
        {
            failureReason = "document-identity-missing";
            return null;
        }

        SameByteReadmeSourceIdentity capturedIdentity;
        lock (SameByteReadmeSourceGate)
        {
            SameByteReadmeSourceIdentity? identity = _sameByteReadmeSourceIdentity;
            if (identity is null)
            {
                failureReason = "source-evidence-missing";
                return null;
            }

            if (!string.Equals(identity.ReadmeGitBlobSha1, expectedReadmeGitBlobSha1, StringComparison.OrdinalIgnoreCase))
            {
                failureReason = "git-blob-mismatch";
                return null;
            }

            // The source loader already hashed this immutable string off the
            // UI thread. The common payload path hands that exact instance to
            // the viewer, so a second full UTF-8 encode/SHA-256 here would
            // block input without adding any identity assurance.
            if (identity.DecodedMarkdownReference?.TryGetTarget(out string? recordedMarkdown) == true &&
                ReferenceEquals(recordedMarkdown, markdown))
            {
                failureReason = "matched";
                return identity;
            }

            capturedIdentity = identity;
        }

        // A host may legitimately reconstruct an equal string. Verify its
        // digest off the lock in that uncommon path, and fail closed if the
        // source identity changed while the digest was computed.
        byte[] decodedBytes = Encoding.UTF8.GetBytes(markdown);
        string decodedTextSha256 = Convert.ToHexString(SHA256.HashData(decodedBytes)).ToLowerInvariant();
        lock (SameByteReadmeSourceGate)
        {
            if (!ReferenceEquals(capturedIdentity, _sameByteReadmeSourceIdentity))
            {
                failureReason = "source-evidence-missing";
                return null;
            }

            if (capturedIdentity.DecodedTextUtf8ByteSize != decodedBytes.Length)
            {
                failureReason = "decoded-text-size-mismatch";
                return null;
            }

            if (!string.Equals(capturedIdentity.DecodedTextSha256, decodedTextSha256, StringComparison.OrdinalIgnoreCase))
            {
                failureReason = "decoded-text-digest-mismatch";
                return null;
            }

            failureReason = "matched";
            return capturedIdentity;
        }
    }

    internal static void RecordSameByteReadmeSourceAdmission(
        string host,
        long generation,
        long stopwatchTicks,
        long stopwatchFrequency,
        SameByteReadmeSourceIdentity? identity,
        string failureReason,
        MarkdownDocumentSource? documentSource = null)
    {
        if (!IsSameByteReplayEnabled || !TargetsHost(host))
            return;

        if (IsSameByteAdmissionTraceEnabled)
            TraceSameByteAdmission($"admission-attempt:{host}:{generation}:{(identity is null ? failureReason : "matched")}");

        lock (SameByteReadmeSourceGate)
        {
            _sameByteReadmeSourceAdmission = null;
            _sameByteReadmeSourceAdmissionFailure = null;
            _sameByteAdmissionEventGeneration = 0;
            _sameByteAdmissionEventHost = null;
            if (string.IsNullOrWhiteSpace(host) || generation <= 0)
            {
                return;
            }

            _sameByteAdmissionEventGeneration = generation;
            _sameByteAdmissionEventHost = host;

            string? failure = null;
            if (identity is null)
            {
                failure = IsSafeSameByteAdmissionFailure(failureReason)
                    ? failureReason
                    : "admission-state-invalid";
            }
            else if (!ReferenceEquals(identity, _sameByteReadmeSourceIdentity))
            {
                failure = "source-identity-changed";
            }
            else if (stopwatchTicks <= 0 || stopwatchFrequency <= 0)
            {
                failure = "monotonic-clock-invalid";
            }

            if (failure is not null)
            {
                _sameByteReadmeSourceAdmissionFailure = new SameByteReadmeSourceAdmissionFailure(
                    host,
                    generation,
                    failure);
                if (IsSameByteAdmissionTraceEnabled)
                    TraceSameByteAdmission($"admission-failed:{host}:{generation}:{failure}");
                return;
            }

            _sameByteReadmeSourceAdmission = new SameByteReadmeSourceAdmission(
                Environment.ProcessId,
                host,
                generation,
                identity!.RawByteSize,
                identity.RawSha256,
                identity.DecodedTextUtf8ByteSize,
                identity.DecodedTextSha256,
                identity.ReadmeGitBlobSha1,
                stopwatchTicks,
                stopwatchFrequency,
                documentSource?.Owner,
                documentSource?.Repository,
                documentSource?.Ref,
                documentSource?.Path);
            if (IsSameByteAdmissionTraceEnabled)
                TraceSameByteAdmission($"admission-recorded:{host}:{generation}");
        }
    }

    private static bool IsSafeSameByteAdmissionFailure(string value) => value is
        "audit-disabled" or
        "document-identity-missing" or
        "source-evidence-missing" or
        "git-blob-mismatch" or
        "decoded-text-size-mismatch" or
        "decoded-text-digest-mismatch" or
        "generation-not-advanced" or
        "assignment-generation-unavailable" or
        "assignment-generation-superseded" or
        "markdown-unchanged" or
        "mutable-source-ref" or
        "rendered-document-changed";

    public static bool IsRasterPreparationEvidenceEnabled => IsEvidenceEnabled &&
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(RasterPreparationEvidencePathVariable));

    public static bool IsSvgStageEvidenceEnabled => IsEvidenceEnabled &&
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(SvgStageEvidencePathVariable));

#if MARKDOWN_LAYOUT_DIAGNOSTICS
    public static void RecordImageActivationTrace(
        long generation,
        int imageOrdinal,
        int stageId,
        long stopwatchTicks,
        long stopwatchFrequency,
        int viewportMeasured,
        int intersectsViewport,
        long viewportWidthSixtyFourthDips,
        long viewportHeightSixtyFourthDips)
    {
        if (!IsSvgStageEvidenceEnabled ||
            generation <= 0 ||
            imageOrdinal < -1 ||
            stageId is < 1 or > 15 ||
            stopwatchTicks <= 0 ||
            stopwatchFrequency <= 0 ||
            viewportMeasured is < 0 or > 1 ||
            intersectsViewport is < 0 or > 1 ||
            viewportWidthSixtyFourthDips < 0 ||
            viewportHeightSixtyFourthDips < 0)
        {
            return;
        }

        var signal = new ImageActivationTraceSignal(
            Environment.ProcessId,
            15,
            generation,
            imageOrdinal,
            stageId,
            stopwatchTicks,
            stopwatchFrequency,
            viewportMeasured,
            intersectsViewport,
            viewportWidthSixtyFourthDips,
            viewportHeightSixtyFourthDips);
        lock (SignalGate)
        {
            if (_deferredSvgStageEvidenceFlushed ||
                _imageActivationTraceRecordsWritten >= MaximumImageActivationTraceRecords)
            {
                return;
            }

            ImageActivationTraceRecords.Add(signal);
            _imageActivationTraceRecordsWritten++;
        }
    }

    /// <summary>
    /// Writes the bounded activation trace and font-catalog phase rows once,
    /// after renderer teardown. The normal audit copies this file after closing
    /// the app; an abrupt process kill before shutdown can therefore lose these
    /// optional buffered diagnostics.
    /// </summary>
    public static void FlushDeferredSvgStageEvidence()
    {
        ImageActivationTraceSignal[] activationSignals;
        SvgWorkerFontCatalogPhaseSignal[] fontCatalogPhaseSignals;
        string? path;
        lock (SignalGate)
        {
            if (_deferredSvgStageEvidenceFlushed)
                return;

            _deferredSvgStageEvidenceFlushed = true;
            activationSignals = ImageActivationTraceRecords.ToArray();
            ImageActivationTraceRecords.Clear();
            fontCatalogPhaseSignals = SvgWorkerFontCatalogPhaseRecords.ToArray();
            SvgWorkerFontCatalogPhaseRecords.Clear();
            path = IsSvgStageEvidenceEnabled
                ? Environment.GetEnvironmentVariable(SvgStageEvidencePathVariable)
                : null;
        }

        if ((activationSignals.Length == 0 && fontCatalogPhaseSignals.Length == 0) ||
            string.IsNullOrWhiteSpace(path))
            return;

        try
        {
            var contents = new StringBuilder(
                (activationSignals.Length + fontCatalogPhaseSignals.Length) * 256);
            foreach (ImageActivationTraceSignal signal in activationSignals)
            {
                contents.Append(JsonSerializer.Serialize(
                    signal,
                    MarkdownLifecycleJsonContext.Default.ImageActivationTraceSignal));
                contents.AppendLine();
            }

            foreach (SvgWorkerFontCatalogPhaseSignal signal in fontCatalogPhaseSignals)
            {
                contents.Append(JsonSerializer.Serialize(
                    signal,
                    MarkdownLifecycleJsonContext.Default.SvgWorkerFontCatalogPhaseSignal));
                contents.AppendLine();
            }

            string fullPath = Path.GetFullPath(path);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            // Other SVG stages share this NDJSON file and use SignalGate too.
            // Serialize both bounded buffers, then perform one append at shutdown.
            lock (SignalGate)
                File.AppendAllText(fullPath, contents.ToString());
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
        catch (ArgumentException)
        {
        }
        catch (NotSupportedException)
        {
        }
        catch (System.Security.SecurityException)
        {
        }
    }
#endif

    public static bool IsHighContrastEnabled => IsEnabled && IsOne(HighContrastVariable);

    public static bool IsResourceMapForcedAbsent => IsEnabled && IsOne(ResourceMapAbsentVariable);

    public static string? TargetHost => IsEvidenceEnabled
        ? _launchTargetHost ?? Environment.GetEnvironmentVariable(TargetHostVariable)
        : null;

    public static void ConfigureLaunchOptions(
        bool fixtureEnabled,
        string? targetHost,
        bool productionAuditEnabled = false,
        bool offlineSameBytePublicAuditEnabled = false,
        JitHub.WinUI.OfflineSameByteReadmeAuditPin? offlineSameByteReadmeAuditPin = null)
    {
        if (offlineSameBytePublicAuditEnabled &&
            (!productionAuditEnabled || offlineSameByteReadmeAuditPin is null))
        {
            throw new ArgumentException(
                "Offline same-byte public audit requires production audit mode and a validated pinned README identity.",
                nameof(offlineSameBytePublicAuditEnabled));
        }

        SameByteFixtureResolvers.Clear();
        ImageResolutionAuditBudget.Reset();
        lock (SignalGate)
        {
            _launchFixtureEnabled = fixtureEnabled;
            _productionAuditEnabled = productionAuditEnabled;
            _offlineSameBytePublicAuditEnabled = offlineSameBytePublicAuditEnabled;
            _offlineSameByteReadmeAuditPin = offlineSameBytePublicAuditEnabled
                ? offlineSameByteReadmeAuditPin
                : null;
            _launchTargetHost = string.IsNullOrWhiteSpace(targetHost) ? null : targetHost.Trim();
            Interlocked.Increment(ref _imageTransportGeneration);
            ImageTransportAuditBudget.Reset();
            ImageTransportSuccessSampleBudget.Reset();
            ImageTransportSuccessLimitNotices.Reset();
            ImageTransportFailureLimitNotices.Reset();
            ImageTransportDocuments.Clear();
            _imageTransportDocumentLimitNotified = false;
#if MARKDOWN_LAYOUT_DIAGNOSTICS
            _imageActivationTraceRecordsWritten = 0;
            _svgWorkerFontCatalogPhaseRecordsWritten = 0;
            _deferredSvgStageEvidenceFlushed = false;
            ImageActivationTraceRecords.Clear();
            SvgWorkerFontCatalogPhaseRecords.Clear();
#endif
        }
        lock (SameByteReadmeSourceGate)
        {
            _sameByteReadmeSourceIdentity = null;
            _sameByteReadmeSourceAdmission = null;
            _sameByteReadmeSourceAdmissionFailure = null;
        }

        if (!fixtureEnabled && !productionAuditEnabled)
        {
            lock (SignalGate)
            {
                _shutdownAuditDetails = null;
                System.Threading.Interlocked.Exchange(ref _markdownViewerLoadCount, 0);
                System.Threading.Interlocked.Exchange(ref _markdownViewerUnloadCount, 0);
                System.Threading.Interlocked.Exchange(ref _markdownViewerLiveCount, 0);
            }
            lock (ParsePipelineTimingGate)
            {
                _parsePipelineOwnerIdentity = 0;
                _hasParsePipelineTiming = false;
                _engineParseAndCacheMilliseconds = 0;
                _legacyParseAndDocumentMilliseconds = 0;
                _progressiveScenePlanMilliseconds = 0;
                _styleRoleDemandMilliseconds = 0;
                _parseSessionTotalMilliseconds = 0;
            }
        }
    }

    internal static void RecordParsePipelineStage(
        int ownerIdentity,
        int stage,
        long elapsedStopwatchTicks)
    {
        if (!IsEvidenceEnabled)
            return;

        lock (ParsePipelineTimingGate)
        {
            if (stage == ParsePipelineBeginStage)
            {
                _parsePipelineOwnerIdentity = ownerIdentity;
                _hasParsePipelineTiming = true;
                _engineParseAndCacheMilliseconds = 0;
                _legacyParseAndDocumentMilliseconds = 0;
                _progressiveScenePlanMilliseconds = 0;
                _styleRoleDemandMilliseconds = 0;
                _parseSessionTotalMilliseconds = 0;
                return;
            }

            if (!_hasParsePipelineTiming || ownerIdentity != _parsePipelineOwnerIdentity)
                return;

            double elapsedMilliseconds = elapsedStopwatchTicks > 0
                ? Stopwatch.GetElapsedTime(0, elapsedStopwatchTicks).TotalMilliseconds
                : 0;
            switch (stage)
            {
                case ParsePipelineEngineParseAndCacheStage:
                    _engineParseAndCacheMilliseconds += elapsedMilliseconds;
                    break;
                case ParsePipelineLegacyParseAndDocumentStage:
                    _legacyParseAndDocumentMilliseconds += elapsedMilliseconds;
                    break;
                case ParsePipelineProgressiveScenePlanStage:
                    _progressiveScenePlanMilliseconds = elapsedMilliseconds;
                    break;
                case ParsePipelineStyleRoleDemandStage:
                    _styleRoleDemandMilliseconds = elapsedMilliseconds;
                    break;
                case ParsePipelineSessionTotalStage:
                    _parseSessionTotalMilliseconds = elapsedMilliseconds;
                    break;
            }
        }
    }

    internal static MarkdownAuditParsePreparationTiming GetParsePreparationTiming(int ownerIdentity)
    {
        lock (ParsePipelineTimingGate)
        {
            return _hasParsePipelineTiming && ownerIdentity == _parsePipelineOwnerIdentity
                ? new MarkdownAuditParsePreparationTiming(
                    _engineParseAndCacheMilliseconds,
                    _legacyParseAndDocumentMilliseconds,
                    _progressiveScenePlanMilliseconds,
                    _styleRoleDemandMilliseconds,
                    _parseSessionTotalMilliseconds)
                : default;
        }
    }

    public static bool TargetsHost(string automationId) =>
        IsEvidenceEnabled &&
        !string.IsNullOrWhiteSpace(automationId) &&
        (string.IsNullOrWhiteSpace(TargetHost) ||
         automationId.StartsWith(TargetHost, StringComparison.Ordinal));

    public static void SignalAppReady()
    {
        if (!IsEvidenceEnabled)
        {
            return;
        }

        WriteSignal(
            Environment.GetEnvironmentVariable(AppReadyPathVariable),
            new LifecycleReadySignal(Environment.ProcessId, "app", DateTimeOffset.UtcNow),
            MarkdownLifecycleJsonContext.Default.LifecycleReadySignal);
    }

    public static void SignalHostReady(string automationId)
    {
        if (!IsEvidenceEnabled || string.IsNullOrWhiteSpace(automationId))
        {
            return;
        }

        string? target = TargetHost;
        if (!string.IsNullOrWhiteSpace(target) &&
            !automationId.StartsWith(target, StringComparison.Ordinal))
        {
            return;
        }

        lock (SignalGate)
        {
            if (string.Equals(_signaledHost, automationId, StringComparison.Ordinal))
            {
                return;
            }

            WriteSignal(
                Environment.GetEnvironmentVariable(HostReadyPathVariable),
                new LifecycleReadySignal(Environment.ProcessId, automationId, DateTimeOffset.UtcNow),
                MarkdownLifecycleJsonContext.Default.LifecycleReadySignal);
            _signaledHost = automationId;
        }
    }

    public static double? GetTextScaleFactor()
    {
        if (!IsEnabled)
        {
            return null;
        }

        if (TryReadRuntimeSettings(out MarkdownLifecycleRuntimeSettings? settings) &&
            settings is not null &&
            settings.TextScaleFactor > 0)
        {
            return Math.Clamp(settings.TextScaleFactor, 1, 3);
        }

        return double.TryParse(
            Environment.GetEnvironmentVariable("JITHUB_AUTOMATION_TEXT_SCALE_FACTOR"),
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out double scale)
                ? Math.Clamp(scale, 1, 3)
                : null;
    }

    public static int GetRuntimeSettingsRevision() =>
        IsEnabled &&
        TryReadRuntimeSettings(out MarkdownLifecycleRuntimeSettings? settings) &&
        settings is not null
            ? settings.Revision
            : 0;

    private static bool TryReadRuntimeSettings(out MarkdownLifecycleRuntimeSettings? settings)
    {
        settings = null;
        string? path = Environment.GetEnvironmentVariable(RuntimeSettingsPathVariable);
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            using FileStream stream = new(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            settings = JsonSerializer.Deserialize(
                stream,
                MarkdownLifecycleJsonContext.Default.RuntimeSettings);
            return settings is not null;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static void SignalResourceMapFallback(string fallback)
    {
        if (!IsResourceMapForcedAbsent)
        {
            return;
        }

        WriteSignal(
            Environment.GetEnvironmentVariable(ResourceMapEvidencePathVariable),
            new ResourceMapFallbackSignal(Environment.ProcessId, fallback, DateTimeOffset.UtcNow),
            MarkdownLifecycleJsonContext.Default.ResourceMapFallbackSignal);
    }

    public static bool RecordLinkRoute(string automationId, string disposition)
    {
        if (!TargetsHost(automationId) || string.IsNullOrWhiteSpace(disposition))
        {
            return false;
        }

        string? path = Environment.GetEnvironmentVariable(LinkEvidencePathVariable);
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        lock (SignalGate)
        {
            try
            {
                string fullPath = Path.GetFullPath(path);
                Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
                string entry = JsonSerializer.Serialize(
                    new LinkRouteSignal(
                        Environment.ProcessId,
                        automationId,
                        disposition,
                        DateTimeOffset.UtcNow),
                    MarkdownLifecycleJsonContext.Default.LinkRouteSignal);
                File.AppendAllText(fullPath, entry + Environment.NewLine);
                return true;
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }
    }

    public static void RecordImageUnavailable(
        string automationId,
        string source,
        MarkdownRenderer.Images.MarkdownImageUnavailableReason reason,
        MarkdownRenderer.Images.MarkdownSvgFailureReason? svgFailureReason = null)
    {
        if (!TargetsHost(automationId))
        {
            return;
        }

        string? path = Environment.GetEnvironmentVariable(ImageEvidencePathVariable);
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        lock (SignalGate)
        {
            try
            {
                string fullPath = Path.GetFullPath(path);
                Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
                string entry = JsonSerializer.Serialize(
                    new ImageUnavailableSignal(
                        Environment.ProcessId,
                        automationId,
                        MarkdownImageResolutionAuditDiagnostic.HashSourceIdentity(source),
                        MarkdownImageResolutionAuditDiagnostic.GetSourceKind(source),
                        MarkdownImageResolutionAuditDiagnostic.GetSourceMatchSha256(source),
                        reason.ToString(),
                        svgFailureReason?.ToString(),
                        DateTimeOffset.UtcNow),
                    MarkdownLifecycleJsonContext.Default.ImageUnavailableSignal);
                File.AppendAllText(fullPath, entry + Environment.NewLine);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    public static void SignalShutdownStage(string stage)
    {
        if (!IsEvidenceEnabled)
        {
            return;
        }

        lock (SignalGate)
        {
            string stageWithAuditDetails = _shutdownAuditDetails is { Length: > 0 } details
                ? $"{stage};{details}"
                : stage;
            WriteSignal(
                Environment.GetEnvironmentVariable(ShutdownStagePathVariable),
                new ShutdownStageSignal(Environment.ProcessId, stageWithAuditDetails, DateTimeOffset.UtcNow),
                MarkdownLifecycleJsonContext.Default.ShutdownStageSignal);
        }
    }

    public static void RecordMarkdownViewerLoaded()
    {
        if (!IsShutdownAuditDiagnosticsEnabled)
            return;

        System.Threading.Interlocked.Increment(ref _markdownViewerLoadCount);
        System.Threading.Interlocked.Increment(ref _markdownViewerLiveCount);
    }

    public static void RecordMarkdownViewerUnloaded()
    {
        if (!IsShutdownAuditDiagnosticsEnabled)
            return;

        System.Threading.Interlocked.Increment(ref _markdownViewerUnloadCount);
        System.Threading.Interlocked.Decrement(ref _markdownViewerLiveCount);
    }

    public static void RecordMarkdownShutdownAuditSnapshot(
        MarkdownShutdownAuditPerformanceSnapshot? performance,
        bool pageUnloadWaitTimedOut,
        bool shellContentUnloadWaitTimedOut = false,
        bool markdownRendererDisposalWaitTimedOut = false,
        int markdownRenderersForceDisposed = 0)
    {
        if (!IsShutdownAuditDiagnosticsEnabled)
            return;

        long loaded = System.Threading.Interlocked.Read(ref _markdownViewerLoadCount);
        long unloaded = System.Threading.Interlocked.Read(ref _markdownViewerUnloadCount);
        long live = System.Threading.Interlocked.Read(ref _markdownViewerLiveCount);
        string performanceDetails = performance is null
            ? "performanceSnapshotAvailable=false"
            : FormattableString.Invariant(
                $"performanceSnapshotAvailable=true;pendingImageFetches={performance.PendingImageFetches};activeImageFetches={performance.ActiveImageFetches};scenePreparationsTotal={performance.ScenePreparationsTotal};pendingSourceByteRequests={performance.PendingSourceByteRequests};inFlightSourceBytes={performance.InFlightSourceBytes}");
        string details = FormattableString.Invariant(
            $"markdown-shutdown-audit:pageUnloadWaitTimedOut={(pageUnloadWaitTimedOut ? "true" : "false")};shellContentUnloadWaitTimedOut={(shellContentUnloadWaitTimedOut ? "true" : "false")};markdownRendererDisposalWaitTimedOut={(markdownRendererDisposalWaitTimedOut ? "true" : "false")};markdownRenderersForceDisposed={markdownRenderersForceDisposed};markdownViewsLoaded={loaded};markdownViewsUnloaded={unloaded};markdownViewsStillLoaded={live};{performanceDetails}");

        lock (SignalGate)
            _shutdownAuditDetails = details;

        SignalShutdownStage("markdown-shutdown-audit-snapshot");
    }

    internal static Action<GitHubRawImageFetchOutcome>? CreateRepositoryImageFetchObserver(
        string source,
        MarkdownImageResolveContext context,
        Uri resourceUri,
        MarkdownRepositoryImageTransportStage stage)
    {
        // The normal application and uninstrumented release audit do not hash,
        // allocate a callback/collector, or inspect document identity here.
        if (!IsEvidenceEnabled)
            return null;
        string? path = Environment.GetEnvironmentVariable(ImageTransportEvidencePathVariable);
        if (string.IsNullOrWhiteSpace(path))
            return null;
        try
        {
            if (!Enum.IsDefined(stage))
                return null;
            MarkdownRepositoryImageTransportIdentity? identity =
                MarkdownRepositoryImageTransportIdentity.TryCreate(source, context, resourceUri);
            if (identity is null)
                return null;
            lock (SignalGate)
            {
                if (!IsEvidenceEnabled || path != Environment.GetEnvironmentVariable(ImageTransportEvidencePathVariable))
                    return null;
                var documentIdentity = new MarkdownImageResolutionAuditDiagnostic(
                    "transport", identity.DocumentContextKind, identity.SourceKind,
                    "transport", identity.DocumentPathSha256, identity.DocumentRefIdentitySha256, null, null);
                string documentKey = documentIdentity.GetDocumentBudgetIdentity();
                if (!ImageTransportDocuments.Contains(documentKey) && ImageTransportDocuments.Count >= 500)
                {
                    if (!_imageTransportDocumentLimitNotified)
                    {
                        _imageTransportDocumentLimitNotified = true;
                        string fullPath = Path.GetFullPath(path);
                        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
                        string notice = JsonSerializer.Serialize(
                            new RepositoryImageTransportDocumentBudgetSignal(
                                "BudgetLimit", Environment.ProcessId, Volatile.Read(ref _imageTransportGeneration),
                                "Documents", 500, DateTimeOffset.UtcNow),
                            MarkdownLifecycleJsonContext.Default.RepositoryImageTransportDocumentBudgetSignal);
                        File.AppendAllText(fullPath, notice + Environment.NewLine);
                    }
                    return null;
                }
                ImageTransportDocuments.Add(documentKey);
                return new RepositoryImageFetchObserver(
                    identity, stage, Volatile.Read(ref _imageTransportGeneration), path,
                    Interlocked.Increment(ref _imageTransportOperationSequence)).Observe;
            }
        }
        catch
        {
            // Optional evidence cannot turn otherwise valid content into an
            // unavailable image, even if hashing or evidence setup fails.
            return null;
        }
    }

    private sealed class RepositoryImageFetchObserver(
        MarkdownRepositoryImageTransportIdentity identity,
        MarkdownRepositoryImageTransportStage stage,
        long generation,
        string evidencePath,
        long operationId)
    {
        private int _observations;

        internal void Observe(GitHubRawImageFetchOutcome outcome)
        {
            try
            {
                if (!IsEvidenceEnabled || generation != Volatile.Read(ref _imageTransportGeneration))
                    return;
                // Raw-image helpers emit one aggregate service result, including
                // cache hits and stale-on-failure results, not an HTTP outcome. Repository
                // fetchers can be retried by the unchanged three-attempt policy.
                // These are fetcher invocations, never asserted HTTP retry counts.
                int ordinal = Interlocked.Increment(ref _observations);
                if (ordinal > (stage == MarkdownRepositoryImageTransportStage.RawImageServiceResult ? 1 : 3))
                    return;
                var budgetIdentity = new MarkdownImageResolutionAuditDiagnostic(
                    "transport", identity.DocumentContextKind, identity.SourceKind,
                    "transport", identity.DocumentPathSha256, identity.DocumentRefIdentitySha256, null, null);
                string kind = Enum.IsDefined(outcome.Kind) ? outcome.Kind.ToString() : "OtherFailure";
                int? status = outcome.HttpStatusCode is >= 100 and <= 599 ? outcome.HttpStatusCode : null;
                lock (SignalGate)
                {
                    if (!IsEvidenceEnabled || generation != Volatile.Read(ref _imageTransportGeneration))
                        return;
                    bool success = outcome.Kind == GitHubRawImageFetchOutcomeKind.Success;
                    MarkdownImageResolutionAuditBudget budget = success ? ImageTransportSuccessSampleBudget : ImageTransportAuditBudget;
                    string entry;
                    if (!budget.TryReserve(budgetIdentity))
                    {
                        MarkdownImageResolutionAuditBudget notices = success ? ImageTransportSuccessLimitNotices : ImageTransportFailureLimitNotices;
                        if (!notices.TryReserve(budgetIdentity))
                            return;
                        entry = JsonSerializer.Serialize(
                            new RepositoryImageTransportBudgetSignal(
                                "BudgetLimit", Environment.ProcessId, generation,
                                success ? "SuccessSample" : "FailureRecords", success ? 32 : 512,
                                identity.DocumentContextKind, identity.DocumentPathSha256,
                                identity.DocumentRefIdentitySha256, DateTimeOffset.UtcNow),
                            MarkdownLifecycleJsonContext.Default.RepositoryImageTransportBudgetSignal);
                    }
                    else
                    {
                        entry = JsonSerializer.Serialize(
                            new RepositoryImageTransportSignal(
                                "Outcome", Environment.ProcessId, generation, operationId, ordinal, stage.ToString(), kind,
                                status, identity, DateTimeOffset.UtcNow),
                            MarkdownLifecycleJsonContext.Default.RepositoryImageTransportSignal);
                    }
                    string fullPath = Path.GetFullPath(evidencePath);
                    Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
                    File.AppendAllText(fullPath, entry + Environment.NewLine);
                }
            }
            catch
            {
                // This callback owns only hash/scalar metadata and its captured
                // evidence destination, never caller bytes, document context,
                // image URL, exception or resource leases.
            }
        }
    }

    public static void RecordImageResolution(
        string source,
        MarkdownRenderer.Images.MarkdownImageResolution resolution,
        DateTimeOffset startedAt,
        double elapsedMilliseconds,
        MarkdownSameByteResolverStageEvidence? sameByteResolverStages = null,
        MarkdownImageResolveContext? imageResolveContext = null)
    {
        if (!IsEvidenceEnabled)
        {
            return;
        }

        string? path = Environment.GetEnvironmentVariable(ImageResolutionEvidencePathVariable);
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        // Only audit launches pay the hash cost. The digest identifies the
        // exact resolved payload for same-byte replays without retaining it.
        string? contentSha256 = resolution.Asset is { } asset
            ? Convert.ToHexString(SHA256.HashData(asset.Bytes))
            : null;
        MarkdownImageResolutionAuditDiagnostic? resolutionDiagnostic = null;
        if (resolution.Asset is null && imageResolveContext is not null)
        {
            MarkdownImageResolutionAuditDiagnostic candidate = MarkdownImageResolutionAuditDiagnostic.Create(
                source,
                imageResolveContext,
                resolution);
            if (ImageResolutionAuditBudget.TryReserve(candidate))
                resolutionDiagnostic = candidate;
        }

        lock (SignalGate)
        {
            try
            {
                string fullPath = Path.GetFullPath(path);
                Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
                string entry = JsonSerializer.Serialize(
                    new ImageResolutionSignal(
                        Environment.ProcessId,
                        MarkdownImageResolutionAuditDiagnostic.HashSourceIdentity(source),
                        MarkdownImageResolutionAuditDiagnostic.GetSourceKind(source),
                        resolution.IsHandled,
                        resolution.Asset is not null,
                        resolution.Asset?.Bytes.Length ?? 0,
                        contentSha256,
                        resolution.Asset?.ContentType,
                        MarkdownImageResolutionAuditDiagnostic.HashResolvedUriIdentity(
                            resolution.Asset?.ResolvedUri),
                        resolution.UnavailableReason.ToString(),
                        startedAt,
                        elapsedMilliseconds,
                        DateTimeOffset.UtcNow,
                        sameByteResolverStages,
                        resolutionDiagnostic),
                    MarkdownLifecycleJsonContext.Default.ImageResolutionSignal);
                File.AppendAllText(fullPath, entry + Environment.NewLine);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    public static void RecordRasterPreparation(
        long preparationId,
        int stage,
        long sourceBytes,
        long packedDimensions,
        long elapsedStopwatchTicks)
    {
        if (!IsRasterPreparationEvidenceEnabled)
            return;

        string path = Environment.GetEnvironmentVariable(RasterPreparationEvidencePathVariable)!;
        lock (SignalGate)
        {
            try
            {
                string fullPath = Path.GetFullPath(path);
                Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
                string entry = JsonSerializer.Serialize(
                    new RasterPreparationSignal(
                        Environment.ProcessId,
                        preparationId,
                        stage,
                        sourceBytes,
                        (int)(packedDimensions >> 32),
                        (int)packedDimensions,
                        elapsedStopwatchTicks == 0
                            ? 0
                            : Stopwatch.GetElapsedTime(0, elapsedStopwatchTicks).TotalMilliseconds,
                        DateTimeOffset.UtcNow),
                    MarkdownLifecycleJsonContext.Default.RasterPreparationSignal);
                File.AppendAllText(fullPath, entry + Environment.NewLine);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
            catch (ArgumentException)
            {
            }
            catch (NotSupportedException)
            {
            }
        }
    }

    public static void RecordSvgWorkStage(long workId, long elapsedStopwatchTicks, long packedMetrics)
    {
        if (!IsSvgStageEvidenceEnabled || workId <= 0 || elapsedStopwatchTicks < 0)
            return;

        const int firstStageId = 1;
        // Stage 11-16 are opt-in device/bitmap/cache/dispatch details emitted by the
        // same Event 12 contract. Keep this bound synchronized with
        // MarkdownSvgStageTrace.
        const int lastStageId = 16;
        int stageId = (int)((packedMetrics >> 49) & 0x1F);
        if (stageId is < firstStageId or > lastStageId)
            return;

        long sourceBytes = (packedMetrics >> 25) & 0x00FF_FFFFL;
        long targetPixels = packedMetrics & 0x01FF_FFFFL;
        string path = Environment.GetEnvironmentVariable(SvgStageEvidencePathVariable)!;
        lock (SignalGate)
        {
            try
            {
                string fullPath = Path.GetFullPath(path);
                Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
                string entry = JsonSerializer.Serialize(
                    new SvgWorkStageSignal(
                        Environment.ProcessId,
                        12,
                        workId,
                        stageId,
                        sourceBytes,
                        targetPixels,
                        elapsedStopwatchTicks,
                        elapsedStopwatchTicks * (1000d / Stopwatch.Frequency),
                        DateTimeOffset.UtcNow),
                    MarkdownLifecycleJsonContext.Default.SvgWorkStageSignal);
                File.AppendAllText(fullPath, entry + Environment.NewLine);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
            catch (ArgumentException)
            {
            }
            catch (NotSupportedException)
            {
            }
        }
    }

    public static void RecordSvgWorkerStage(
        long workId,
        int stageId,
        long workerInstanceId,
        long requestId,
        long documentId,
        long elapsedStopwatchTicks,
        long workerCpuTicks,
        long sourceBytes,
        long targetPixels)
    {
        bool isSupportedStage = stageId is >= 101 and <= 109;
#if MARKDOWN_LAYOUT_DIAGNOSTICS
        isSupportedStage |= stageId is >= 110 and <= 120;
        if (stageId is >= 110 and <= 120 && (sourceBytes != 0 || targetPixels != 0))
        {
            return;
        }
#endif
        if (!IsSvgStageEvidenceEnabled || workId <= 0 || !isSupportedStage ||
            elapsedStopwatchTicks < 0 || workerCpuTicks < -1 || sourceBytes < 0 || targetPixels < 0)
        {
            return;
        }

#if MARKDOWN_LAYOUT_DIAGNOSTICS
        if (stageId is >= 110 and <= 116)
        {
            var phaseSignal = new SvgWorkerFontCatalogPhaseSignal(
                Environment.ProcessId,
                13,
                workId,
                stageId,
                workerInstanceId,
                requestId,
                documentId,
                elapsedStopwatchTicks,
                workerCpuTicks,
                sourceBytes,
                targetPixels,
                elapsedStopwatchTicks * (1000d / Stopwatch.Frequency),
                workerCpuTicks < 0 ? -1 : workerCpuTicks / 10_000d,
                Stopwatch.GetTimestamp(),
                Stopwatch.Frequency,
                DateTimeOffset.UtcNow);
            lock (SignalGate)
            {
                if (_deferredSvgStageEvidenceFlushed ||
                    _svgWorkerFontCatalogPhaseRecordsWritten >= MaximumSvgWorkerFontCatalogPhaseRecords)
                {
                    return;
                }

                SvgWorkerFontCatalogPhaseRecords.Add(phaseSignal);
                _svgWorkerFontCatalogPhaseRecordsWritten++;
            }

            return;
        }
#endif

        string path = Environment.GetEnvironmentVariable(SvgStageEvidencePathVariable)!;
        lock (SignalGate)
        {
            try
            {
                string fullPath = Path.GetFullPath(path);
                Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
                string entry = JsonSerializer.Serialize(
                    new SvgWorkerStageSignal(
                        Environment.ProcessId,
                        13,
                        workId,
                        stageId,
                        workerInstanceId,
                        requestId,
                        documentId,
                        elapsedStopwatchTicks,
                        workerCpuTicks,
                        sourceBytes,
                        targetPixels,
                        elapsedStopwatchTicks * (1000d / Stopwatch.Frequency),
                        workerCpuTicks < 0 ? -1 : workerCpuTicks / 10_000d,
                        DateTimeOffset.UtcNow),
                    MarkdownLifecycleJsonContext.Default.SvgWorkerStageSignal);
                File.AppendAllText(fullPath, entry + Environment.NewLine);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
            catch (ArgumentException)
            {
            }
            catch (NotSupportedException)
            {
            }
        }
    }

#if MARKDOWN_LAYOUT_DIAGNOSTICS
    public static void RecordSvgWorkerPolicy(MarkdownSvgWorkerPolicySnapshot snapshot)
    {
        if (!IsSvgStageEvidenceEnabled || snapshot.EventSequence <= 0 ||
            snapshot.StopwatchTicks <= 0 || snapshot.StopwatchFrequency <= 0 ||
            snapshot.VisibleOpenCount < 0 || snapshot.VisibleRenderCount < 0)
        {
            return;
        }

        string path = Environment.GetEnvironmentVariable(SvgStageEvidencePathVariable)!;
        lock (SignalGate)
        {
            try
            {
                string fullPath = Path.GetFullPath(path);
                Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
                string entry = JsonSerializer.Serialize(
                    new SvgWorkerPolicyEvidenceSignal(
                        14,
                        snapshot.EventSequence,
                        snapshot.DecisionSequence,
                        snapshot.Phase,
                        snapshot.ProcessId,
                        snapshot.StopwatchTicks,
                        snapshot.StopwatchFrequency,
                        snapshot.ProcessArchitecture,
                        snapshot.LogicalProcessorCount,
                        snapshot.EnergySaverOn,
                        snapshot.EnergySaverQueryFailed,
                        snapshot.CanUseSecondary,
                        snapshot.PrimaryBusy,
                        snapshot.PrimaryWorkerState,
                        snapshot.PrimaryWorkerInstanceId,
                        snapshot.PrimaryFontCatalogReady,
                        snapshot.VisibleOpenCount,
                        snapshot.VisibleRenderCount,
                        snapshot.SecondaryReadinessState,
                        snapshot.SecondaryBusy,
                        snapshot.SecondaryWorkerState,
                        snapshot.SecondaryWorkerInstanceId,
                        snapshot.SecondaryFontCatalogReady,
                        snapshot.ShouldStartSecondary,
                        DateTimeOffset.UtcNow),
                    MarkdownLifecycleJsonContext.Default.SvgWorkerPolicyEvidenceSignal);
                File.AppendAllText(fullPath, entry + Environment.NewLine);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
            catch (ArgumentException)
            {
            }
            catch (NotSupportedException)
            {
            }
        }
    }
#endif

    public static void RecordSvgWorkerTimeout(
        int stage,
        int deadlineMilliseconds,
        int workerProcessCpuMilliseconds,
        int transportPhase,
        int requestWriteMilliseconds,
        int workerExited,
        int openProgressPhase,
        int workerWorkingSetKiB,
        int workerPrivateCommitKiB,
        int workerPageFaults,
        int elapsedWallMilliseconds,
        string workerInputSha256,
        string workerExecutableSha256)
    {
        if (!IsEvidenceEnabled)
            return;

        string? path = Environment.GetEnvironmentVariable(SvgWorkerEvidencePathVariable);
        if (string.IsNullOrWhiteSpace(path))
            return;

        string phase = stage switch
        {
            0 => "process-startup",
            1 => "font-catalog",
            2 => "open",
            3 => "render",
            4 => "cache-trim",
            5 => "close-document",
            _ => "unknown",
        };
        string transport = transportPhase switch
        {
            0 => "request-write",
            1 => "pipe-flush",
            2 => "response-read",
            _ => "not-applicable",
        };
        string openProgress = openProgressPhase switch
        {
            0 => "not-started",
            1 => "mapping-opened",
            2 => "hash-verified",
            3 => "xml-parsed",
            4 => "security-inspected",
            5 => "font-gate-passed",
            6 => "cached-tree-acquired",
            7 => "document-attached",
            8 => "svg-options-constructed",
            9 => "svg-resolver-configured",
            10 => "svg-theme-ready",
            11 => "usvg-input-ready",
            12 => "usvg-tree-built",
            _ => "not-applicable",
        };
        lock (SignalGate)
        {
            try
            {
                string fullPath = Path.GetFullPath(path);
                Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
                string entry = JsonSerializer.Serialize(
                    new SvgWorkerTimeoutSignal(
                        Environment.ProcessId,
                        phase,
                        deadlineMilliseconds,
                        workerProcessCpuMilliseconds,
                        transport,
                        requestWriteMilliseconds,
                        workerExited switch { 0 => false, 1 => true, _ => null },
                        openProgress,
                        workerWorkingSetKiB,
                        workerPrivateCommitKiB,
                        workerPageFaults,
                        elapsedWallMilliseconds,
                        workerInputSha256,
                        workerExecutableSha256,
                        DateTimeOffset.UtcNow),
                    MarkdownLifecycleJsonContext.Default.SvgWorkerTimeoutSignal);
                File.AppendAllText(fullPath, entry + Environment.NewLine);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    public static void RecordSvgFontCatalogDeadlinePhase(string initializationPhase)
    {
        if (!IsEvidenceEnabled)
            return;

        string? path = Environment.GetEnvironmentVariable(SvgWorkerEvidencePathVariable);
        if (string.IsNullOrWhiteSpace(path))
            return;

        // A compromised or malformed worker response must not put source text
        // into an audit artifact. These are the only protocol phases.
        string phase = initializationPhase switch
        {
            "machine-fonts" or "user-local-fonts" or "user-roaming-fonts" or
            "font-family-defaults" or "text-pipeline-warmup" => initializationPhase,
            _ => "unknown",
        };
        lock (SignalGate)
        {
            try
            {
                string fullPath = Path.GetFullPath(path);
                Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
                string entry = JsonSerializer.Serialize(
                    new SvgFontCatalogDeadlinePhaseSignal(
                        Environment.ProcessId,
                        phase,
                        DateTimeOffset.UtcNow),
                    MarkdownLifecycleJsonContext.Default.SvgFontCatalogDeadlinePhaseSignal);
                File.AppendAllText(fullPath, entry + Environment.NewLine);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    public static void RecordSvgPreflightRejection(
        string reason,
        int sourceByteLength,
        string sourceSha256)
    {
        if (!IsEvidenceEnabled)
            return;

        string? path = Environment.GetEnvironmentVariable(SvgPreflightEvidencePathVariable);
        if (string.IsNullOrWhiteSpace(path))
            return;

        lock (SignalGate)
        {
            try
            {
                string fullPath = Path.GetFullPath(path);
                Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
                string entry = JsonSerializer.Serialize(
                    new SvgPreflightRejectionSignal(
                        Environment.ProcessId,
                        reason,
                        sourceByteLength,
                        sourceSha256,
                        DateTimeOffset.UtcNow),
                    MarkdownLifecycleJsonContext.Default.SvgPreflightRejectionSignal);
                File.AppendAllText(fullPath, entry + Environment.NewLine);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    public static void RecordRenderFailure(string automationId, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (!TargetsHost(automationId))
        {
            return;
        }

        RecordAuditFailure("markdown-render", exception);
    }

    /// <summary>
    /// Records a production-audit failure that prevents the targeted Markdown
    /// surface from being created. This is inert outside explicit automation
    /// launches and preserves the original exception for the audit report.
    /// </summary>
    public static void RecordAuditFailure(string stage, Exception exception)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stage);
        ArgumentNullException.ThrowIfNull(exception);
        if (!IsEvidenceEnabled)
        {
            return;
        }

        string? path = Environment.GetEnvironmentVariable(RenderFailureEvidencePathVariable);
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        lock (SignalGate)
        {
            try
            {
                string fullPath = Path.GetFullPath(path);
                Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
                File.WriteAllText(fullPath, $"Stage: {stage}{Environment.NewLine}{exception}");
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    public static void RecordRenderComplete(
        string automationId,
        long generation,
        DateTimeOffset completedAt,
        long stopwatchTicks,
        long stopwatchFrequency,
        MarkdownAuditPerformanceSnapshot? performance,
        string? visualProfileId = null,
        int? visualProfileVersion = null,
        string? visualProfileCssSha256 = null)
    {
        if (!TargetsHost(automationId) || generation <= 0)
        {
            return;
        }

        SameByteReadmeSourceAdmission? sourceAdmission = null;
        SameByteReadmeSourceAdmissionFailure? sourceAdmissionFailure = null;
        long? admissionEventGeneration = null;
        lock (SameByteReadmeSourceGate)
        {
            if (_sameByteReadmeSourceAdmission is { } admitted &&
                admitted.Generation == generation &&
                string.Equals(admitted.Host, automationId, StringComparison.Ordinal))
            {
                sourceAdmission = admitted;
            }

            if (_sameByteAdmissionEventGeneration > 0 &&
                _sameByteAdmissionEventGeneration <= generation &&
                string.Equals(_sameByteAdmissionEventHost, automationId, StringComparison.Ordinal))
            {
                admissionEventGeneration = _sameByteAdmissionEventGeneration;
            }

            if (_sameByteReadmeSourceAdmissionFailure is { } failure &&
                failure.Generation == generation &&
                string.Equals(failure.Host, automationId, StringComparison.Ordinal))
            {
                sourceAdmissionFailure = failure;
            }
        }

        if (IsSameByteAdmissionTraceEnabled)
            TraceSameByteAdmission($"render-complete:{automationId}:{generation}:admitted={(sourceAdmission is not null)}:failure={(sourceAdmissionFailure?.Status ?? "none")}");

        string? renderCompletePath = Environment.GetEnvironmentVariable(RenderCompleteEvidencePathVariable);
        var signal = new RenderCompleteSignal(
            Environment.ProcessId,
            automationId,
            generation,
            completedAt,
            stopwatchTicks,
            stopwatchFrequency,
            sourceAdmission,
            sourceAdmissionFailure,
            visualProfileId is not null &&
            visualProfileVersion is > 0 &&
            visualProfileCssSha256 is not null
                ? new VisualProfileSignal(
                    visualProfileId,
                    visualProfileVersion.Value,
                    visualProfileCssSha256)
                : null,
            performance,
            admissionEventGeneration);
        WriteSignal(renderCompletePath, signal, MarkdownLifecycleJsonContext.Default.RenderCompleteSignal);
        if (sourceAdmission is not null && !string.IsNullOrWhiteSpace(renderCompletePath))
        {
            // A later rebuild of the same document overwrites the render-complete
            // signal without an admission. Keep the admitted render where it
            // survives so the audit can prove the later generation still shows
            // the admitted bytes (T3-010).
            WriteSignal(
                renderCompletePath + AdmittedRenderCompleteSuffix,
                signal,
                MarkdownLifecycleJsonContext.Default.RenderCompleteSignal);
        }
    }

    public static Task<bool>? RecordFirstViewportImagesReady(
        string automationId,
        long generation,
        long viewportPaintGeneration,
        int pollCount,
        double probeWorkMilliseconds,
        double viewportTop,
        double viewportHeight,
        bool viewportMeasured,
        DateTimeOffset readyAt,
        long viewportPaintStopwatchTicks,
        CancellationToken generationCancellationToken)
    {
        if (!IsFirstViewportImagesReadyEvidenceEnabled ||
            !TargetsHost(automationId) ||
            generation <= 0 ||
            viewportPaintGeneration != generation ||
            pollCount <= 0)
        {
            return null;
        }

        SameByteReadmeSourceAdmission? sourceAdmission = null;
        lock (SameByteReadmeSourceGate)
        {
            if (_sameByteReadmeSourceAdmission is { } admitted &&
                admitted.ProcessId == Environment.ProcessId && admitted.Generation == generation &&
                string.Equals(admitted.Host, automationId, StringComparison.Ordinal))
                sourceAdmission = admitted;
        }

        // Capture the actual admitted source, never a harness-supplied digest
        // in place of rendered-byte proof. Ordinary audits retain null source
        // identity. These value records cannot keep a viewer or source alive.
        var publicationIdentity = new FirstViewportImagesReadyPublicationIdentity(
            Environment.ProcessId, automationId, generation, viewportPaintGeneration, sourceAdmission);
        string? progressPath = Environment.GetEnvironmentVariable(FirstViewportImagesReadyProgressPathVariable);
        try
        {
            return FirstViewportImagesReadyEvidenceWriter.TryQueueWrite(
                Environment.GetEnvironmentVariable(FirstViewportImagesReadyEvidencePathVariable),
                IsFirstViewportImagesReadyEvidenceEnabled,
                Environment.ProcessId,
                automationId,
                generation,
                viewportPaintGeneration,
                pollCount,
                probeWorkMilliseconds,
                viewportTop,
                viewportHeight,
                viewportMeasured,
                hasVisibleLoadingImages: false,
                readmeGitBlobSha1: SameByteReplayReadmeSha,
                timestamp: readyAt,
                stopwatchTicks: viewportPaintStopwatchTicks,
                stopwatchFrequency: Stopwatch.Frequency,
                generationCancellationToken: generationCancellationToken,
                publicationIdentity: publicationIdentity,
                publicationProgressPath: progressPath);
        }
        catch (Exception)
        {
            // Scheduling/resource failure is diagnostic-only and must not
            // escape the UI paint observer or poison a later generation.
            try
            {
                _ = FirstViewportImagesReadyEvidenceWriter.TryQueuePublicationOutcome(
                    progressPath, IsFirstViewportImagesReadyProgressEnabled,
                    new(1, "readiness-publication-outcome", publicationIdentity.ProcessId,
                        publicationIdentity.Host, publicationIdentity.Generation,
                        publicationIdentity.ViewportPaintGeneration, publicationIdentity.Source,
                        "faulted", DateTimeOffset.UtcNow));
            }
            catch (Exception) { }
            return Task.FromResult(false);
        }
    }

    public static bool TryReadCaptureRequest(
        string automationId,
        out MarkdownAuditCaptureRequest? request)
    {
        request = null;
        if (!TargetsHost(automationId))
            return false;

        string? path = Environment.GetEnvironmentVariable(CaptureRequestPathVariable);
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return false;

        try
        {
            using FileStream stream = new(
                Path.GetFullPath(path),
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            request = JsonSerializer.Deserialize(
                stream,
                MarkdownLifecycleJsonContext.Default.MarkdownAuditCaptureRequest);
            return request is { RequestId.Length: > 0 };
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static bool TryGetAuditScrollViewportFraction(
        MarkdownAuditCaptureRequest request,
        out double viewportFraction)
    {
        ArgumentNullException.ThrowIfNull(request);
        viewportFraction = 0;
        if (request.ScrollViewportFraction is not double requestedFraction)
            return false;

        if (!_productionAuditEnabled)
        {
            throw new InvalidOperationException(
                "Programmatic Markdown scrolling is available only during the explicit production README audit.");
        }

        if (!double.IsFinite(requestedFraction) || requestedFraction == 0 ||
            Math.Abs(requestedFraction) > MaximumAuditScrollViewportFraction)
        {
            throw new InvalidDataException(
                $"The audit scroll step must be finite, nonzero, and no larger than {MaximumAuditScrollViewportFraction:P0} of one viewport.");
        }

        viewportFraction = requestedFraction;
        return true;
    }

    public static bool TryGetAuditReadinessDiagnosticOnly(MarkdownAuditCaptureRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!request.ReadinessDiagnosticOnly)
            return false;

        if (!_productionAuditEnabled)
        {
            throw new InvalidOperationException(
                "Renderer readiness diagnostics are available only during the explicit production README audit.");
        }

        if (request.Save || request.OutputPath is not null || request.ScrollViewportFraction is not null)
        {
            throw new InvalidDataException(
                "A readiness-only audit request cannot save, capture, or scroll the Markdown viewport.");
        }

        return true;
    }

    public static void RecordCaptureResponse(
        string requestId,
        bool succeeded,
        int width,
        int height,
        double documentTop,
        string? error,
        MarkdownAuditPerformanceSnapshot? performance,
        double scrollOperationMilliseconds = 0,
        string? readinessDiagnostic = null)
    {
        WriteSignal(
            Environment.GetEnvironmentVariable(CaptureResponsePathVariable),
            new MarkdownAuditCaptureResponse(
                requestId,
                succeeded,
                width,
                height,
                documentTop,
                error,
                performance,
                scrollOperationMilliseconds,
                readinessDiagnostic),
            MarkdownLifecycleJsonContext.Default.MarkdownAuditCaptureResponse);
    }

    private static bool IsOne(string variable) => string.Equals(
        Environment.GetEnvironmentVariable(variable),
        "1",
        StringComparison.Ordinal);

    private static void WriteSignal<T>(string? path, T signal, JsonTypeInfo<T> jsonTypeInfo)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            string fullPath = Path.GetFullPath(path);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            string temporaryPath = fullPath + $".{Environment.ProcessId}.tmp";
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(signal, jsonTypeInfo));
            File.Move(temporaryPath, fullPath, overwrite: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed record LifecycleReadySignal(int ProcessId, string Stage, DateTimeOffset Timestamp);

    private sealed record ShutdownStageSignal(int ProcessId, string Stage, DateTimeOffset Timestamp);

    private sealed record ResourceMapFallbackSignal(int ProcessId, string Fallback, DateTimeOffset Timestamp);

    private sealed record LinkRouteSignal(
        int ProcessId,
        string Host,
        string Disposition,
        DateTimeOffset Timestamp);

    private sealed record ImageUnavailableSignal(
        int ProcessId,
        string Host,
        string SourceSha256,
        string SourceKind,
        string[] SourceMatchSha256,
        string Reason,
        string? SvgFailureReason,
        DateTimeOffset Timestamp);

    private sealed record RepositoryImageTransportSignal(
        string RecordType,
        int ProcessId,
        long AuditGeneration,
        long OperationId,
        int StageObservationOrdinal,
        string Stage,
        string Outcome,
        int? HttpStatusCode,
        MarkdownRepositoryImageTransportIdentity Identity,
        DateTimeOffset Timestamp);

    private sealed record RepositoryImageTransportBudgetSignal(
        string RecordType,
        int ProcessId,
        long AuditGeneration,
        string Budget,
        int MaximumRecordsPerDocument,
        string DocumentContextKind,
        string? DocumentPathSha256,
        string? DocumentRefIdentitySha256,
        DateTimeOffset Timestamp);

    private sealed record RepositoryImageTransportDocumentBudgetSignal(
        string RecordType,
        int ProcessId,
        long AuditGeneration,
        string Budget,
        int MaximumDocuments,
        DateTimeOffset Timestamp);

    private sealed record ImageResolutionSignal(
        int ProcessId,
        string SourceSha256,
        string SourceKind,
        bool IsHandled,
        bool HasAsset,
        int ByteLength,
        string? ContentSha256,
        string? ContentType,
        string? ResolvedUriSha256,
        string Reason,
        DateTimeOffset StartedAt,
        double ElapsedMilliseconds,
        DateTimeOffset Timestamp,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        MarkdownSameByteResolverStageEvidence? SameByteResolverStages,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        MarkdownImageResolutionAuditDiagnostic? ResolutionDiagnostic);

    private sealed record RasterPreparationSignal(
        int ProcessId,
        long PreparationId,
        int Stage,
        long SourceBytes,
        int SourceWidth,
        int SourceHeight,
        double ElapsedMilliseconds,
        DateTimeOffset Timestamp);

    private sealed record SvgWorkStageSignal(
        int ProcessId,
        int EventId,
        long WorkId,
        int StageId,
        long SourceBytes,
        long TargetPixels,
        long ElapsedStopwatchTicks,
        double ElapsedMilliseconds,
        DateTimeOffset Timestamp);

#if MARKDOWN_LAYOUT_DIAGNOSTICS
    private sealed record ImageActivationTraceSignal(
        int ProcessId,
        int EventId,
        long Generation,
        int ImageOrdinal,
        int StageId,
        long StopwatchTicks,
        long StopwatchFrequency,
        int ViewportMeasured,
        int IntersectsViewport,
        long ViewportWidthSixtyFourthDips,
        long ViewportHeightSixtyFourthDips);

    private sealed record SvgWorkerFontCatalogPhaseSignal(
        int ProcessId,
        int EventId,
        long WorkId,
        int StageId,
        long WorkerInstanceId,
        long RequestId,
        long DocumentId,
        long ElapsedStopwatchTicks,
        long WorkerCpuTicks,
        long SourceBytes,
        long TargetPixels,
        double ElapsedMilliseconds,
        double WorkerCpuMilliseconds,
        long StopwatchTicks,
        long StopwatchFrequency,
        DateTimeOffset Timestamp);
#endif

    private sealed record SvgWorkerStageSignal(
        int ProcessId,
        int EventId,
        long WorkId,
        int StageId,
        long WorkerInstanceId,
        long RequestId,
        long DocumentId,
        long ElapsedStopwatchTicks,
        long WorkerCpuTicks,
        long SourceBytes,
        long TargetPixels,
        double ElapsedMilliseconds,
        double WorkerCpuMilliseconds,
        DateTimeOffset Timestamp);

#if MARKDOWN_LAYOUT_DIAGNOSTICS
    private sealed record SvgWorkerPolicyEvidenceSignal(
        int EventId,
        long EventSequence,
        long DecisionSequence,
        int Phase,
        int ProcessId,
        long StopwatchTicks,
        long StopwatchFrequency,
        int ProcessArchitecture,
        int LogicalProcessorCount,
        int EnergySaverOn,
        int EnergySaverQueryFailed,
        int CanUseSecondary,
        int PrimaryBusy,
        int PrimaryWorkerState,
        long PrimaryWorkerInstanceId,
        int PrimaryFontCatalogReady,
        int VisibleOpenCount,
        int VisibleRenderCount,
        int SecondaryReadinessState,
        int SecondaryBusy,
        int SecondaryWorkerState,
        long SecondaryWorkerInstanceId,
        int SecondaryFontCatalogReady,
        int ShouldStartSecondary,
        DateTimeOffset Timestamp);
#endif

    private sealed record RenderCompleteSignal(
        int ProcessId,
        string Host,
        long Generation,
        DateTimeOffset Timestamp,
        long StopwatchTicks,
        long StopwatchFrequency,
        SameByteReadmeSourceAdmission? SameByteReadmeSourceAdmission,
        SameByteReadmeSourceAdmissionFailure? SameByteReadmeSourceAdmissionFailure,
        VisualProfileSignal? VisualProfile,
        MarkdownAuditPerformanceSnapshot? Performance,
        long? SameByteAdmissionEventGeneration = null);

    private sealed record VisualProfileSignal(
        string Id,
        int Version,
        string CssSha256);

    private sealed record SvgWorkerTimeoutSignal(
        int ProcessId,
        string Phase,
        int DeadlineMilliseconds,
        int WorkerProcessCpuMilliseconds,
        string TransportPhase,
        int RequestWriteMilliseconds,
        bool? WorkerExited,
        string OpenProgressPhase,
        int WorkerWorkingSetKiB,
        int WorkerPrivateCommitKiB,
        int WorkerPageFaults,
        int ElapsedWallMilliseconds,
        string WorkerInputSha256,
        string WorkerExecutableSha256,
        DateTimeOffset Timestamp);

    private sealed record SvgFontCatalogDeadlinePhaseSignal(
        int ProcessId,
        string InitializationPhase,
        DateTimeOffset Timestamp);

    private sealed record SameByteReadmeSourceSignal(
        int ByteSize,
        string Sha256,
        int DecodedTextUtf8ByteSize,
        string DecodedTextSha256,
        string ReadmeGitBlobSha1);

    private sealed record SvgPreflightRejectionSignal(
        int ProcessId,
        string Reason,
        int SourceByteLength,
        string SourceSha256,
        DateTimeOffset Timestamp);

    internal sealed record MarkdownAuditPerformanceSnapshot(
        long SourceCacheBytes,
        long SourceCacheHits,
        long ImageFetches,
        long ImageFetchMilliseconds,
        long ImageFetchFailures,
        long ImageFetchCancellations,
        long SourceCacheEvictions,
        int PendingImageFetches,
        int ActiveImageFetches,
        long CpuPreparations,
        long CpuPreparationMilliseconds,
        long ScenePreparations,
        long ScenePreparationMilliseconds,
        long InFlightSourceBytes,
        long PeakInFlightSourceBytes,
        int PendingSourceByteRequests,
        MarkdownAuditPipelineTimingSnapshot Pipeline);

    internal sealed record MarkdownAuditPipelineTimingSnapshot(
        long Generation,
        long SourceUtf16Bytes,
        double ParseMilliseconds,
        double EngineParseAndCacheMilliseconds,
        double LegacyParseAndDocumentMilliseconds,
        double ProgressiveScenePlanMilliseconds,
        double StyleRoleDemandMilliseconds,
        double ParseSessionTotalMilliseconds,
        double ParseResumeAndAdapterMilliseconds,
        double SetupMilliseconds,
        double ThemeSnapshotMilliseconds,
        double LayoutMilliseconds,
        double LayoutCpuMilliseconds,
        double LayoutQueueMilliseconds,
        double LayoutWorkerWallMilliseconds,
        double LayoutContinuationMilliseconds,
        int LayoutTopLevelBlockCount,
        int CanvasTextLayoutConstructorCalls,
        double CanvasTextLayoutConstructorMilliseconds,
        double LayoutBlockConstructionMilliseconds,
        double LayoutMeasureArrangeMilliseconds,
        double LayoutSnapshotConstructionMilliseconds,
        bool InitialLayoutDispatcherSentinelEnqueued,
        bool InitialLayoutDispatcherSentinelDispatched,
        double InitialLayoutDispatcherSentinelDelayMilliseconds,
        int InitialLayoutDispatcherHeartbeatSampleCount,
        double InitialLayoutDispatcherHeartbeatMaxGapMilliseconds,
        double PublicationMilliseconds,
        double CommitMilliseconds,
        double OverlayResetMilliseconds,
        double PlanConstructionMilliseconds,
        double VisibleRealizationMilliseconds,
        double EmbedRealizationMilliseconds,
        double HighlightSchedulingMilliseconds,
        double HighlightRetirementMilliseconds,
        double HighlightBandSchedulingMilliseconds,
        double AdornmentFocusMilliseconds,
        double FinalNotificationMilliseconds);

    internal readonly record struct MarkdownAuditParsePreparationTiming(
        double EngineParseAndCacheMilliseconds,
        double LegacyParseAndDocumentMilliseconds,
        double ProgressiveScenePlanMilliseconds,
        double StyleRoleDemandMilliseconds,
        double SessionTotalMilliseconds);

    internal sealed record MarkdownAuditCaptureRequest(
        string RequestId,
        string? OutputPath,
        bool Save,
        double? ScrollViewportFraction = null,
        bool ReadinessDiagnosticOnly = false);

    private sealed record MarkdownAuditCaptureResponse(
        string RequestId,
        bool Succeeded,
        int Width,
        int Height,
        double DocumentTop,
        string? Error,
        MarkdownAuditPerformanceSnapshot? Performance,
        double ScrollOperationMilliseconds,
        string? ReadinessDiagnostic);

    private sealed record MarkdownLifecycleRuntimeSettings(double TextScaleFactor, int Revision);

    [JsonSerializable(typeof(LifecycleReadySignal), TypeInfoPropertyName = "LifecycleReadySignal")]
    [JsonSerializable(typeof(ShutdownStageSignal), TypeInfoPropertyName = "ShutdownStageSignal")]
    [JsonSerializable(typeof(ResourceMapFallbackSignal), TypeInfoPropertyName = "ResourceMapFallbackSignal")]
    [JsonSerializable(typeof(LinkRouteSignal), TypeInfoPropertyName = "LinkRouteSignal")]
    [JsonSerializable(typeof(ImageUnavailableSignal), TypeInfoPropertyName = "ImageUnavailableSignal")]
    [JsonSerializable(typeof(ImageResolutionSignal), TypeInfoPropertyName = "ImageResolutionSignal")]
    [JsonSerializable(typeof(RepositoryImageTransportSignal), TypeInfoPropertyName = "RepositoryImageTransportSignal")]
    [JsonSerializable(typeof(RepositoryImageTransportBudgetSignal), TypeInfoPropertyName = "RepositoryImageTransportBudgetSignal")]
    [JsonSerializable(typeof(RepositoryImageTransportDocumentBudgetSignal), TypeInfoPropertyName = "RepositoryImageTransportDocumentBudgetSignal")]
    [JsonSerializable(typeof(MarkdownImageResolutionAuditDiagnostic))]
    [JsonSerializable(typeof(RasterPreparationSignal), TypeInfoPropertyName = "RasterPreparationSignal")]
    [JsonSerializable(typeof(SvgWorkStageSignal), TypeInfoPropertyName = "SvgWorkStageSignal")]
#if MARKDOWN_LAYOUT_DIAGNOSTICS
    [JsonSerializable(typeof(ImageActivationTraceSignal), TypeInfoPropertyName = "ImageActivationTraceSignal")]
    [JsonSerializable(typeof(SvgWorkerFontCatalogPhaseSignal), TypeInfoPropertyName = "SvgWorkerFontCatalogPhaseSignal")]
#endif
    [JsonSerializable(typeof(SvgWorkerStageSignal), TypeInfoPropertyName = "SvgWorkerStageSignal")]
#if MARKDOWN_LAYOUT_DIAGNOSTICS
    [JsonSerializable(typeof(SvgWorkerPolicyEvidenceSignal), TypeInfoPropertyName = "SvgWorkerPolicyEvidenceSignal")]
#endif
    [JsonSerializable(typeof(RenderCompleteSignal), TypeInfoPropertyName = "RenderCompleteSignal")]
    [JsonSerializable(typeof(SvgWorkerTimeoutSignal), TypeInfoPropertyName = "SvgWorkerTimeoutSignal")]
    [JsonSerializable(typeof(SvgFontCatalogDeadlinePhaseSignal), TypeInfoPropertyName = "SvgFontCatalogDeadlinePhaseSignal")]
    [JsonSerializable(typeof(SameByteReadmeSourceSignal), TypeInfoPropertyName = "SameByteReadmeSourceSignal")]
[JsonSerializable(typeof(SameByteReadmeSourceAdmission), TypeInfoPropertyName = "SameByteReadmeSourceAdmission")]
[JsonSerializable(typeof(SameByteReadmeSourceAdmissionFailure), TypeInfoPropertyName = "SameByteReadmeSourceAdmissionFailure")]
    [JsonSerializable(typeof(SvgPreflightRejectionSignal), TypeInfoPropertyName = "SvgPreflightRejectionSignal")]
    [JsonSerializable(typeof(MarkdownAuditPerformanceSnapshot), TypeInfoPropertyName = "MarkdownAuditPerformanceSnapshot")]
    [JsonSerializable(typeof(MarkdownAuditPipelineTimingSnapshot), TypeInfoPropertyName = "MarkdownAuditPipelineTimingSnapshot")]
    [JsonSerializable(typeof(MarkdownAuditCaptureRequest), TypeInfoPropertyName = "MarkdownAuditCaptureRequest")]
    [JsonSerializable(typeof(MarkdownAuditCaptureResponse), TypeInfoPropertyName = "MarkdownAuditCaptureResponse")]
    [JsonSerializable(typeof(MarkdownLifecycleRuntimeSettings), TypeInfoPropertyName = "RuntimeSettings")]
    private sealed partial class MarkdownLifecycleJsonContext : JsonSerializerContext
    {
    }
}

internal sealed record SameByteReadmeSourceIdentity(
    int RawByteSize,
    string RawSha256,
    int DecodedTextUtf8ByteSize,
    string DecodedTextSha256,
    string ReadmeGitBlobSha1)
{
    // Weak so audit identity never extends a large README's lifetime.
    internal WeakReference<string>? DecodedMarkdownReference { get; set; }
}

/// <summary>
/// Captures the source-available boundary before Markdown assignment. The
/// renderer can be unloaded at that point, so its eventual generation must
/// be bound only when the matching document actually completes rendering.
/// </summary>
internal sealed record PendingSameByteReadmeSourceAdmission(
    string Markdown,
    string? DocumentPath,
    MarkdownDocumentSource? DocumentSource,
    SameByteReadmeSourceIdentity? Identity,
    string FailureReason,
    long StopwatchTicks,
    long StopwatchFrequency)
{
    /// <summary>
    /// Pipeline generation requested by the Markdown assignment. Zero means
    /// the renderer was unloaded and the assignment could not be associated
    /// with a generation; that state must fail closed instead of binding to an
    /// arbitrary later restyle.
    /// </summary>
    internal long AssignmentPipelineGeneration { get; init; }

    internal bool Matches(string markdown, string? documentPath, MarkdownDocumentSource? documentSource) =>
        string.Equals(Markdown, markdown, StringComparison.Ordinal) &&
        string.Equals(DocumentPath, documentPath, StringComparison.Ordinal) &&
        HasSameDocumentSource(DocumentSource, documentSource);

    internal bool IsBoundToPipelineGeneration(long generation) =>
        AssignmentPipelineGeneration > 0 &&
        generation == AssignmentPipelineGeneration;

    private static bool HasSameDocumentSource(
        MarkdownDocumentSource? expected,
        MarkdownDocumentSource? actual)
    {
        if (expected is null || actual is null)
            return expected is null && actual is null;

        // DocumentId is the canonical document identity. Repository sources
        // created by MarkdownDocumentSourceFactory bind owner/repository/ref/
        // path/tree into it, so a fresh metadata record for the same document
        // must not discard the frozen source-admission boundary. Compare the
        // resolved context as well so malformed/reused IDs cannot carry an
        // admission across a different repository or relative-asset context.
        return string.Equals(expected.DocumentId, actual.DocumentId, StringComparison.Ordinal) &&
            string.Equals(expected.Owner, actual.Owner, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(expected.Repository, actual.Repository, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(expected.Ref, actual.Ref, StringComparison.Ordinal) &&
            string.Equals(expected.Path, actual.Path, StringComparison.Ordinal);
    }
}

internal sealed record SameByteReadmeSourceAdmission(
    int ProcessId,
    string Host,
    long Generation,
    int RawByteSize,
    string RawSha256,
    int DecodedTextUtf8ByteSize,
    string DecodedTextSha256,
    string ReadmeGitBlobSha1,
    long StopwatchTicks,
    long StopwatchFrequency,
    string? SourceOwner = null,
    string? SourceRepository = null,
    string? SourceRef = null,
    string? SourcePath = null);

internal sealed record SameByteReadmeSourceAdmissionFailure(
    string Host,
    long Generation,
    string Status);

internal sealed record MarkdownShutdownAuditPerformanceSnapshot(
    int PendingImageFetches,
    int ActiveImageFetches,
    long ScenePreparationsTotal,
    int PendingSourceByteRequests,
    long InFlightSourceBytes);
