using System;
using System.Threading;
using System.Threading.Tasks;
using MarkdownRenderer.Diagnostics.Internal;
using MarkdownRenderer.Images.Svg;

namespace MarkdownRenderer.Layout.Internal.Images.Svg.HostAdmission;

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

    /// <summary>Retires the demand-promotion registration once, logging a failure.</summary>
    internal void DisposePromotionRegistration()
    {
        IDisposable? registration = Interlocked.Exchange(ref PromotionRegistration, null);
        try { registration?.Dispose(); }
        catch (Exception exception)
        {
            MarkdownDiagnostics.WriteLine(
                $"[SvgAdmission] promotion retirement failed: {exception.GetType().Name}");
        }
    }

    /// <summary>
    /// Marks the row removed, retires its promotion registration, and
    /// requests cancellation of its waiter once. Every caller shares the
    /// same cancellation-callback task.
    /// </summary>
    internal Task RequestCancellation()
    {
        Interlocked.Exchange(ref Removed, 1);
        DisposePromotionRegistration();
        lock (CancellationSync)
        {
            if (CancellationCallbacks is { } existing)
                return existing;
            try
            {
                CancellationCallbacks = Cancellation.CancelAsync();
                return CancellationCallbacks;
            }
            catch (Exception exception)
            {
                MarkdownDiagnostics.WriteLine(
                    $"[SvgAdmission] waiter cancellation failed: {exception.GetType().Name}");
                return Task.FromException(exception);
            }
        }
    }

    /// <summary>
    /// Marks waiter completion as started and tells a still-live owner that
    /// this waiter finished. A callback failure is logged with
    /// <paramref name="failureLabel"/>.
    /// </summary>
    internal void NotifyOwnerWaiterFinished(string failureLabel)
    {
        Volatile.Write(ref WaiterCompletionStarted, 1);
        try
        {
            if (Owner.TryGetTarget(out SvgHostLoadDeferral? owner))
                owner.OnSvgHostAdmissionWaiterFinished(Demand, LoadGeneration, Identity);
        }
        catch (Exception exception)
        {
            MarkdownDiagnostics.WriteLine(
                $"[SvgAdmission] {failureLabel} failed: {exception.GetType().Name}");
        }
    }
}
