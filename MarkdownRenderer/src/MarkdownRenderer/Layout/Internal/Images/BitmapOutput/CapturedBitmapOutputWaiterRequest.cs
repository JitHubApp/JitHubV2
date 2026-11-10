using MarkdownRenderer.Images.Svg;

namespace MarkdownRenderer.Layout.Internal.Images.BitmapOutput;

/// <summary>
/// What one <see cref="ImageBitmapOutputDeferral"/> waiter preparation read
/// from its stored deferred request under the lock, for the governor join it
/// then makes outside the lock: the waiter owner and any existing
/// registration, the demand threshold and versions it observed, the load
/// identity to revalidate, and whether this join creates the registration.
/// </summary>
internal readonly record struct CapturedBitmapOutputWaiterRequest(
    ImageBitmapOutputWaiter? Owner,
    BitmapOutputAdmission.WaiterRegistration? Waiter,
    long Bytes,
    long ObservedVersion,
    long ObservedSlotVersion,
    bool Visible,
    bool Replacement,
    MarkdownSvgRenderDemand? Demand,
    int Generation,
    int SvgGeneration,
    bool NeedsTrim,
    bool CreatingRegistration);
