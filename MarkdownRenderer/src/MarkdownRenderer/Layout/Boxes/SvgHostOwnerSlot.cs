using System;
using MarkdownRenderer.Images;

namespace MarkdownRenderer.Layout.Boxes;

/// <summary>
/// One reserved, then committed, accepted-owner place in an SVG admission
/// domain. Disposing it returns the place and signals owner-slot waiters.
/// </summary>
internal sealed class SvgHostOwnerSlot : IDisposable
{
    private readonly object _sync = new();
    private SvgHostOwnerRegistry? _domain;
    private SvgHostOwnerEntry? _entry;
    private int _state;

    internal SvgHostOwnerSlot(object admissionDomain, SvgHostOwnerRegistry domain)
    {
        AdmissionDomain = admissionDomain;
        _domain = domain;
    }

    internal object AdmissionDomain { get; }

    internal void Commit(
        ImageBox owner,
        SvgTileRenderPlan plan,
        MarkdownSvgRenderDemand demand,
        long sourceReservationBytes)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(demand);
        if (sourceReservationBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(sourceReservationBytes));
        lock (_sync)
        {
            if (_state != 0)
                throw new InvalidOperationException("The SVG owner slot is no longer reserved.");

            SvgHostOwnerRegistry domain = _domain ?? throw new ObjectDisposedException(nameof(SvgHostOwnerSlot));
            SvgHostOwnerEntry entry = domain.Commit(owner, plan, demand, this, sourceReservationBytes);
            _entry = entry;
            _state = 1;
        }
    }

    internal void Touch()
    {
        SvgHostOwnerRegistry? domain;
        SvgHostOwnerEntry? entry;
        lock (_sync)
        {
            if (_state != 1)
                return;
            domain = _domain;
            entry = _entry;
        }

        if (domain is not null && entry is not null)
            domain.Touch(entry);
    }

    public void Dispose()
    {
        SvgHostOwnerRegistry? domain;
        SvgHostOwnerEntry? entry;
        lock (_sync)
        {
            if (_state == 2)
                return;
            _state = 2;
            domain = _domain;
            entry = _entry;
            _domain = null;
            _entry = null;
        }

        if (domain is not null)
            domain.Release(this, entry);
    }
}
