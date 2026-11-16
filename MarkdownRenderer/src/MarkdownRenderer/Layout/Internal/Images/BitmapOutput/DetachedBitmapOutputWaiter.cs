using System.Threading;

namespace MarkdownRenderer.Layout.Internal.Images.BitmapOutput;

/// <summary>
/// The waiter registration and cancellation registrations an
/// <see cref="ImageBitmapOutputDeferral"/> detached under its lock when a new
/// demand replaced its waiter epoch. They are released after the lock is left.
/// </summary>
internal readonly record struct DetachedBitmapOutputWaiter(
    BitmapOutputAdmission.WaiterRegistration? Waiter,
    bool ReturnUnusedOffer,
    CancellationTokenRegistration ImageCancellation,
    CancellationTokenRegistration SessionCancellation)
{
    /// <summary>
    /// Returns a pending unused capacity offer or disposes the waiter, then
    /// unregisters both cancellations. The default value releases nothing.
    /// </summary>
    internal void Release()
    {
        if (Waiter is not null)
        {
            if (ReturnUnusedOffer)
                Waiter.ReturnUnusedNotification();
            else
                Waiter.Dispose();
        }
        ImageCancellation.Unregister();
        SessionCancellation.Unregister();
    }
}
