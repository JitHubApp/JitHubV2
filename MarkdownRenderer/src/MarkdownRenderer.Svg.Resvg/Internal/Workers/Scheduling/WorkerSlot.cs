using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using MarkdownRenderer.Images;
using MarkdownRenderer.Svg.Resvg.Internal.Diagnostics;
using MarkdownRenderer.Svg.Resvg.Internal.Workers.Processes;

namespace MarkdownRenderer.Svg.Resvg.Internal.Workers.Scheduling;

/// <summary>
/// The primary or secondary worker position in the pool. Every mutable member
/// is read and written under the pool's state lock.
/// </summary>
internal sealed class WorkerSlot(bool isSecondary)
{
    public bool IsSecondary { get; } = isSecondary;
    public SemaphoreSlim FontCatalogGate { get; } = new(1, 1);
    public long PolicyDecisionSequence { get; set; }
    public SvgWorkerPolicySnapshot PolicySnapshot { get; set; }
    public SecondaryWorkerReadiness? Readiness { get; } =
        isSecondary ? new SecondaryWorkerReadiness() : null;
    public WindowsWorkerProcess? Worker { get; set; }
    public PendingWork? Active { get; set; }
    public bool Busy { get; set; }
    public bool KillCanceledWhenStarted { get; set; }
    public DateTimeOffset IdleSince { get; set; }
    public CancellationTokenSource? IdleRetirement { get; set; }
}
