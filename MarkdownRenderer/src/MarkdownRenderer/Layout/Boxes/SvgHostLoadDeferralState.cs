namespace MarkdownRenderer.Layout.Boxes;

/// <summary>
/// One consistent view of an <see cref="SvgHostLoadDeferral"/>, taken under
/// its lock: the phase, whether a bounded waiter handle is held, the denial,
/// registration and waiter identities a later signal must match, and the
/// versions, threshold and readiness recorded for the denied load.
/// </summary>
internal readonly record struct SvgHostLoadDeferralState(
    SvgHostDeferralPhase Phase,
    bool HasWaiter,
    object? WaiterIdentity,
    object? RegistrationAttempt,
    object? DenialIdentity,
    bool HasAdmission,
    long DemandVersion,
    long CapacityVersion,
    long OwnerVersion,
    long VisualDemandEpoch,
    long RequiredSourceBytes,
    bool RequiresOwnerSlot,
    bool CapacityReady);
