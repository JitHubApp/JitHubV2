using MarkdownRenderer.Images;

namespace MarkdownRenderer.Svg.Resvg.Internal;

/// <summary>
/// Accounts for admitted requests that released their worker slot to wait for
/// a font catalog. Like <see cref="WorkerSchedulingQueue{T}"/>, it holds no
/// lock: the scheduler calls every member under its state lock.
/// </summary>
internal sealed class DetachedFontWaitLedger
{
    private readonly HashSet<WorkerFontWaitReservation> _reservations = [];
    private DetachedFontWaitCounts _counts;

    internal int Count => _counts.Count;
    internal int VisibleRenderCount => _counts.VisibleRenderCount;
    internal int VisibleTextRenderCount => _counts.VisibleTextRenderCount;
    internal int ReservationCount => _reservations.Count;

    /// <summary>Computes the counters for one more reservation without publishing them.</summary>
    /// <param name="visibleRender">Whether the reservation is a visible render.</param>
    /// <param name="visibleTextRender">Whether the reservation is a visible text render.</param>
    /// <returns>The next counters.</returns>
    /// <exception cref="OverflowException">A counter would overflow.</exception>
    internal DetachedFontWaitCounts PrepareAdd(bool visibleRender, bool visibleTextRender) => new(
        checked(_counts.Count + 1),
        checked(_counts.VisibleRenderCount + (visibleRender ? 1 : 0)),
        checked(_counts.VisibleTextRenderCount + (visibleTextRender ? 1 : 0)));

    /// <summary>Publishes a reservation's membership, then the counters prepared for it.</summary>
    /// <param name="reservation">The reservation to register.</param>
    /// <param name="next">The counters to publish, normally from <see cref="PrepareAdd"/>.</param>
    internal void Publish(WorkerFontWaitReservation reservation, DetachedFontWaitCounts next)
    {
        if (!_reservations.Add(reservation))
            throw new InvalidOperationException("The SVG font-wait reservation is already registered.");
        _counts = next;
    }

    /// <summary>
    /// Creates the reservation for active work that is detaching to wait for
    /// a font catalog, counting it as a visible render or visible text render
    /// from the work's current priority, and registers it.
    /// </summary>
    /// <param name="pending">The active work whose lease transfers to the reservation.</param>
    /// <param name="release">Releases an abandoned reservation under the state lock.</param>
    /// <param name="promote">Applies a later demand-ticket promotion under the state lock.</param>
    /// <returns>The registered reservation.</returns>
    internal WorkerFontWaitReservation Reserve(
        PendingWork pending,
        Action<WorkerFontWaitReservation> release,
        Action<WorkerFontWaitReservation> promote)
    {
        bool visibleRender = (pending.Operation is WorkerOperation.Render or WorkerOperation.RasterDecode) &&
            pending.Priority == MarkdownSvgRenderPriority.Visible;
        bool visibleTextRender = visibleRender && pending.RequiresFontCatalog;
        WorkerFontWaitReservation reservation = null!;
        reservation = new WorkerFontWaitReservation(
            () => release(reservation),
            visibleRender,
            visibleTextRender,
            pending.DemandTicket,
            isRender: pending.Operation is WorkerOperation.Render or WorkerOperation.RasterDecode,
            requiresFontCatalog: pending.RequiresFontCatalog,
            consumed: () => pending.HasDetachedFontWaitReservation = false);
        Register(
            reservation,
            visibleRender,
            visibleTextRender,
            pending,
            promote);
        return reservation;
    }

