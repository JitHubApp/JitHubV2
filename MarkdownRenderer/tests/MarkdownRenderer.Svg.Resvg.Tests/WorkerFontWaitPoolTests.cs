using System.Collections.Concurrent;
using MarkdownRenderer.Images;
using MarkdownRenderer.Svg.Resvg.Internal;
using Xunit;

namespace MarkdownRenderer.Svg.Resvg.Tests;

public sealed class WorkerFontWaitPoolTests
{
    [Fact]
    public async Task AllAdmittedRequestsCanParkAndReenterWithoutAdmittingNewWork()
    {
        var fontWaits = new DetachedFontWaitLedger();
        var policySnapshots = new ConcurrentQueue<SvgWorkerPolicySnapshot>();
        using WorkerScheduler pool = CreatePool(fontWaits, policySnapshots);
        var reservations = new List<WorkerFontWaitReservation>();
        var leases = new List<IDisposable>();
        try
        {
            // Exercise the actual admission queue and ticket transfer, not just
            // the policy arithmetic. No worker, pipe or mapping is created.
            Task<WorkerLease>[] admissions = Enumerable.Range(0, 33)
                .Select(_ => Acquire(pool, CancellationToken.None))
                .ToArray();
            Assert.True(admissions[0].IsCompletedSuccessfully);
            Assert.All(admissions.Skip(1), task => Assert.False(task.IsCompleted));

            foreach (Task<WorkerLease> admission in admissions)
            {
                IDisposable active = await GetLeaseAsync(admission);
                leases.Add(active);
                reservations.Add(Reserve(pool, active));
                active.Dispose();
            }
            Assert.Equal(33, fontWaits.Count);
            // New work is not admitted while parked continuations hold
            // every credit, but it waits for capacity instead of failing.
            Task<WorkerLease> waitingNew = Acquire(pool, CancellationToken.None);
            await Task.Delay(100);
            Assert.False(waitingNew.IsCompleted);
            Task waitingNewReleased = waitingNew.ContinueWith(
                static task =>
                {
                    if (task.IsCompletedSuccessfully)
                        task.Result.Dispose();
                },
                TaskScheduler.Default);

            // At q=0, d=33 every continuation already owns admission credit.
            // It must not wait for unrelated new work to release capacity.
            foreach (WorkerFontWaitReservation reservation in reservations)
            {
                IDisposable resumed = await GetLeaseAsync(Acquire(pool, CancellationToken.None, reservation));
                leases.Add(resumed);
                Assert.False(reservation.IsActive);
                resumed.Dispose();
            }
            Assert.Equal(0, fontWaits.Count);
            await waitingNewReleased.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(waitingNew.IsCompletedSuccessfully);
            using IDisposable fresh = await GetLeaseAsync(Acquire(pool, CancellationToken.None));
            Assert.DoesNotContain(policySnapshots, snapshot =>
                snapshot.ShouldStartSecondary != 0 || snapshot.SecondaryReadinessState != 0);
        }
        finally
        {
            pool.Dispose();
            foreach (IDisposable lease in leases)
                lease.Dispose();
            foreach (WorkerFontWaitReservation reservation in reservations)
                reservation.Dispose();
        }
    }

    [Fact]
    public async Task RealActiveLeaseCannotReserveTwiceOrAfterRelease()
    {
        var fontWaits = new DetachedFontWaitLedger();
        using WorkerScheduler pool = CreatePool(fontWaits);
        using IDisposable active = await GetLeaseAsync(Acquire(pool, CancellationToken.None));
        using WorkerFontWaitReservation reservation = Reserve(pool, active);

        Assert.Throws<InvalidOperationException>(() => Reserve(pool, active));
        Assert.Equal(1, fontWaits.Count);
        active.Dispose();
        // A distinct, never-reserved lease proves this is the active-lease
        // check, rather than the duplicate-reservation check above.
        using IDisposable released = await GetLeaseAsync(Acquire(pool, CancellationToken.None));
        released.Dispose();
        Assert.Throws<InvalidOperationException>(() => Reserve(pool, released));
        reservation.Dispose();
        Assert.Equal(0, fontWaits.Count);
    }

