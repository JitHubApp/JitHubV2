using System.Text.Json.Serialization;

namespace MarkdownRenderer.PerformanceHarness;

/// <summary>
/// Separate, opt-in, non-gating diagnostic output for attributing the 1 MiB
/// first-viewport pipeline. This deliberately does not extend the release
/// report schema.
/// </summary>
internal sealed class FirstViewportStageProfileArtifact
{
    internal const int CurrentSchemaVersion = 1;
    internal const string ProfileKind = "markdown-first-viewport-stage-profile";

    public int SchemaVersion { get; init; }
    public string Kind { get; init; } = string.Empty;
    public string EvidenceClassification { get; init; } = string.Empty;
    public string BuildIdentity { get; init; } = string.Empty;
    public string PerformanceReportPath { get; init; } = string.Empty;
    public BuildArtifactHashes BuildArtifacts { get; init; } = new();
    public DateTimeOffset StartedUtc { get; init; }
    public DateTimeOffset CompletedUtc { get; init; }
    public int SourceUtf16Bytes { get; init; }
    public string Corpus { get; init; } = string.Empty;
    public bool LayoutDiagnosticsEnabled { get; init; }
    public string AllocationScope { get; init; } = string.Empty;
    public List<FirstViewportStageProfileSample> Presentations { get; init; } = [];
}

internal sealed class FirstViewportStageProfileSample
{
    public string Corpus { get; init; } = string.Empty;
    public string Mode { get; init; } = string.Empty;
    public int SourceUtf16Bytes { get; init; }
    public string TrialKind { get; init; } = string.Empty;
    public int TrialOrdinal { get; init; }
    public int GlobalOrdinal { get; init; }
    public int ScheduleRow { get; init; }
    public int SchedulePosition { get; init; }
    public string PresentationRole { get; init; } = string.Empty;
    public int SampleIndex { get; init; }
    public long Generation { get; init; }
    public double FirstViewportMilliseconds { get; init; }
    public double ParseMilliseconds { get; init; }
    public double SetupMilliseconds { get; init; }
    public double ThemeSnapshotMilliseconds { get; init; }
    public double LayoutMilliseconds { get; init; }
    public double LayoutCpuMilliseconds { get; init; }
    public double LayoutQueueMilliseconds { get; init; }
    public double LayoutWorkerWallMilliseconds { get; init; }
    public double LayoutContinuationMilliseconds { get; init; }
    public double PublicationMilliseconds { get; init; }
    public double? PublicationCommitMilliseconds { get; init; }
    public double? PublicationOverlayResetMilliseconds { get; init; }
    public double? PublicationPlanConstructionMilliseconds { get; init; }
    public double? PublicationEmbedRealizationMilliseconds { get; init; }
    public double? PublicationHighlightRetirementMilliseconds { get; init; }
    public double? PublicationHighlightSchedulingMilliseconds { get; init; }
    public double? PublicationVisibleRealizationMilliseconds { get; init; }
    public double? PublicationFinalizationMilliseconds { get; init; }
    public long ProcessAllocatedBytes { get; init; }
    public long? UiThreadAllocatedBytes { get; init; }
#if MARKDOWN_LAYOUT_DIAGNOSTICS
    public int? LayoutTopLevelBlockCount { get; init; }
    public int? CanvasTextLayoutConstructorCalls { get; init; }
    public double? CanvasTextLayoutConstructorMilliseconds { get; init; }
    public double? LayoutBlockConstructionMilliseconds { get; init; }
    public double? LayoutMeasureArrangeMilliseconds { get; init; }
    public double? LayoutSnapshotConstructionMilliseconds { get; init; }
    public bool? InitialLayoutDispatcherSentinelEnqueued { get; init; }
    public bool? InitialLayoutDispatcherSentinelDispatched { get; init; }
    public double? InitialLayoutDispatcherSentinelDelayMilliseconds { get; init; }
    public int? InitialLayoutDispatcherHeartbeatSampleCount { get; init; }
    public double? InitialLayoutDispatcherHeartbeatMaxGapMilliseconds { get; init; }
#endif
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true)]
[JsonSerializable(typeof(FirstViewportStageProfileArtifact))]
internal partial class FirstViewportStageProfileJsonContext : JsonSerializerContext
{
}
