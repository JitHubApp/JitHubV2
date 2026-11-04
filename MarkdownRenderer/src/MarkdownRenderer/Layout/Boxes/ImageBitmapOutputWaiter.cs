using System;

namespace MarkdownRenderer.Layout.Boxes;

/// <summary>
/// The source-free token an image box's bitmap-output deferral registers with
/// the bitmap output governor. It holds its deferral weakly, so a queued
/// waiter never keeps an image box or its source alive.
/// </summary>
internal sealed class ImageBitmapOutputWaiter : IBitmapOutputWaiter
{
    private readonly WeakReference<ImageBitmapOutputDeferral> _owner;
    private readonly long _epoch;

    internal ImageBitmapOutputWaiter(ImageBitmapOutputDeferral owner, long epoch)
    {
        _owner = new WeakReference<ImageBitmapOutputDeferral>(owner);
        _epoch = epoch;
    }

    public void OnBitmapOutputCapacityAvailable(long capacityVersion)
    {
        if (_owner.TryGetTarget(out ImageBitmapOutputDeferral? owner))
            owner.OnBitmapOutputCapacityAvailable(_epoch, capacityVersion);
    }

    internal void Cancel()
    {
        if (_owner.TryGetTarget(out ImageBitmapOutputDeferral? owner))
            owner.CancelBitmapOutputDeferral(_epoch, resetLoad: false);
    }

    internal void PrepareRetry(long epoch)
    {
        if (epoch == _epoch && _owner.TryGetTarget(out ImageBitmapOutputDeferral? owner))
            owner.PrepareDeferredBitmapOutputWaiterSafely(epoch);
    }

    /// <summary>
    /// Refreshes <paramref name="waiter"/> with the governor, or joins its
    /// queue when there is none. A refresh that finds the registration removed
    /// rejoins at once and reports the removed one as
    /// <paramref name="staleWaiter"/>; on failure the governor's atomic slot
    /// generation is returned so a later removal cannot be lost.
    /// </summary>
    internal bool TryJoin(
        BitmapOutputAdmission admission,
        ref BitmapOutputAdmission.WaiterRegistration? waiter,
        long bytes,
        bool visible,
        bool replacement,
        long observedVersion,
        ref BitmapOutputAdmission.WaiterRegistration? staleWaiter,
        ref long capacityVersionAtAttempt,
        ref long waiterSlotVersionAtAttempt)
    {
        bool registrationReady;
        if (waiter is not null)
        {
            registrationReady = waiter.UpdateDemand(
                bytes,
                visible,
                replacement,
                observedVersion,
                out capacityVersionAtAttempt,
                out waiterSlotVersionAtAttempt);
            if (!registrationReady)
            {
                // The captured registration may have been removed while
                // this source-free retry was queued. Rejoin immediately;
                // the fresh attempt returns an atomic slot generation on
                // failure so a later removal cannot be lost.
                staleWaiter = waiter;
                registrationReady = admission.TryRegisterWaiter(
                    this,
                    bytes,
                    visible,
                    replacement,
                    observedVersion,
                    out BitmapOutputAdmission.WaiterRegistration? replacementWaiter,
                    out capacityVersionAtAttempt,
                    out waiterSlotVersionAtAttempt);
                waiter = registrationReady ? replacementWaiter : null;
            }
        }
        else
        {
            registrationReady = admission.TryRegisterWaiter(
                this,
                bytes,
                visible,
                replacement,
                observedVersion,
                out waiter,
                out capacityVersionAtAttempt,
                out waiterSlotVersionAtAttempt);
        }
        return registrationReady;
    }
}
