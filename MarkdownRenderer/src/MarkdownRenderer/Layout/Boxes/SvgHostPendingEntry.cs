using System;
using System.Threading;
using System.Threading.Tasks;
using MarkdownRenderer.Images;

namespace MarkdownRenderer.Layout.Boxes;

/// <summary>
/// One deferred SVG admission demand and the state of its single waiter.
/// It holds weak references only, so a pending row never keeps a box alive.
/// </summary>
internal sealed class SvgHostPendingEntry(
    SvgHostLoadDeferral owner,
    IMarkdownSvgAdmission admission,
    MarkdownSvgRenderDemand demand,
    int loadGeneration,
    long capacityVersion,
    long requiredSourceReservationBytes,
    bool requiresOwnerSlot,
    long ownerVersion,
    long demandVersion,
    long visualDemandEpoch,
    CancellationToken cancellationToken)
{
    internal readonly WeakReference<SvgHostLoadDeferral> Owner = new(owner);
    internal readonly WeakReference<IMarkdownSvgAdmission> Admission = new(admission);
    internal readonly MarkdownSvgRenderDemand Demand = demand;
    internal readonly int LoadGeneration = loadGeneration;
    internal readonly object Identity = new();
    internal long CapacityVersion = capacityVersion;
    internal long RequiredSourceReservationBytes = requiredSourceReservationBytes;
    internal bool RequiresOwnerSlot = requiresOwnerSlot;
    internal long OwnerVersion = ownerVersion;
    internal long DemandVersionAtDenial = demandVersion;
    internal long VisualDemandEpochAtDenial = visualDemandEpoch;
    internal readonly TaskCompletionSource Promotion = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    internal IDisposable? PromotionRegistration;
    internal readonly object CancellationSync = new();
    // Create the linked source last among the allocating initializers. If
    // an earlier allocation fails during construction, no parent-token
    // registration has been created that could keep this partial entry
    // alive without a reachable owner to dispose it.
    internal readonly CancellationTokenSource Cancellation =
        CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    internal Task? CancellationCallbacks;
    internal int Claimed;
    internal int Removed;
    internal int WaiterCompletionStarted;
    internal int WaiterFinished;
    internal int WaiterHandles = 1;
    // Initial row fields are provisional until one exact ImageBox
    // registration accepts the row. Concurrent rejected attempts must not
    // raise a live waiter's threshold or select its wait kind.
    internal bool HasAcceptedRegistration;
    // 0=no startup owner, 1=startup owner claimed, 2=waiter task scheduled.
    internal int WaiterStartState;
    internal int UnstartedCleanupStarted;
    // Every Defer call publishes its ImageBox identity outside this
    // registry lock. A ready waiter cannot claim the row until all such
    // callbacks have completed, so a capacity pulse cannot race the
    // accepted identity into the host retry gate.
    internal int RegistrationsInFlight;
    internal TaskCompletionSource? RegistrationsDrained;

    // Pressure parking is cumulative per pending demand. Duplicate
    // threshold increases may schedule additional victims, but can never
    // reset the eight-victim bound.
    internal long ParkedSourceBytes;
    internal int ParkedVictims;
    internal bool ParkingInProgress;
}
