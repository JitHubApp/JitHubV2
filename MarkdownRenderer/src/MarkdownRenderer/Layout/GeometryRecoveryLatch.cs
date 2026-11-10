using System;
using System.Threading;
using MarkdownRenderer.Diagnostics.Internal;

namespace MarkdownRenderer.Layout;

/// <summary>
/// Lock-free latch raised when a failed live layout leaves nested mutable or
/// native geometry that cannot be certified by restoring only top-level bounds.
/// Unlike the transient paint-only taint, a raised latch makes the published
/// snapshot permanently unreadable and routes its owner to replacement. It
/// holds only scalar cause fields, never an exception graph.
/// </summary>
internal sealed class GeometryRecoveryLatch
{
    private int _geometryRecoveryRequired;
    private int _geometryRecoveryRequestClaimed;
    private int _geometryRecoveryIsDeviceLost;
    private int _geometryRecoveryFailureHResult;

    // Read by UI scheduling paths without taking the layout gate. The control
    // uses the cause scalars to preserve device-loss backoff without retaining
    // an exception graph or touching native state on the recovery path.
    internal bool IsRequired => Volatile.Read(ref _geometryRecoveryRequired) != 0;

    internal bool IsDeviceLost => Volatile.Read(ref _geometryRecoveryIsDeviceLost) != 0;

    internal int FailureHResult => Volatile.Read(ref _geometryRecoveryFailureHResult);

    internal bool TryClaimGeometryRecoveryRequest()
    {
        if (!IsRequired ||
            Interlocked.CompareExchange(ref _geometryRecoveryRequestClaimed, 1, 0) != 0)
        {
            return false;
        }

        // Recheck after the CAS to keep the request claim coupled to the latch.
        if (IsRequired)
            return true;

        Interlocked.CompareExchange(ref _geometryRecoveryRequestClaimed, 0, 1);
        return false;
    }

    internal void MarkGeometryRecoveryRequiredNoLock(Exception failure, Exception? secondaryFailure = null)
    {
        if (_geometryRecoveryRequired != 0)
        {
            if (secondaryFailure is not null && GraphicsDeviceErrors.IsDeviceLost(secondaryFailure))
                Volatile.Write(ref _geometryRecoveryIsDeviceLost, 1);
            return;
        }

        Volatile.Write(ref _geometryRecoveryFailureHResult, failure.HResult);
        bool deviceLost = GraphicsDeviceErrors.IsDeviceLost(failure) ||
            GraphicsDeviceErrors.IsDeviceLost(secondaryFailure);
        Volatile.Write(ref _geometryRecoveryIsDeviceLost, deviceLost ? 1 : 0);
        Volatile.Write(ref _geometryRecoveryRequired, 1);
    }
}
