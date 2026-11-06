using MarkdownRenderer.Images;

namespace MarkdownRenderer.Svg.Resvg.Internal;

/// <summary>The state handed to a queued work item's cancellation callback.</summary>
internal sealed record PendingWorkCancellation(WorkerScheduler Pool, PendingWork Pending);
