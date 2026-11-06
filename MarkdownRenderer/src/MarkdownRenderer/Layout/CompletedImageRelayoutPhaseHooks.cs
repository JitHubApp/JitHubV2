using System;

namespace MarkdownRenderer.Layout;

/// <summary>
/// Optional callbacks run at the two outside-gate phase boundaries of a
/// completed-image root relayout. <see cref="BeforeMeasureOutsideGate"/> runs
/// after the source providers are validated and before the private clones are
/// measured; <see cref="BeforeCommitOutsideGate"/> runs after native
/// preparation and the successor placement generation are ready, immediately
/// before the final cancellation check and the publication gate. Neither runs
/// under the snapshot layout gate, so a callback may cancel, record pointer
/// input or commit unrelated relayout to exercise the retry paths.
/// </summary>
internal sealed class CompletedImageRelayoutPhaseHooks
{
    internal Action? BeforeMeasureOutsideGate { get; init; }

    internal Action? BeforeCommitOutsideGate { get; init; }

    internal static CompletedImageRelayoutPhaseHooks BeforeMeasure(Action callback) =>
        new() { BeforeMeasureOutsideGate = callback };

    internal static CompletedImageRelayoutPhaseHooks BeforeCommit(Action callback) =>
        new() { BeforeCommitOutsideGate = callback };
}
