using System.Diagnostics;
using System.Text.Json;
using MarkdownRenderer.Controls;

namespace MarkdownRenderer.PerformanceHarness;

/// <summary>
/// Collects the opt-in, quick-run-only first-viewport stage profile and writes
/// it as a separate diagnostic sidecar next to the performance report. It is
/// enabled by <c>MARKDOWNRENDERER_PERF_STAGE_PROFILE_PATH</c> and never changes
/// the release report schema.
/// </summary>
internal sealed class FirstViewportStageProfileRecorder
{
    private const string StageProfilePathEnvironmentVariable =
        "MARKDOWNRENDERER_PERF_STAGE_PROFILE_PATH";

    private readonly string? _path;
    private readonly List<FirstViewportStageProfileSample>? _samples;

    private FirstViewportStageProfileRecorder(
        string? path,
        string? configurationError,
        int sourceUtf16Bytes)
    {
        _path = path;
        ConfigurationError = configurationError;
        SourceUtf16Bytes = sourceUtf16Bytes;
        _samples = path is null ? null : [];
    }

    /// <summary>The resolved sidecar path, or null when profiling is off.</summary>
    internal string? Path => _path;

    /// <summary>Why the profile configuration was rejected, or null when it is valid.</summary>
    internal string? ConfigurationError { get; }

    /// <summary>The source size whose presentations are profiled.</summary>
    internal int SourceUtf16Bytes { get; }

    /// <summary>True when profiling is on and a sidecar will be written.</summary>
    internal bool IsEnabled => _path is not null && _samples is not null;

    /// <summary>True when presentations of <paramref name="sourceBytes"/> are profiled.</summary>
    internal bool Captures(int sourceBytes)
        => _samples is not null && sourceBytes == SourceUtf16Bytes;

    internal static FirstViewportStageProfileRecorder Create(PerformanceOptions options)
    {
        string? path = ResolvePath(
            options,
            out string? configurationError,
            out int sourceUtf16Bytes);
        return new FirstViewportStageProfileRecorder(path, configurationError, sourceUtf16Bytes);
    }

