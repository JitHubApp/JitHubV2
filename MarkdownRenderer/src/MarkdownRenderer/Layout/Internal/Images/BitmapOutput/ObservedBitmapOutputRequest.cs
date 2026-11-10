using MarkdownRenderer.Images;

namespace MarkdownRenderer.Layout.Internal.Images.BitmapOutput;

/// <summary>
/// What one <see cref="ImageBitmapOutputDeferral"/> observation read from its
/// stored deferred request under the lock: the waiter epoch and demand, whether
/// the request is still current, and, for a current one, the waiter and the
/// demand to update it with. A stale request carries only its epoch and demand.
/// </summary>
internal readonly record struct ObservedBitmapOutputRequest(
    long Epoch,
    MarkdownSvgRenderDemand? Demand,
    bool StaleStoredRequest)
{
    public BitmapOutputAdmission.WaiterRegistration? Waiter { get; init; }
    public long Bytes { get; init; }
    public long Version { get; init; }
    public bool Replacement { get; init; }
    public bool VisibleNow { get; init; }
    public bool Valid { get; init; }
    public bool RetryRegistration { get; init; }
}
