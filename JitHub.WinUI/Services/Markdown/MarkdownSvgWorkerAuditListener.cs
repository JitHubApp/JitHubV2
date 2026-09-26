using System.Diagnostics.Tracing;

namespace JitHub.Services.Markdown;

/// <summary>Listens only during explicit audits; SVG events contain no source data.</summary>
internal sealed partial class MarkdownSvgWorkerAuditListener : EventListener
{
    private const string WorkerSourceName = "MarkdownRenderer.Svg.Resvg.Worker";
    private const string PreflightSourceName = "MarkdownRenderer.Svg.Resvg.Preflight";

    protected override void OnEventSourceCreated(EventSource eventSource)
    {
        if (eventSource.Name is WorkerSourceName or PreflightSourceName)
            EnableEvents(eventSource, EventLevel.Warning);
    }

    protected override void OnEventWritten(EventWrittenEventArgs eventData)
    {
        if (eventData.EventSource.Name == WorkerSourceName &&
            eventData.EventId == 1 &&
            eventData.Payload is { Count: 3 } payload &&
            payload[0] is int stage &&
            payload[1] is int deadlineMilliseconds &&
            payload[2] is int workerProcessCpuMilliseconds)
        {
            MarkdownLifecycleAutomationBridge.RecordSvgWorkerTimeout(
                stage,
                deadlineMilliseconds,
                workerProcessCpuMilliseconds);
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
    }
}
