using System.Threading;

namespace MarkdownRenderer.Layout.Internal.LazyLayout;

/// <summary>
/// Asks an in-flight lazy band realization to stop at its next top-level root
/// boundary and commit the roots it already measured.
/// </summary>
/// <remarks>
/// Superseding look-ahead work is not a failure. Cancelling a pass after its
/// first root was measured cannot be rolled back with certainty, so the
/// snapshot would demand a full rebuild; a yield ends the pass cleanly
/// instead. Hard cancellation stays reserved for teardown and replacement.
/// </remarks>
internal sealed class LazyBandYield
{
    private int _requested;

    /// <summary>True once any thread asked the realization to stop early.</summary>
    internal bool IsRequested => Volatile.Read(ref _requested) != 0;

    /// <summary>Asks the realization to stop at its next root boundary. Idempotent.</summary>
    internal void Request() => Volatile.Write(ref _requested, 1);
}