    /// <summary>
    /// Records one profiled presentation.
    /// </summary>
    /// <remarks>
    /// This method stays whole because it is one flat mapping from the
    /// presentation's timing snapshot to the sidecar sample; each property is
    /// a single field copy or timestamp delta, and splitting the initializer
    /// would only scatter that one mapping.
    /// </remarks>
    internal void Record(
        FirstViewportTrialSlot slot,
        string presentationRole,
        int sampleIndex,
        FirstViewportPresentation presentation)
    {
        FirstViewportScenario scenario = slot.Scenario;
        List<FirstViewportStageProfileSample>? samples = _samples;
        FirstViewportStageProfileMeasurements? profile = presentation.StageProfileMeasurements;
        if (samples is null ||
            scenario.SourceBytes != SourceUtf16Bytes ||
            profile is null)
            return;

        MarkdownPipelineTimingSnapshot timing = profile.Timing;
        samples.Add(new FirstViewportStageProfileSample
        {
            Corpus = PerformanceDocumentFactory.GetCorpusName(scenario.SourceBytes),
            Mode = scenario.Mode,
            SourceUtf16Bytes = scenario.SourceBytes,
            TrialKind = slot.IsWarmup ? "warmup" : "measured",
            TrialOrdinal = slot.Ordinal,
            GlobalOrdinal = slot.GlobalOrdinal,
            ScheduleRow = slot.ScheduleRow,
            SchedulePosition = slot.SchedulePosition,
            PresentationRole = presentationRole,
            SampleIndex = sampleIndex,
            Generation = presentation.FirstViewport.Generation,
            FirstViewportMilliseconds = presentation.FirstViewport.ElapsedMilliseconds,
            ParseMilliseconds = timing.ParseMilliseconds,
            SetupMilliseconds = timing.SetupMilliseconds,
            ThemeSnapshotMilliseconds = timing.ThemeSnapshotMilliseconds,
            LayoutMilliseconds = timing.LayoutMilliseconds,
            LayoutCpuMilliseconds = timing.LayoutCpuMilliseconds,
            LayoutQueueMilliseconds = timing.LayoutQueueMilliseconds,
            LayoutWorkerWallMilliseconds = timing.LayoutWorkerWallMilliseconds,
            LayoutContinuationMilliseconds = timing.LayoutContinuationMilliseconds,
            PublicationMilliseconds = presentation.PublicationMilliseconds,
            PublicationCommitMilliseconds = GetTimestampDeltaMilliseconds(
                timing.PublicationStartedTimestamp,
                timing.CommitEndedTimestamp),
            PublicationOverlayResetMilliseconds = GetTimestampDeltaMilliseconds(
                timing.CommitEndedTimestamp,
                timing.OverlayResetEndedTimestamp),
            PublicationPlanConstructionMilliseconds = GetTimestampDeltaMilliseconds(
                timing.OverlayResetEndedTimestamp,
                timing.PlanConstructionEndedTimestamp),
            PublicationEmbedRealizationMilliseconds = GetTimestampDeltaMilliseconds(
                timing.PlanConstructionEndedTimestamp,
                timing.EmbedRealizationEndedTimestamp),
            PublicationHighlightRetirementMilliseconds = GetTimestampDeltaMilliseconds(
                timing.EmbedRealizationEndedTimestamp,
                timing.HighlightRetirementEndedTimestamp),
            PublicationHighlightSchedulingMilliseconds = GetTimestampDeltaMilliseconds(
                timing.HighlightRetirementEndedTimestamp,
                timing.HighlightSchedulingEndedTimestamp),
            PublicationVisibleRealizationMilliseconds = GetTimestampDeltaMilliseconds(
                timing.HighlightSchedulingEndedTimestamp,
                timing.VisibleRealizationEndedTimestamp),
            PublicationFinalizationMilliseconds = GetTimestampDeltaMilliseconds(
                timing.VisibleRealizationEndedTimestamp,
                timing.PublicationEndedTimestamp),
            ProcessAllocatedBytes = profile.ProcessAllocatedBytes,
            UiThreadAllocatedBytes = profile.UiThreadAllocatedBytes,
            UiThreadCpuThroughFirstViewportMilliseconds =
                profile.ThreadMetrics?.UiThreadCpuMilliseconds,
            ProcessGen0CollectionsThroughFirstViewport =
                profile.ThreadMetrics?.ProcessGen0Collections,
            ProcessGen1CollectionsThroughFirstViewport =
                profile.ThreadMetrics?.ProcessGen1Collections,
            ProcessGen2CollectionsThroughFirstViewport =
                profile.ThreadMetrics?.ProcessGen2Collections,
            LayoutTopLevelBlockCount = timing.LayoutTopLevelBlockCount,
            CanvasTextLayoutConstructorCalls = timing.CanvasTextLayoutConstructorCalls,
            CanvasTextLayoutConstructorMilliseconds = timing.CanvasTextLayoutConstructorMilliseconds,
            LayoutBlockConstructionMilliseconds = timing.LayoutBlockConstructionMilliseconds,
            LayoutMeasureArrangeMilliseconds = timing.LayoutMeasureArrangeMilliseconds,
            LayoutSnapshotConstructionMilliseconds = timing.LayoutSnapshotConstructionMilliseconds,
            InitialLayoutDispatcherSentinelEnqueued = timing.InitialLayoutDispatcherSentinelEnqueued,
            InitialLayoutDispatcherSentinelDispatched = timing.InitialLayoutDispatcherSentinelDispatched,
            InitialLayoutDispatcherSentinelDelayMilliseconds =
                timing.InitialLayoutDispatcherSentinelDelayMilliseconds,
            InitialLayoutDispatcherHeartbeatSampleCount =
                timing.InitialLayoutDispatcherHeartbeatSampleCount,
            InitialLayoutDispatcherHeartbeatMaxGapMilliseconds =
                timing.InitialLayoutDispatcherHeartbeatMaxGapMilliseconds,
        });
    }

