using System.Threading;
using MarkdownRenderer.Images;

namespace MarkdownRenderer.Layout.Boxes;

/// <summary>
/// The opened SVG document and the full-size geometry, demand and host
/// admission an image box renders its visible tiles from.
/// </summary>
internal sealed class SvgTileRenderPlan(
    IMarkdownSvgDocument document,
    string identityPrefix,
    int fullWidthPixels,
    int fullHeightPixels,
    int renderGeneration,
    MarkdownSvgRenderDemand? demand = null,
    IMarkdownSvgAdmission? hostAdmission = null,
    long sourceReservationBytes = 0,
    SvgHostOwnerSlot? ownerSlot = null,
    MarkdownSvgPhysicalViewport? physicalViewport = null)
{
    // Serializes the short viewport-state mutation with the final owner
    // detach in pressure parking. It is per-plan so a late release from an
    // obsolete operation cannot reopen a replacement plan's gate.
    private int _viewportMutationState;

    internal readonly IMarkdownSvgDocument Document = document;
    internal readonly string IdentityPrefix = identityPrefix;
    internal readonly int FullWidthPixels = fullWidthPixels;
    internal readonly int FullHeightPixels = fullHeightPixels;
    internal readonly MarkdownSvgPhysicalViewport PhysicalViewport = physicalViewport ??
        new MarkdownSvgPhysicalViewport(fullWidthPixels, fullHeightPixels);
    internal readonly int RenderGeneration = renderGeneration;
    internal readonly MarkdownSvgRenderDemand Demand = demand ??
        new MarkdownSvgRenderDemand(MarkdownSvgRenderPriority.Visible);
    internal readonly IMarkdownSvgAdmission? HostAdmission = hostAdmission;
    internal readonly object? AdmissionDomain = ownerSlot?.AdmissionDomain;
    internal readonly long SourceReservationBytes = sourceReservationBytes;
    internal readonly SvgHostOwnerSlot? OwnerSlot = ownerSlot;

    // 0=open, 1=viewport mutation in progress, 2=parking commit claimed.
    internal bool TryBeginViewportMutation() =>
        Interlocked.CompareExchange(ref _viewportMutationState, 1, 0) == 0;

    internal void EndViewportMutation() =>
        Interlocked.CompareExchange(ref _viewportMutationState, 0, 1);

    internal bool TryClaimViewportParking() =>
        Interlocked.CompareExchange(ref _viewportMutationState, 2, 0) == 0;

    internal void EndViewportParking() =>
        Interlocked.CompareExchange(ref _viewportMutationState, 0, 2);

    internal int ViewportMutationState => Volatile.Read(ref _viewportMutationState);
}
