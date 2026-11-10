using System.Diagnostics;
using MarkdownRenderer.Layout.Internal.Snapshots;

namespace MarkdownRenderer.Controls.Pipeline;

/// <summary>
/// The snapshot a rebuild's layout stage built, or null when cancelled, with
/// the stage's timestamps: requested, worker start and end, and resumed on
/// the UI thread.
/// </summary>
internal sealed record RebuildLayoutRunResult(
    LayoutSnapshot? Snapshot,
    double CpuMilliseconds,
    long Started,
    long WorkerStarted,
    long WorkerEnded,
    long Ended)
{
    internal double Milliseconds => Stopwatch.GetElapsedTime(Started, Ended).TotalMilliseconds;

    internal double QueueMilliseconds => Stopwatch.GetElapsedTime(Started, WorkerStarted).TotalMilliseconds;

    internal double WorkerWallMilliseconds => Stopwatch.GetElapsedTime(WorkerStarted, WorkerEnded).TotalMilliseconds;

    internal double ContinuationMilliseconds => Stopwatch.GetElapsedTime(WorkerEnded, Ended).TotalMilliseconds;
}