    /// <summary>
    /// Registers a new reservation for an active lease that is detaching: its
    /// demand-ticket promotion, then its membership and counters, then the
    /// lease's transfer marker. A failure rolls back and consumes the reservation.
    /// </summary>
    /// <param name="reservation">The new, unregistered reservation.</param>
    /// <param name="visibleRender">Whether it counts as a visible render.</param>
    /// <param name="visibleTextRender">Whether it counts as a visible text render.</param>
    /// <param name="pending">The active work whose lease transfers to the reservation.</param>
    /// <param name="promote">Applies a later demand-ticket promotion under the state lock.</param>
    /// <exception cref="OverflowException">A counter would overflow.</exception>
    internal void Register(
        WorkerFontWaitReservation reservation,
        bool visibleRender,
        bool visibleTextRender,
        PendingWork pending,
        Action<WorkerFontWaitReservation> promote)
    {
        try
        {
            // Compute every counter before publishing membership. Checked
            // overflow must not leave a registered reservation or a count
            // that has no owner.
            DetachedFontWaitCounts next = PrepareAdd(visibleRender, visibleTextRender);

            // Register before publishing detached counts/ownership. The
            // callback is asynchronous and will observe the fully-added
            // reservation after this state lock is released.
            if (reservation.DemandTicket is not null)
            {
                reservation.SetPromotionRegistration(reservation.DemandTicket.RegisterPromotion(
                    () => promote(reservation)));
            }

            // HashSet.Add can allocate while growing. Publish its
            // membership first so an allocation failure cannot strand
            // accounting or the active-lease transfer marker. Promotion
            // callbacks block on _stateLock until this transaction ends.
            Publish(reservation, next);
            pending.HasDetachedFontWaitReservation = true;
        }
        catch
        {
            // Roll back a partial publication, if any, before consuming
            // the local wrapper. Its release callback is membership-gated
            // and therefore cannot decrement another reservation's counts.
            Withdraw(reservation);
            reservation.TryConsume();
            throw;
        }
    }

    /// <summary>Rolls back a partial publication's membership; counters are untouched.</summary>
    /// <param name="reservation">The reservation to remove.</param>
    internal void Withdraw(WorkerFontWaitReservation reservation) => _reservations.Remove(reservation);

    /// <summary>Moves a reservation whose ticket was just consumed back into the queue's accounting.</summary>
    /// <param name="reservation">A registered reservation.</param>
    internal void Consume(WorkerFontWaitReservation reservation)
    {
        if (!_reservations.Remove(reservation))
            throw new InvalidOperationException("The SVG font-wait reservation was not registered with this pool.");
        _counts = new DetachedFontWaitCounts(
            _counts.Count - 1,
            _counts.VisibleRenderCount - (reservation.CountsAsVisibleRender ? 1 : 0),
            _counts.VisibleTextRenderCount - (reservation.CountsAsVisibleTextRender ? 1 : 0));
    }

    /// <summary>Releases an abandoned reservation if it is still registered.</summary>
    /// <param name="reservation">The reservation to release.</param>
    /// <returns>False when it was already consumed or released.</returns>
    internal bool TryRelease(WorkerFontWaitReservation reservation)
    {
        if (!_reservations.Remove(reservation))
            return false;

        bool visibleRender = reservation.CountsAsVisibleRender;
        bool visibleTextRender = reservation.CountsAsVisibleTextRender;
        if (_counts.Count <= 0 ||
            (visibleRender && _counts.VisibleRenderCount <= 0) ||
            (visibleTextRender && _counts.VisibleTextRenderCount <= 0))
            throw new InvalidOperationException("The SVG font-wait admission count underflowed.");
        _counts = new DetachedFontWaitCounts(
            _counts.Count - 1,
            _counts.VisibleRenderCount - (visibleRender ? 1 : 0),
            _counts.VisibleTextRenderCount - (visibleTextRender ? 1 : 0));
        return true;
    }

    /// <summary>Applies a registered reservation's visible promotion once.</summary>
    /// <param name="reservation">The reservation whose ticket was promoted.</param>
    /// <returns>True when the counters changed.</returns>
    internal bool TryPromote(WorkerFontWaitReservation reservation)
    {
        if (!_reservations.Contains(reservation) ||
            !reservation.PromoteVisible(out bool addedVisibleRender, out bool addedVisibleTextRender))
        {
            return false;
        }

        AddPromotion(addedVisibleRender, addedVisibleTextRender);
        return true;
    }

    /// <summary>Reconciles every reservation with its monotonic demand ticket.</summary>
    internal void PromoteFromTickets()
    {
        foreach (WorkerFontWaitReservation reservation in _reservations)
        {
            if (reservation.PromoteVisible(out bool addedVisibleRender, out bool addedVisibleTextRender))
                AddPromotion(addedVisibleRender, addedVisibleTextRender);
        }
    }

    private void AddPromotion(bool addedVisibleRender, bool addedVisibleTextRender) =>
        _counts = new DetachedFontWaitCounts(
            _counts.Count,
            _counts.VisibleRenderCount + (addedVisibleRender ? 1 : 0),
            _counts.VisibleTextRenderCount + (addedVisibleTextRender ? 1 : 0));
}
