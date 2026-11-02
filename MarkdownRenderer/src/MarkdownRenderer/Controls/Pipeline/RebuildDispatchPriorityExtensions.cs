using System;
using Microsoft.UI.Dispatching;

namespace MarkdownRenderer.Controls.Pipeline;

/// <summary>
/// Maps the WinUI-independent rebuild dispatch priority to the UI queue's
/// priority, keeping <see cref="MarkdownRebuildDispatchPriority"/> testable
/// without WinUI.
/// </summary>
internal static class RebuildDispatchPriorityExtensions
{
    internal static DispatcherQueuePriority ToDispatcherQueuePriority(
        this MarkdownRebuildDispatchPriority priority) =>
        priority switch
        {
            MarkdownRebuildDispatchPriority.Low =>
                DispatcherQueuePriority.Low,
            MarkdownRebuildDispatchPriority.Normal =>
                DispatcherQueuePriority.Normal,
            _ => throw new ArgumentOutOfRangeException(nameof(priority)),
        };
}
