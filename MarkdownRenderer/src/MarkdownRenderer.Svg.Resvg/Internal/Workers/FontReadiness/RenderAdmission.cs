using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using MarkdownRenderer.Images;
using MarkdownRenderer.Svg.Resvg.Internal.Workers.Processes;
using MarkdownRenderer.Svg.Resvg.Internal.Workers.Scheduling;

namespace MarkdownRenderer.Svg.Resvg.Internal.Workers.FontReadiness;

/// <summary>
/// A render admitted to a worker slot for one font generation. Disposing it
/// releases the slot.
/// </summary>
internal sealed class RenderAdmission(
    WorkerLease lease,
    WindowsWorkerProcess worker,
    long requestGeneration) : IDisposable
{
    public WorkerLease Lease { get; } = lease;
    public WindowsWorkerProcess Worker { get; } = worker;
    public long RequestGeneration { get; } = requestGeneration;

    public void Dispose() => Lease.Dispose();
}
