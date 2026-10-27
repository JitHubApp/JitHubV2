using System.Reflection;
using MarkdownRenderer.Images;
using MarkdownRenderer.Svg.Resvg.Internal;
using Xunit;

namespace MarkdownRenderer.Svg.Resvg.Tests;

public sealed class WorkerFontWaitPoolTests
{
    [Fact]
    public async Task AllAdmittedRequestsCanParkAndReenterWithoutAdmittingNewWork()
    {
        using WorkerPool pool = CreatePool();
        var reservations = new List<WorkerFontWaitReservation>();
        var leases = new List<IDisposable>();
        try
        {
            // Exercise the actual admission queue and ticket transfer, not just
            // the policy arithmetic. No worker, pipe or mapping is created.
            Task[] admissions = Enumerable.Range(0, 33)
                .Select(_ => Acquire(pool, CancellationToken.None))
                .ToArray();
            Assert.True(admissions[0].IsCompletedSuccessfully);
            Assert.All(admissions.Skip(1), task => Assert.False(task.IsCompleted));

            foreach (Task admission in admissions)
            {
                IDisposable active = await GetLeaseAsync(admission);
                leases.Add(active);
                reservations.Add(Reserve(pool, active));
                active.Dispose();
            }
            Assert.Equal(33, GetField(pool, "_detachedFontWaitCount"));
            // New work is not admitted while parked continuations hold
            // every credit, but it waits for capacity instead of failing.
            Task waitingNew = Acquire(pool, CancellationToken.None);
            await Task.Delay(100);
            Assert.False(waitingNew.IsCompleted);
            Task waitingNewReleased = waitingNew.ContinueWith(
                static task =>
                {
                    if (task.IsCompletedSuccessfully &&
                        task.GetType().GetProperty("Result")?.GetValue(task) is IDisposable lease)
                    {
                        lease.Dispose();
                    }
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
            Assert.Equal(0, GetField(pool, "_detachedFontWaitCount"));
            await waitingNewReleased.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(waitingNew.IsCompletedSuccessfully);
            using IDisposable fresh = await GetLeaseAsync(Acquire(pool, CancellationToken.None));
            Assert.Null(GetField(pool, "_secondary"));
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
        using WorkerPool pool = CreatePool();
        using IDisposable active = await GetLeaseAsync(Acquire(pool, CancellationToken.None));
        using WorkerFontWaitReservation reservation = Reserve(pool, active);

        Assert.IsType<InvalidOperationException>(Assert.Throws<TargetInvocationException>(
            () => Reserve(pool, active)).InnerException);
        Assert.Equal(1, GetField(pool, "_detachedFontWaitCount"));
        active.Dispose();
        // A distinct, never-reserved lease proves this is the active-lease
        // check, rather than the duplicate-reservation check above.
        using IDisposable released = await GetLeaseAsync(Acquire(pool, CancellationToken.None));
        released.Dispose();
        Assert.IsType<InvalidOperationException>(Assert.Throws<TargetInvocationException>(
            () => Reserve(pool, released)).InnerException);
        reservation.Dispose();
        Assert.Equal(0, GetField(pool, "_detachedFontWaitCount"));
    }

    [Fact]
    public async Task PromotionRegistrationLimitFailureDoesNotPublishDetachedOwnership()
    {
        using WorkerPool pool = CreatePool();
        var ticket = new MarkdownSvgDemandTicket(MarkdownSvgRenderPriority.Overscan);
        using IDisposable active = await GetLeaseAsync(Acquire(
            pool,
            CancellationToken.None,
            demandTicket: ticket));
        IDisposable[] registrations = Enumerable.Range(0, MarkdownSvgDemandTicket.MaximumPromotionRegistrations)
            .Select(_ => ticket.RegisterPromotion(static () => { }))
            .ToArray();

        try
        {
            TargetInvocationException failure = Assert.Throws<TargetInvocationException>(
                () => Reserve(pool, active));
            Assert.IsType<InvalidOperationException>(failure.InnerException);
            Assert.Equal(0, GetField(pool, "_detachedFontWaitCount"));
            Assert.Empty((HashSet<WorkerFontWaitReservation>)GetField(pool, "_detachedFontWaitReservations")!);
            Assert.False((bool)GetPendingProperty(active, "HasDetachedFontWaitReservation")!);

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
        var visibleTicket = new MarkdownSvgDemandTicket(MarkdownSvgRenderPriority.Visible);
        using IDisposable visibleRender = await GetLeaseAsync(Acquire(
            pool,
            CancellationToken.None,
            demandTicket: visibleTicket,
            operation: WorkerOperation.Render,
            requiresFontCatalog: true));
        SetField(pool, "_detachedVisibleRenderCount", int.MaxValue);
        try
        {
            TargetInvocationException overflow = Assert.Throws<TargetInvocationException>(
                () => Reserve(pool, visibleRender));
            Assert.IsType<OverflowException>(overflow.InnerException);
            Assert.Equal(0, GetField(pool, "_detachedFontWaitCount"));
            Assert.Empty((HashSet<WorkerFontWaitReservation>)GetField(pool, "_detachedFontWaitReservations")!);
            Assert.False((bool)GetPendingProperty(visibleRender, "HasDetachedFontWaitReservation")!);
            Assert.Equal(0, GetDemandRegistrationCount(visibleTicket));
        }
        finally
        {
            SetField(pool, "_detachedVisibleRenderCount", 0);
        }
    }

    [Fact]
    public async Task DetachedFontWaitPromotionUpdatesCountersOnceAndRetiresItsRegistrationOnResume()
    {
        using WorkerPool pool = CreatePool();
        var demand = new MarkdownSvgDemandTicket(MarkdownSvgRenderPriority.Overscan);
        using IDisposable active = await GetLeaseAsync(Acquire(
            pool,
            CancellationToken.None,
            demandTicket: demand,
            operation: WorkerOperation.Render,
            requiresFontCatalog: true));
        using WorkerFontWaitReservation reservation = Reserve(pool, active);
        active.Dispose();

        Assert.Equal(1, GetField(pool, "_detachedFontWaitCount"));
        Assert.Equal(0, GetField(pool, "_detachedVisibleRenderCount"));
        Assert.Equal(0, GetField(pool, "_detachedVisibleTextRenderCount"));
        Assert.Single((HashSet<WorkerFontWaitReservation>)GetField(pool, "_detachedFontWaitReservations")!);

        Assert.True(demand.PromoteVisible());
        await WaitForPoolFieldAsync(pool, "_detachedVisibleRenderCount", 1);
        await WaitForPoolFieldAsync(pool, "_detachedVisibleTextRenderCount", 1);
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
        Assert.Equal(0, GetField(pool, "_detachedFontWaitCount"));
        Assert.Equal(0, GetField(pool, "_detachedVisibleRenderCount"));
        Assert.Equal(0, GetField(pool, "_detachedVisibleTextRenderCount"));
        Assert.Empty((HashSet<WorkerFontWaitReservation>)GetField(pool, "_detachedFontWaitReservations")!);
        Assert.Equal(0, demand.PendingPromotionCount);
        resumed.Dispose();

        // A later detached owner disposed before promotion must remove its
        // callback and counters. Promoting that ticket afterwards is harmless.
        var canceledDemand = new MarkdownSvgDemandTicket(MarkdownSvgRenderPriority.Overscan);
        using IDisposable secondActive = await GetLeaseAsync(Acquire(
            pool,
            CancellationToken.None,
            demandTicket: canceledDemand,
            operation: WorkerOperation.Render,
            requiresFontCatalog: true));
        WorkerFontWaitReservation canceledReservation = Reserve(pool, secondActive);
        secondActive.Dispose();
        canceledReservation.Dispose();
        Assert.Equal(0, GetField(pool, "_detachedFontWaitCount"));
        Assert.Equal(0, GetField(pool, "_detachedVisibleRenderCount"));
        Assert.Equal(0, GetField(pool, "_detachedVisibleTextRenderCount"));
        Assert.Equal(0, canceledDemand.PendingPromotionCount);

        Assert.True(canceledDemand.PromoteVisible());
        await Task.Delay(25);
        Assert.Equal(0, GetField(pool, "_detachedFontWaitCount"));
        Assert.Equal(0, GetField(pool, "_detachedVisibleRenderCount"));
        Assert.Equal(0, GetField(pool, "_detachedVisibleTextRenderCount"));
        Assert.Equal(0, canceledDemand.PendingPromotionCount);
    }

    [Fact]
    public async Task ExpiredReadinessAdmissionCannotTransferToAnUncanceledCaller()
    {
        using WorkerPool pool = CreatePool();
        using var readiness = new CancellationTokenSource();
        using var caller = new CancellationTokenSource();
        using IDisposable active = await GetLeaseAsync(Acquire(pool, readiness.Token));
        readiness.Cancel();

        TargetInvocationException exception = Assert.Throws<TargetInvocationException>(
            () => Rebind(pool, active, caller.Token, readiness.Token));
        Assert.IsAssignableFrom<OperationCanceledException>(exception.InnerException);
        Assert.True((bool)GetPendingProperty(active, "IsCanceled")!);
        Assert.Equal(readiness.Token, GetPendingProperty(active, "CancellationToken"));
        Assert.False(caller.IsCancellationRequested);
    }

    [Fact]
    public async Task SuccessfulRebindDetachesReadinessAndPreservesCallerPreemption()
    {
        using WorkerPool pool = CreatePool();
        using var readiness = new CancellationTokenSource();
        using var caller = new CancellationTokenSource();
        using IDisposable active = await GetLeaseAsync(Acquire(pool, readiness.Token));

        Rebind(pool, active, caller.Token, readiness.Token);
        readiness.Cancel();
        Assert.False((bool)GetPendingProperty(active, "IsCanceled")!);
        Assert.Equal(caller.Token, GetPendingProperty(active, "CancellationToken"));
        caller.Cancel();
        Assert.True((bool)GetPendingProperty(active, "IsCanceled")!);
    }

    private static WorkerPool CreatePool() => new(
        new ResvgMarkdownSvgRendererOptions(),
        TimeProvider.System,
        static () => true,
        static (_, _) => throw new InvalidOperationException("Admission-only tests must not create a source mapping."),
        static (_, _) => throw new InvalidOperationException("Admission-only tests must not create an output mapping."));

    private static Task Acquire(
        WorkerPool pool,
        CancellationToken cancellationToken,
        WorkerFontWaitReservation? reservation = null,
        MarkdownSvgDemandTicket? demandTicket = null,
        WorkerOperation operation = WorkerOperation.Open,
        bool requiresFontCatalog = true) =>
        (Task)FindMethod("AcquireAsync").Invoke(pool,
        [operation, demandTicket?.Priority ?? MarkdownSvgRenderPriority.Visible, cancellationToken, requiresFontCatalog, 0L, false, reservation, demandTicket])!;

    private static async Task WaitForPoolFieldAsync(WorkerPool pool, string name, int expected)
    {
        for (int attempt = 0; attempt < 200; attempt++)
        {
            if ((int)GetField(pool, name)! == expected)
                return;
            await Task.Delay(5);
        }

        Assert.Equal(expected, GetField(pool, name));
    }

    private static async Task WaitForTicketRegistrationsAsync(MarkdownSvgDemandTicket ticket, int expected)
    {
        for (int attempt = 0; attempt < 200; attempt++)
        {
            if (ticket.PendingPromotionCount == expected)
                return;
            await Task.Delay(5);
        }

        Assert.Equal(expected, ticket.PendingPromotionCount);
    }

    private static async Task<IDisposable> GetLeaseAsync(Task admission)
    {
        await admission.WaitAsync(TimeSpan.FromSeconds(5));
        return Assert.IsAssignableFrom<IDisposable>(admission.GetType().GetProperty("Result")!.GetValue(admission));
    }

    private static WorkerFontWaitReservation Reserve(WorkerPool pool, IDisposable active) =>
        (WorkerFontWaitReservation)FindMethod("ReserveDetachedFontWait").Invoke(pool, [active])!;

    private static void Rebind(WorkerPool pool, IDisposable active, CancellationToken caller, CancellationToken readiness) =>
        FindMethod("RebindLeaseCancellation").Invoke(pool, [active, caller, readiness]);

    private static MethodInfo FindMethod(string name) =>
        typeof(WorkerPool).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!;

    private static object? GetField(WorkerPool pool, string name) =>
        typeof(WorkerPool).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(pool);

    private static void SetField(WorkerPool pool, string name, object value) =>
        typeof(WorkerPool).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(pool, value);

    private static int GetDemandRegistrationCount(MarkdownSvgDemandTicket ticket) =>
        (int)typeof(MarkdownSvgDemandTicket).GetField(
            "_activeRegistrations",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(ticket)!;

    private static object? GetPendingProperty(IDisposable active, string name)
    {
        object pending = active.GetType().GetProperty("Pending")!.GetValue(active)!;
        return pending.GetType().GetProperty(name)!.GetValue(pending);
    }
}
