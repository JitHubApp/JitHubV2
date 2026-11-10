namespace MarkdownRenderer.Layout.Internal.Images.BitmapOutput;

/// <summary>
/// One consistent view of an image box's bitmap-output deferral, taken under
/// the deferral's lock.
/// </summary>
internal readonly record struct ImageBitmapOutputState(
    bool HasWaiter,
    bool HasWaiterOwner,
    bool HasImageCancellation,
    bool HasSessionCancellation,
    bool HasDemand,
    long WaiterEpoch,
    long RecoverySignaledEpoch,
    long RequiredBytes,
    long CapacityVersion,
    long WaiterSlotVersion,
    int DemandGeneration,
    int SvgGeneration,
    bool Deferred,
    bool Visible,
    bool Replacement,
    bool ExistingTile,
    bool NeedsTrim,
    bool Registering,
    bool RegistrationNeeded,
    bool CapacityReady,
    bool RetryQueued,
    bool RetryInProgress,
    bool RetryOfferPending,
    bool RegistrationFailed,
    bool CancellationRegistered)
{
    /// <summary>
    /// True when no waiter, registration, deferral or retry is live.
    /// Successful admission leaves monotonic waiter epochs and cached
    /// visibility/tier hints behind. They are historical bookkeeping, not
    /// pending ownership. A private placement transfer only needs to exclude
    /// live waiters, registrations, deferrals, and retries.
    /// </summary>
    public bool HasNoLiveWork =>
        !HasWaiter &&
        !HasWaiterOwner &&
        !HasImageCancellation &&
        !HasSessionCancellation &&
        !HasDemand &&
        RequiredBytes == 0 &&
        CapacityVersion == 0 &&
        WaiterSlotVersion == 0 &&
        DemandGeneration == 0 &&
        SvgGeneration == 0 &&
        !Deferred &&
        !NeedsTrim &&
        !Registering &&
        !RegistrationNeeded &&
        !CapacityReady &&
        !RetryQueued &&
        !RetryInProgress &&
        !RetryOfferPending &&
        !RegistrationFailed &&
        !CancellationRegistered;

    /// <summary>True when the deferral has never recorded a request, not even historically.</summary>
    public bool IsPristine =>
        HasNoLiveWork &&
        WaiterEpoch == 0 &&
        RecoverySignaledEpoch == 0 &&
        !Visible &&
        !Replacement &&
        !ExistingTile;
}
