using System;
using MarkdownRenderer.Images;

namespace MarkdownRenderer.Layout.Boxes;

/// <summary>
/// A committed SVG owner: weak references to the box and its tile plan, the
/// demand that admitted it and its source reservation, plus an LRU stamp.
/// </summary>
internal sealed class SvgHostOwnerEntry(
    ImageBox owner,
    ImageBox.SvgTileRenderPlan plan,
    MarkdownSvgRenderDemand demand,
    long sourceReservationBytes,
    SvgHostOwnerSlot slot,
    long lastUse)
{
    internal readonly WeakReference<ImageBox> Owner = new(owner);
    internal readonly WeakReference<ImageBox.SvgTileRenderPlan> Plan = new(plan);
    internal readonly MarkdownSvgRenderDemand Demand = demand;
    internal readonly long SourceReservationBytes = sourceReservationBytes;
    internal readonly WeakReference<SvgHostOwnerSlot> Slot = new(slot);
    internal long LastUse = lastUse;
}
