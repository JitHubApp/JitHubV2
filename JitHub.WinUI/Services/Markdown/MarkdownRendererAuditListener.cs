using System.Diagnostics.Tracing;

namespace JitHub.Services.Markdown;

/// <summary>Listens only during explicit audits; events contain no source data.</summary>
internal sealed partial class MarkdownRendererAuditListener : EventListener
{
    private const string WorkerSourceName = "MarkdownRenderer.Svg.Resvg.Worker";
    private const string PreflightSourceName = "MarkdownRenderer.Svg.Resvg.Preflight";
    private const string RasterPreparationSourceName = "MarkdownRenderer-RasterPreparation";

    protected override void OnEventSourceCreated(EventSource eventSource)
    {
        if (eventSource.Name is WorkerSourceName or PreflightSourceName)
            EnableEvents(eventSource, EventLevel.Warning);
        else if (eventSource.Name == RasterPreparationSourceName &&
            MarkdownLifecycleAutomationBridge.IsRasterPreparationEvidenceEnabled)
            EnableEvents(eventSource, EventLevel.Informational);
    }

    protected override void OnEventWritten(EventWrittenEventArgs eventData)
    {
        if (eventData.EventSource.Name == WorkerSourceName &&
            eventData.EventId == 1 &&
            eventData.Payload is { Count: 13 } payload &&
            payload[0] is int stage &&
            payload[1] is int deadlineMilliseconds &&
            payload[2] is int workerProcessCpuMilliseconds &&
            payload[3] is int transportPhase &&
            payload[4] is int requestWriteMilliseconds &&
            payload[5] is int workerExited &&
            payload[6] is int openProgressPhase &&
            payload[7] is int workerWorkingSetKiB &&
            payload[8] is int workerPrivateCommitKiB &&
            payload[9] is int workerPageFaults &&
            payload[10] is int elapsedWallMilliseconds &&
            payload[11] is string workerInputSha256 &&
            payload[12] is string workerExecutableSha256)
        {
            MarkdownLifecycleAutomationBridge.RecordSvgWorkerTimeout(
                stage,
                deadlineMilliseconds,
                workerProcessCpuMilliseconds,
                transportPhase,
                requestWriteMilliseconds,
                workerExited,
                openProgressPhase,
                workerWorkingSetKiB,
                workerPrivateCommitKiB,
                workerPageFaults,
                elapsedWallMilliseconds,
                workerInputSha256,
                workerExecutableSha256);
        }
        else if (eventData.EventSource.Name == WorkerSourceName &&
            eventData.EventId == 2 &&
            eventData.Payload is { Count: 1 } fontPhasePayload &&
            fontPhasePayload[0] is string initializationPhase)
        {
            MarkdownLifecycleAutomationBridge.RecordSvgFontCatalogDeadlinePhase(initializationPhase);
        }
        else if (eventData.EventSource.Name == PreflightSourceName &&
            eventData.EventId == 1 &&
            eventData.Payload is { Count: 3 } preflightPayload &&
            preflightPayload[0] is string reason &&
            preflightPayload[1] is int sourceByteLength &&
            preflightPayload[2] is string sourceSha256)
        {
            MarkdownLifecycleAutomationBridge.RecordSvgPreflightRejection(
                reason,
                sourceByteLength,
                sourceSha256);
        }
        else if (eventData.EventSource.Name == RasterPreparationSourceName &&
            eventData.EventId is 1 or 2 &&
            eventData.Payload is { Count: 3 } rasterPayload &&
            rasterPayload[0] is long preparationId &&
            rasterPayload[1] is long value &&
            rasterPayload[2] is long durationOrDimensions)
        {
            MarkdownLifecycleAutomationBridge.RecordRasterPreparation(
                preparationId,
                eventData.EventId == 1 ? 0 : (int)value,
                eventData.EventId == 1 ? value : 0,
                eventData.EventId == 1 ? durationOrDimensions : 0,
                eventData.EventId == 2 ? durationOrDimensions : 0);
        }
    }
}
