using System;
using System.Runtime.CompilerServices;
using System.Threading;
using MarkdownRenderer.Images;

namespace MarkdownRenderer.Layout.Boxes;

/// <summary>
/// Weak, bounded host-side coordination for provider-owned tiled SVG source
/// snapshots, keyed weakly by each provider's admission domain. It stores no
/// source arrays, documents, or strong ImageBox refs. One coordinator belongs
/// to an <see cref="ImageResourceScope"/>.
/// </summary>
internal sealed class SvgHostAdmissionCoordinator
{
    private readonly ConditionalWeakTable<object, SvgHostAdmissionDomain> _domains = new();

    internal SvgHostOwnerSlot? TryReserveOwnerSlot(
        object admissionDomain,
        MarkdownSvgRenderDemand demand,
        out long ownerVersionAtDenial)
    {
        ArgumentNullException.ThrowIfNull(admissionDomain);
        ArgumentNullException.ThrowIfNull(demand);
        SvgHostAdmissionDomain domain = _domains.GetValue(admissionDomain, static _ => new SvgHostAdmissionDomain());
        SvgHostOwnerSlot? slot = domain.Owners.TryReserveOwnerSlot(admissionDomain, out ownerVersionAtDenial);
        if (slot is not null)
            return slot;

        domain.TryParkForAdmission(1, demand.Priority, requiresOwnerSlot: true);
        return domain.Owners.TryReserveOwnerSlot(admissionDomain, out ownerVersionAtDenial);
    }

    internal SvgHostPendingWaiter? Defer(
        ImageBox owner,
        IMarkdownSvgAdmission admission,
        MarkdownSvgRenderDemand demand,
        int loadGeneration,
        long capacityVersion,
        long requiredSourceReservationBytes,
        bool requiresOwnerSlot,
        long ownerVersionAtDenial,
        long demandVersionAtDenial,
        long visualDemandEpochAtDenial,
        object registrationAttempt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(admission);
        ArgumentNullException.ThrowIfNull(demand);
        if (requiredSourceReservationBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(requiredSourceReservationBytes));
        object domainKey = admission.AdmissionDomain;
        SvgHostAdmissionDomain domain = _domains.GetValue(domainKey, static _ => new SvgHostAdmissionDomain());
        return domain.Defer(
            owner,
            admission,
            demand,
            loadGeneration,
            capacityVersion,
            requiredSourceReservationBytes,
            requiresOwnerSlot,
            ownerVersionAtDenial,
            demandVersionAtDenial,
            visualDemandEpochAtDenial,
            registrationAttempt,
            cancellationToken);
    }

    internal long GetOwnerVersion(object admissionDomain) =>
        _domains.GetValue(admissionDomain, static _ => new SvgHostAdmissionDomain()).Owners.GetOwnerVersion();

    internal static void Touch(ImageBox.SvgTileRenderPlan plan) => plan.OwnerSlot?.Touch();
}
