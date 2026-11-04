using MarkdownRenderer.Images;

namespace MarkdownRenderer.Layout.Boxes;

/// <summary>
/// The host admission denial an SVG load is waiting out: the admission and
/// demand, and the capacity, owner and demand versions observed at denial.
/// </summary>
internal readonly struct SvgHostCapacityDeferral(
    IMarkdownSvgAdmission Admission,
    MarkdownSvgRenderDemand Demand,
    int LoadGeneration,
    long CapacityVersion,
    long RequiredSourceReservationBytes,
    bool RequiresOwnerSlot,
    long OwnerVersionAtDenial,
    long DemandVersionAtDenial,
    long VisualDemandEpochAtDenial)
{
    internal IMarkdownSvgAdmission Admission { get; } = Admission;
    internal MarkdownSvgRenderDemand Demand { get; } = Demand;
    internal int LoadGeneration { get; } = LoadGeneration;
    internal long CapacityVersion { get; } = CapacityVersion;
    internal long RequiredSourceReservationBytes { get; } = RequiredSourceReservationBytes;
    internal bool RequiresOwnerSlot { get; } = RequiresOwnerSlot;
    internal long OwnerVersionAtDenial { get; } = OwnerVersionAtDenial;
    internal long DemandVersionAtDenial { get; } = DemandVersionAtDenial;
    internal long VisualDemandEpochAtDenial { get; } = VisualDemandEpochAtDenial;
}