    /// <summary>
    /// Writes the sidecar. The file must not exist yet; it is never overwritten.
    /// </summary>
    internal void WriteArtifact(PerformanceReport report, string performanceReportPath)
    {
        string path = _path ??
            throw new InvalidOperationException("No stage profile path was configured.");
        List<FirstViewportStageProfileSample> samples = _samples ??
            throw new InvalidOperationException("Stage profile collection was not initialized.");
        string? directory = System.IO.Path.GetDirectoryName(path);
        if (string.IsNullOrWhiteSpace(directory))
            throw new InvalidOperationException("Stage profile path must have a parent directory.");

        Directory.CreateDirectory(directory);
        using FileStream stream = new(
            path,
            new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                Options = FileOptions.SequentialScan,
            });
        JsonSerializer.Serialize(
            stream,
            new FirstViewportStageProfileArtifact
            {
                SchemaVersion = FirstViewportStageProfileArtifact.CurrentSchemaVersion,
                Kind = FirstViewportStageProfileArtifact.ProfileKind,
                EvidenceClassification = "quick-non-gating-diagnostic",
                BuildIdentity = report.BuildIdentity,
                PerformanceReportPath = performanceReportPath,
                BuildArtifacts = CopyBuildArtifactHashes(report.BuildArtifacts),
                StartedUtc = report.StartedUtc,
                CompletedUtc = report.CompletedUtc,
                SourceUtf16Bytes = SourceUtf16Bytes,
                Corpus = PerformanceDocumentFactory.GetCorpusName(SourceUtf16Bytes),
                LayoutDiagnosticsEnabled = true,
                AllocationScope =
                    "Process allocated bytes from immediately before MarkdownScrollView construction through " +
                    "the first-viewport event and timing snapshot; includes all process threads and the " +
                    "opt-in diagnostic capture/observation allocations, and is not attributed to individual " +
                    "stages. UI-thread bytes cover the same interval only when the " +
                    "continuation returns to the initiating managed thread; otherwise they are null. UI-thread " +
                    "CPU is measured on that thread from immediately before control construction through the " +
                    "synchronous first-viewport event; it is not a per-publication-stage CPU measurement. " +
                    "GC collection deltas are process-wide over the same event boundary and are coincident, " +
                    "not attributed to this presentation.",
                Presentations = samples,
            },
            FirstViewportStageProfileJsonContext.Default.FirstViewportStageProfileArtifact);
        stream.Flush(flushToDisk: true);
    }

    private static double? GetTimestampDeltaMilliseconds(long start, long end)
        => start > 0 && end >= start
            ? Stopwatch.GetElapsedTime(start, end).TotalMilliseconds
            : null;

    private static string? ResolvePath(
        PerformanceOptions options,
        out string? configurationError,
        out int sourceUtf16Bytes)
    {
        configurationError = null;
        sourceUtf16Bytes = FirstViewportStageProfileSelection.DefaultSourceUtf16Bytes;
        string? configuredPath = Environment.GetEnvironmentVariable(
            StageProfilePathEnvironmentVariable);
        string? configuredSourceBytes = Environment.GetEnvironmentVariable(
            FirstViewportStageProfileSelection.SourceBytesEnvironmentVariable);
        try
        {
            FirstViewportStageProfileSelection selection =
                FirstViewportStageProfileSelection.Resolve(
                    options.Quick,
                    !string.IsNullOrWhiteSpace(configuredPath),
                    configuredSourceBytes);
            sourceUtf16Bytes = selection.SourceUtf16Bytes;
        }
        catch (ArgumentException exception)
        {
            configurationError = exception.Message;
            return null;
        }

        if (string.IsNullOrWhiteSpace(configuredPath))
            return null;

        try
        {
            string fullPath = System.IO.Path.GetFullPath(configuredPath);
            string executablePath = Environment.ProcessPath ??
                throw new InvalidOperationException("The performance executable path is unavailable.");
            if (PerformanceDeploymentIdentity.IsPathWithinDeploymentDirectory(fullPath, executablePath))
            {
                throw new ArgumentException(
                    $"{StageProfilePathEnvironmentVariable} must be outside the executable deployment directory.");
            }
            if (string.Equals(fullPath, options.OutputPath, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(fullPath, options.ReferencePath, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException(
                    $"{StageProfilePathEnvironmentVariable} must be distinct from the primary report and reference paths.");
            }
            if (File.Exists(fullPath) || Directory.Exists(fullPath))
            {
                throw new IOException(
                    $"Stage profile path '{fullPath}' already exists and will not be overwritten.");
            }

            return fullPath;
        }
        catch (Exception exception) when (
            exception is ArgumentException or IOException or UnauthorizedAccessException or
                NotSupportedException or InvalidOperationException)
        {
            configurationError =
                $"Invalid {StageProfilePathEnvironmentVariable} configuration: {exception.Message}";
            return null;
        }
    }

    private static BuildArtifactHashes CopyBuildArtifactHashes(BuildArtifactHashes source)
        => new()
        {
            HashAlgorithm = source.HashAlgorithm,
            Executable = CopyBuildArtifactHash(source.Executable),
            PerformanceHarnessDll = CopyBuildArtifactHash(source.PerformanceHarnessDll),
            MarkdownRendererDll = CopyBuildArtifactHash(source.MarkdownRendererDll),
            RuntimeConfig = CopyBuildArtifactHash(source.RuntimeConfig),
        };

    private static BuildArtifactHash CopyBuildArtifactHash(BuildArtifactHash source)
        => new()
        {
            FileName = source.FileName,
            Path = source.Path,
            Sha256 = source.Sha256,
        };
}
