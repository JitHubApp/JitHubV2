using MarkdownRenderer.Images;
using MarkdownRenderer.Layout.Internal.Images;
using MarkdownRenderer.Layout.Internal.Images.Svg.HostAdmission;
using MarkdownRenderer.Layout.Internal.Images.Svg.Tiles;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>
/// A fixed set of committed SVG host owners for parking-pressure tests: each
/// index holds one <see cref="SvgHostPressureDocument"/>, its overscan demand,
/// its reserved owner slot, its tiled plan and the image box that owns it.
/// </summary>
internal sealed class SvgHostPressureOwnerSet(int count)
{
    internal ImageBox[] Owners { get; } = new ImageBox[count];
    internal SvgTileRenderPlan[] Plans { get; } = new SvgTileRenderPlan[count];
    internal SvgHostPressureDocument[] Documents { get; } = new SvgHostPressureDocument[count];
    internal SvgHostOwnerSlot[] Slots { get; } = new SvgHostOwnerSlot[count];
    internal MarkdownSvgRenderDemand[] Demands { get; } = new MarkdownSvgRenderDemand[count];

    // Reserves a slot per owner in the admission domain and commits each owner
    // with the given source weight.
    internal void Commit(
        SvgHostAdmissionDomain domain,
        object admissionDomain,
        long ownerWeight,
        Func<SvgTileRenderPlan, ImageBox> createOwner)
    {
        for (int i = 0; i < Owners.Length; i++)
        {
            Documents[i] = new SvgHostPressureDocument();
            Demands[i] = new MarkdownSvgRenderDemand(MarkdownSvgRenderPriority.Overscan);
            Slots[i] = Assert.IsType<SvgHostOwnerSlot>(
                domain.Owners.TryReserveOwnerSlot(admissionDomain, out _));
            Plans[i] = new SvgTileRenderPlan(
                Documents[i], $"pressure-owner-{i}", 2048, 2048, i + 1,
                Demands[i], sourceReservationBytes: ownerWeight, ownerSlot: Slots[i]);
            Owners[i] = createOwner(Plans[i]);
            Slots[i].Commit(Owners[i], Plans[i], Demands[i], ownerWeight);
        }
    }

    // Cleanup: parks each still-live document through its owner (disposing it
    // directly if parking does not), then releases every reserved slot.
    internal void ParkOrDisposeUnfinishedDocuments()
    {
        for (int i = 0; i < Documents.Length; i++)
        {
            if (Documents[i] is not { } document || document.Disposed.Task.IsCompleted)
                continue;
            if (Owners[i] is { } owner && Plans[i] is { } plan && Demands[i] is { } demand)
                _ = owner.TryParkSvgDocument(plan, demand.Version, maximumTier: 0);
            if (!document.Disposed.Task.IsCompleted)
                document.Dispose();
        }
        foreach (SvgHostOwnerSlot? slot in Slots)
            slot?.Dispose();
        GC.KeepAlive(Owners);
        GC.KeepAlive(Plans);
    }
}