    [Fact]
    public async Task PromotionRegistrationLimitFailureDoesNotPublishDetachedOwnership()
    {
        var fontWaits = new DetachedFontWaitLedger();
        using WorkerScheduler pool = CreatePool(fontWaits);
        var ticket = new MarkdownSvgRenderDemand(MarkdownSvgRenderPriority.Overscan);
        using IDisposable active = await GetLeaseAsync(Acquire(
            pool,
            CancellationToken.None,
            demandTicket: ticket));
        IDisposable[] registrations = Enumerable.Range(0, MarkdownSvgRenderDemand.MaximumPromotionRegistrations)
            .Select(_ => ticket.RegisterPromotion(static () => { }))
            .ToArray();

        try
        {
            Assert.Throws<InvalidOperationException>(() => Reserve(pool, active));
            Assert.Equal(0, fontWaits.Count);
            Assert.Equal(0, fontWaits.ReservationCount);
            Assert.False(((WorkerLease)active).Pending.HasDetachedFontWaitReservation);

            // The failed reservation attempt must not dispose or consume any
            // of the ticket's earlier registrations while rolling back its
            // unpublished local wrapper.
            Assert.Throws<InvalidOperationException>(
                () => ticket.RegisterPromotion(static () => { }));
            registrations[0].Dispose();
            using IDisposable replacementRegistration = ticket.RegisterPromotion(static () => { });
        }
        finally
        {
            foreach (IDisposable registration in registrations)
                registration.Dispose();
        }

        // The lease still owns the pool's primary worker slot after the
        // failed reservation. Release it before asking this one-slot test pool
        // to admit the separate visible-render scenario below.
        active.Dispose();

        // Force checked visible-counter preparation to fail after creating a
        // local reservation wrapper. Rollback must not publish set membership
        // or the active-lease transfer marker.
        // A visible ticket queues every new promotion registration at once,
        // so its queue-admission probe observes each registration made.
        int visibleRegistrationAttempts = 0;
        var visibleTicket = new MarkdownSvgRenderDemand(
            MarkdownSvgRenderPriority.Visible,
            () => Interlocked.Increment(ref visibleRegistrationAttempts) > 0);
        using IDisposable visibleRender = await GetLeaseAsync(Acquire(
            pool,
            CancellationToken.None,
            demandTicket: visibleTicket,
            operation: WorkerOperation.Render,
            requiresFontCatalog: true));
        // Seed the counter at its limit through the ledger's own publication
        // API. The seed is the only registered reservation.
        var seed = new WorkerFontWaitReservation(static () => { });
        fontWaits.Publish(seed, new DetachedFontWaitCounts(0, int.MaxValue, 0));
        // Admission registered the lease's own promotion, which proves the
        // probe sees registrations on this ticket.
        int registrationsBeforeReserve = Volatile.Read(ref visibleRegistrationAttempts);
        Assert.Equal(1, registrationsBeforeReserve);
        try
        {
            Assert.Throws<OverflowException>(() => Reserve(pool, visibleRender));
            Assert.Equal(0, fontWaits.Count);
            Assert.Equal(1, fontWaits.ReservationCount);
            Assert.False(((WorkerLease)visibleRender).Pending.HasDetachedFontWaitReservation);
            Assert.Equal(registrationsBeforeReserve, Volatile.Read(ref visibleRegistrationAttempts));
        }
        finally
        {
            fontWaits.Withdraw(seed);
            fontWaits.Publish(seed, default);
            fontWaits.Withdraw(seed);
        }
    }

    [Fact]
    public async Task DetachedFontWaitPromotionUpdatesCountersOnceAndRetiresItsRegistrationOnResume()
    {
        var fontWaits = new DetachedFontWaitLedger();
        using WorkerScheduler pool = CreatePool(fontWaits);
        var demand = new MarkdownSvgRenderDemand(MarkdownSvgRenderPriority.Overscan);
        using IDisposable active = await GetLeaseAsync(Acquire(
            pool,
            CancellationToken.None,
            demandTicket: demand,
            operation: WorkerOperation.Render,
            requiresFontCatalog: true));
        using WorkerFontWaitReservation reservation = Reserve(pool, active);
        active.Dispose();

        Assert.Equal(1, fontWaits.Count);
        Assert.Equal(0, fontWaits.VisibleRenderCount);
        Assert.Equal(0, fontWaits.VisibleTextRenderCount);
        Assert.Equal(1, fontWaits.ReservationCount);

        Assert.True(demand.PromoteVisible());
        await WaitForCountAsync(() => fontWaits.VisibleRenderCount, 1);
        await WaitForCountAsync(() => fontWaits.VisibleTextRenderCount, 1);
        await WaitForTicketRegistrationsAsync(demand, 0);
        Assert.True(reservation.CountsAsVisibleRender);
        Assert.True(reservation.CountsAsVisibleTextRender);
        Assert.False(demand.PromoteVisible());

        using IDisposable resumed = await GetLeaseAsync(Acquire(
            pool,
            CancellationToken.None,
            reservation,
            demand,
            WorkerOperation.Render,
            requiresFontCatalog: true));
        Assert.False(reservation.IsActive);
        Assert.Equal(0, fontWaits.Count);
        Assert.Equal(0, fontWaits.VisibleRenderCount);
        Assert.Equal(0, fontWaits.VisibleTextRenderCount);
        Assert.Equal(0, fontWaits.ReservationCount);
        Assert.Equal(0, demand.PendingPromotionCount);
        resumed.Dispose();

        // A later detached owner disposed before promotion must remove its
        // callback and counters. Promoting that ticket afterwards is harmless.
        var canceledDemand = new MarkdownSvgRenderDemand(MarkdownSvgRenderPriority.Overscan);
        using IDisposable secondActive = await GetLeaseAsync(Acquire(
            pool,
            CancellationToken.None,
            demandTicket: canceledDemand,
            operation: WorkerOperation.Render,
            requiresFontCatalog: true));
        WorkerFontWaitReservation canceledReservation = Reserve(pool, secondActive);
        secondActive.Dispose();
        canceledReservation.Dispose();
        Assert.Equal(0, fontWaits.Count);
        Assert.Equal(0, fontWaits.VisibleRenderCount);
        Assert.Equal(0, fontWaits.VisibleTextRenderCount);
        Assert.Equal(0, canceledDemand.PendingPromotionCount);

        Assert.True(canceledDemand.PromoteVisible());
        await Task.Delay(25);
        Assert.Equal(0, fontWaits.Count);
        Assert.Equal(0, fontWaits.VisibleRenderCount);
        Assert.Equal(0, fontWaits.VisibleTextRenderCount);
        Assert.Equal(0, canceledDemand.PendingPromotionCount);
    }

