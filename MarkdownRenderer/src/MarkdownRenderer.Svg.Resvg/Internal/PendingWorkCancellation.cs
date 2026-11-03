using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using MarkdownRenderer.Images;

namespace MarkdownRenderer.Svg.Resvg.Internal;

/// <summary>The state handed to a queued work item's cancellation callback.</summary>
internal sealed record PendingWorkCancellation(WorkerPool Pool, PendingWork Pending);
