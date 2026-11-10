using System.Threading.Tasks;

namespace MarkdownRenderer.Layout.Internal.Snapshots;

/// <summary>
/// Implemented by <see cref="LayoutSnapshot"/> so that
/// <see cref="SnapshotRetirement"/> can release the snapshot's boxes and
/// residency policy exactly once, under the layout gate.
/// </summary>
internal interface ILayoutSnapshotRetirementHost
{
    /// <summary>
    /// Disposes every box and the vector residency policy. Retirement tasks that
    /// must drain before completion are returned even when disposal then throws.
    /// </summary>
    /// <param name="pendingRetirementTasks">Native work that must finish before retirement completes.</param>
    void DisposeResourcesNoLock(out Task[] pendingRetirementTasks);
}
