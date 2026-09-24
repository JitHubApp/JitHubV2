using System.Diagnostics.Tracing;

namespace JitHub.Services.Markdown;

/// <summary>Listens only during explicit audits; worker events contain no source data.</summary>
internal sealed partial class MarkdownSvgWorkerAuditListener : EventListener
{
    private const string SourceName = "MarkdownRenderer.Svg.Resvg.Worker";

    protected override void OnEventSourceCreated(EventSource eventSource)
    {
        if (eventSource.Name == SourceName)
            EnableEvents(eventSource, EventLevel.Warning);
    }

    protected override void OnEventWritten(EventWrittenEventArgs eventData)
    {
        if (eventData.EventId == 1 &&
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
    }
}