    [Fact]
    public async Task ExpiredReadinessAdmissionCannotTransferToAnUncanceledCaller()
    {
        var fontWaits = new DetachedFontWaitLedger();
        using WorkerScheduler pool = CreatePool(fontWaits);
        using var readiness = new CancellationTokenSource();
        using var caller = new CancellationTokenSource();
        using IDisposable active = await GetLeaseAsync(Acquire(pool, readiness.Token));
        readiness.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(
            () => Rebind(pool, active, caller.Token, readiness.Token));
        Assert.True(((WorkerLease)active).Pending.IsCanceled);
        Assert.Equal(readiness.Token, ((WorkerLease)active).Pending.CancellationToken);
        Assert.False(caller.IsCancellationRequested);
    }

    [Fact]
    public async Task SuccessfulRebindDetachesReadinessAndPreservesCallerPreemption()
    {
        var fontWaits = new DetachedFontWaitLedger();
        using WorkerScheduler pool = CreatePool(fontWaits);
        using var readiness = new CancellationTokenSource();
        using var caller = new CancellationTokenSource();
        using IDisposable active = await GetLeaseAsync(Acquire(pool, readiness.Token));

        Rebind(pool, active, caller.Token, readiness.Token);
        readiness.Cancel();
        Assert.False(((WorkerLease)active).Pending.IsCanceled);
        Assert.Equal(caller.Token, ((WorkerLease)active).Pending.CancellationToken);
        caller.Cancel();
        Assert.True(((WorkerLease)active).Pending.IsCanceled);
    }

    private static WorkerScheduler CreatePool(
        DetachedFontWaitLedger fontWaits,
        ConcurrentQueue<SvgWorkerPolicySnapshot>? policySnapshots = null) =>
        TestWorkerSchedulers.Create(
            static () => true,
            policySnapshots is null ? null : new SvgWorkerPolicyTrace(observer: policySnapshots.Enqueue),
            detachedFontWaits: fontWaits);

    private static Task<WorkerLease> Acquire(
        WorkerScheduler pool,
        CancellationToken cancellationToken,
        WorkerFontWaitReservation? reservation = null,
        MarkdownSvgRenderDemand? demandTicket = null,
        WorkerOperation operation = WorkerOperation.Open,
        bool requiresFontCatalog = true) =>
        TestWorkerSchedulers.AcquireAsync(
            pool,
            cancellationToken,
            operation,
            demandTicket?.Priority ?? MarkdownSvgRenderPriority.Visible,
            requiresFontCatalog,
            reservation,
            demandTicket);

    private static async Task WaitForCountAsync(Func<int> count, int expected)
    {
        for (int attempt = 0; attempt < 200; attempt++)
        {
            if (count() == expected)
                return;
            await Task.Delay(5);
        }

        Assert.Equal(expected, count());
    }

    private static async Task WaitForTicketRegistrationsAsync(MarkdownSvgRenderDemand ticket, int expected)
    {
        for (int attempt = 0; attempt < 200; attempt++)
        {
            if (ticket.PendingPromotionCount == expected)
                return;
            await Task.Delay(5);
        }

        Assert.Equal(expected, ticket.PendingPromotionCount);
    }

    private static async Task<IDisposable> GetLeaseAsync(Task<WorkerLease> admission) =>
        await admission.WaitAsync(TimeSpan.FromSeconds(5));

    private static WorkerFontWaitReservation Reserve(WorkerScheduler pool, IDisposable active) =>
        pool.ReserveDetachedFontWait((WorkerLease)active);

    private static void Rebind(WorkerScheduler pool, IDisposable active, CancellationToken caller, CancellationToken readiness) =>
        pool.RebindLeaseCancellation((WorkerLease)active, caller, readiness);
}
