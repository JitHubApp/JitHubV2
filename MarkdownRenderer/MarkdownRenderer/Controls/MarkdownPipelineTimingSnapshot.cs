using MarkdownRenderer.Layout;

namespace MarkdownRenderer.Controls;

/// <summary>
/// Internal wall-clock stages of the last committed initial presentation.
/// Parse includes extension work; setup includes theme/context and work-plan
/// construction; layout includes worker scheduling; UI publication includes
/// realization and invalidation, but not the first paint. Publication's five
/// phases partition that wall interval so slow commits can be attributed.
/// Raw publication timestamps defer audit-only conversion until evidence
/// capture, keeping the normal UI commit path allocation-free and small.
/// </summary>
internal readonly struct MarkdownPipelineTimingSnapshot
{
    internal readonly long Generation;
    internal readonly long SourceUtf16Bytes;
    internal readonly double ParseMilliseconds;
    internal readonly double SetupMilliseconds;
    internal readonly double ThemeSnapshotMilliseconds;
    internal readonly double LayoutMilliseconds;
    internal readonly double LayoutCpuMilliseconds;
    internal readonly double LayoutQueueMilliseconds;
    internal readonly double LayoutWorkerWallMilliseconds;
    internal readonly double LayoutContinuationMilliseconds;
#if MARKDOWN_LAYOUT_DIAGNOSTICS
    internal readonly int LayoutTopLevelBlockCount;
    internal readonly int CanvasTextLayoutConstructorCalls;
    internal readonly double CanvasTextLayoutConstructorMilliseconds;
    internal readonly double LayoutBlockConstructionMilliseconds;
    internal readonly double LayoutMeasureArrangeMilliseconds;
    internal readonly double LayoutSnapshotConstructionMilliseconds;
    internal readonly bool InitialLayoutDispatcherSentinelEnqueued;
    internal readonly bool InitialLayoutDispatcherSentinelDispatched;
    internal readonly double InitialLayoutDispatcherSentinelDelayMilliseconds;
    internal readonly int InitialLayoutDispatcherHeartbeatSampleCount;
    internal readonly double InitialLayoutDispatcherHeartbeatMaxGapMilliseconds;
#endif
    internal readonly long PublicationStartedTimestamp;
    internal readonly long CommitEndedTimestamp;
    internal readonly long OverlayResetEndedTimestamp;
    internal readonly long PlanConstructionEndedTimestamp;
    internal readonly long EmbedRealizationEndedTimestamp;
    internal readonly long HighlightRetirementEndedTimestamp;
    internal readonly long HighlightSchedulingEndedTimestamp;
    internal readonly long VisibleRealizationEndedTimestamp;
    internal readonly long PublicationEndedTimestamp;

    internal MarkdownPipelineTimingSnapshot(
        long generation,
        long sourceUtf16Bytes,
        double parseMilliseconds,
        double setupMilliseconds,
        double themeSnapshotMilliseconds,
        double layoutMilliseconds,
        double layoutCpuMilliseconds,
        double layoutQueueMilliseconds,
        double layoutWorkerWallMilliseconds,
        double layoutContinuationMilliseconds,
#if MARKDOWN_LAYOUT_DIAGNOSTICS
        int layoutTopLevelBlockCount,
        int canvasTextLayoutConstructorCalls,
        double canvasTextLayoutConstructorMilliseconds,
        double layoutBlockConstructionMilliseconds,
        double layoutMeasureArrangeMilliseconds,
        double layoutSnapshotConstructionMilliseconds,
        bool initialLayoutDispatcherSentinelEnqueued,
        bool initialLayoutDispatcherSentinelDispatched,
        double initialLayoutDispatcherSentinelDelayMilliseconds,
        int initialLayoutDispatcherHeartbeatSampleCount,
        double initialLayoutDispatcherHeartbeatMaxGapMilliseconds,
#endif
        long publicationStartedTimestamp,
        long commitEndedTimestamp,
        long overlayResetEndedTimestamp,
        long planConstructionEndedTimestamp,
        long embedRealizationEndedTimestamp,
        long highlightRetirementEndedTimestamp,
        long highlightSchedulingEndedTimestamp,
        long visibleRealizationEndedTimestamp,
        long publicationEndedTimestamp)
    {
        Generation = generation;
        SourceUtf16Bytes = sourceUtf16Bytes;
        ParseMilliseconds = parseMilliseconds;
        SetupMilliseconds = setupMilliseconds;
        ThemeSnapshotMilliseconds = themeSnapshotMilliseconds;
        LayoutMilliseconds = layoutMilliseconds;
        LayoutCpuMilliseconds = layoutCpuMilliseconds;
        LayoutQueueMilliseconds = layoutQueueMilliseconds;
        LayoutWorkerWallMilliseconds = layoutWorkerWallMilliseconds;
        LayoutContinuationMilliseconds = layoutContinuationMilliseconds;
#if MARKDOWN_LAYOUT_DIAGNOSTICS
        LayoutTopLevelBlockCount = layoutTopLevelBlockCount;
        CanvasTextLayoutConstructorCalls = canvasTextLayoutConstructorCalls;
        CanvasTextLayoutConstructorMilliseconds = canvasTextLayoutConstructorMilliseconds;
        LayoutBlockConstructionMilliseconds = layoutBlockConstructionMilliseconds;
        LayoutMeasureArrangeMilliseconds = layoutMeasureArrangeMilliseconds;
        LayoutSnapshotConstructionMilliseconds = layoutSnapshotConstructionMilliseconds;
        InitialLayoutDispatcherSentinelEnqueued = initialLayoutDispatcherSentinelEnqueued;
        InitialLayoutDispatcherSentinelDispatched = initialLayoutDispatcherSentinelDispatched;
        InitialLayoutDispatcherSentinelDelayMilliseconds = initialLayoutDispatcherSentinelDelayMilliseconds;
        InitialLayoutDispatcherHeartbeatSampleCount = initialLayoutDispatcherHeartbeatSampleCount;
        InitialLayoutDispatcherHeartbeatMaxGapMilliseconds = initialLayoutDispatcherHeartbeatMaxGapMilliseconds;
#endif
        PublicationStartedTimestamp = publicationStartedTimestamp;
        CommitEndedTimestamp = commitEndedTimestamp;
        OverlayResetEndedTimestamp = overlayResetEndedTimestamp;
        PlanConstructionEndedTimestamp = planConstructionEndedTimestamp;
        EmbedRealizationEndedTimestamp = embedRealizationEndedTimestamp;
        HighlightRetirementEndedTimestamp = highlightRetirementEndedTimestamp;
        HighlightSchedulingEndedTimestamp = highlightSchedulingEndedTimestamp;
        VisibleRealizationEndedTimestamp = visibleRealizationEndedTimestamp;
        PublicationEndedTimestamp = publicationEndedTimestamp;
    }

#if MARKDOWN_LAYOUT_DIAGNOSTICS
    internal MarkdownPipelineTimingSnapshot WithInitialLayoutDispatcherDiagnostics(
        MarkdownInitialLayoutDispatcherDiagnosticsSnapshot dispatcherDiagnostics) =>
        new(
            Generation,
            SourceUtf16Bytes,
            ParseMilliseconds,
            SetupMilliseconds,
            ThemeSnapshotMilliseconds,
            LayoutMilliseconds,
            LayoutCpuMilliseconds,
            LayoutQueueMilliseconds,
            LayoutWorkerWallMilliseconds,
            LayoutContinuationMilliseconds,
            LayoutTopLevelBlockCount,
            CanvasTextLayoutConstructorCalls,
            CanvasTextLayoutConstructorMilliseconds,
            LayoutBlockConstructionMilliseconds,
            LayoutMeasureArrangeMilliseconds,
            LayoutSnapshotConstructionMilliseconds,
            dispatcherDiagnostics.SentinelEnqueued,
            dispatcherDiagnostics.SentinelDispatched,
            dispatcherDiagnostics.SentinelDelayMilliseconds,
            dispatcherDiagnostics.HeartbeatSampleCount,
            dispatcherDiagnostics.HeartbeatMaxGapMilliseconds,
            PublicationStartedTimestamp,
            CommitEndedTimestamp,
            OverlayResetEndedTimestamp,
            PlanConstructionEndedTimestamp,
            EmbedRealizationEndedTimestamp,
            HighlightRetirementEndedTimestamp,
            HighlightSchedulingEndedTimestamp,
            VisibleRealizationEndedTimestamp,
            PublicationEndedTimestamp);
#endif
}
