using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using MarkdownRenderer.Layout.Boxes;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

public sealed class BitmapOutputAdmissionTests
{
    [Fact]
    public void SharedBudgetUsesArchitectureSpecificCapAndTransitionTiers()
    {
        BitmapOutputAdmission admission = BitmapOutputAdmission.Shared;
        long capacity = Environment.Is64BitProcess ? 64L * 1024 * 1024 : 32L * 1024 * 1024;
        long headroom = Environment.Is64BitProcess ? 16L * 1024 * 1024 : 8L * 1024 * 1024;

        Assert.Equal(capacity, admission.CapacityBytes);
        Assert.Equal(headroom, admission.MaximumSingleOutputBytes);
        Assert.Equal(capacity - (2 * headroom), admission.SpeculativeLimitBytes);
        Assert.Equal(capacity - headroom, admission.VisibleInitialLimitBytes);
    }

    [Fact]
    public void ThreeAdmissionTiersKeepBothVisibleReservesOutOfSpeculativeUse()
    {
        var admission = new BitmapOutputAdmission(capacityBytes: 64, transitionHeadroomBytes: 16);
        var held = new List<BitmapOutputAdmission.Reservation>();
        try
        {
            Assert.True(admission.TryReserve(16, isVisible: false, isReplacement: false, out var speculative1, out _));
            Assert.True(admission.TryReserve(16, isVisible: false, isReplacement: false, out var speculative2, out _));
            held.Add(speculative1!);
            held.Add(speculative2!);
            Assert.False(admission.TryReserve(1, isVisible: false, isReplacement: false, out _, out _));

            Assert.True(admission.TryReserve(16, isVisible: true, isReplacement: false, out var visibleInitial, out _));
            held.Add(visibleInitial!);
            Assert.False(admission.TryReserve(1, isVisible: true, isReplacement: false, out _, out _));

            Assert.True(admission.TryReserve(16, isVisible: true, isReplacement: true, out var replacement, out _));
            held.Add(replacement!);
        Assert.Equal(64L, admission.GetSnapshot().ChargedBytes);
            Assert.False(admission.TryReserve(1, isVisible: true, isReplacement: true, out _, out _));
        }
        finally
        {
            foreach (BitmapOutputAdmission.Reservation reservation in held)
                reservation.Dispose();
        }

        Assert.Equal(0L, admission.GetSnapshot().ChargedBytes);
    }

    [Fact]
    public void OversizedSingleOutputIsRejectedAtTheHeadroomLimit()
    {
        var admission = new BitmapOutputAdmission(capacityBytes: 64, transitionHeadroomBytes: 16);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            admission.TryReserve(17, isVisible: true, isReplacement: true, out _, out _));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            admission.TryRegisterWaiter(
                new RecordingWaiter(_ => { }), 17, isVisible: true, isReplacement: true, 0, out _));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new BitmapOutputAdmission(capacityBytes: 32, transitionHeadroomBytes: 16));
    }

    [Fact]
    public void SplitKeepsAggregateChargeConstantAndEachNativeOwnerCanReleaseSeparately()
    {
        var admission = new BitmapOutputAdmission(capacityBytes: 64, transitionHeadroomBytes: 16);
        Assert.True(admission.TryReserve(16, isVisible: true, isReplacement: true, out var combined, out _));
        BitmapOutputAdmission.Reservation owner = combined!;
        BitmapOutputAdmission.Reservation frame = owner.Split(6);

        Assert.Equal(10L, owner.Bytes);
        Assert.Equal(6L, frame.Bytes);
        Assert.Equal(16L, admission.GetSnapshot().ChargedBytes);
        Assert.Equal(2, admission.GetSnapshot().ActiveReservationCount);

        Assert.True(owner.Commit());
        Assert.True(frame.Commit());
        Assert.True(owner.TransferToEntry());
        Assert.True(frame.TransferToEntry());
        Assert.True(frame.ReleaseAfterSuccessfulCleanup());
        Assert.True(frame.ReleaseAfterSuccessfulCleanup());
        Assert.Equal(10L, admission.GetSnapshot().ChargedBytes);
        Assert.True(owner.ReleaseAfterSuccessfulCleanup());
        Assert.Equal(0L, admission.GetSnapshot().ChargedBytes);
    }

    [Fact]
    public void DisposingUncommittedReservationReleasesExactlyOnce()
    {
        var admission = new BitmapOutputAdmission(capacityBytes: 64, transitionHeadroomBytes: 16);
        Assert.True(admission.TryReserve(8, isVisible: true, isReplacement: false, out var reservation, out _));
        long version = admission.GetSnapshot().CapacityVersion;

        reservation!.Dispose();
        reservation.Dispose();

        BitmapOutputAdmission.Snapshot snapshot = admission.GetSnapshot();
        Assert.Equal(0L, snapshot.ChargedBytes);
        Assert.Equal(0, snapshot.ActiveReservationCount);
        Assert.Equal(version + 1, snapshot.CapacityVersion);
        Assert.False(reservation.IsCommitted);
    }

    [Fact]
    public void DisposingCommittedNativeOwnershipRetainsAnUncertainCharge()
    {
        var admission = new BitmapOutputAdmission(capacityBytes: 64, transitionHeadroomBytes: 16);
        Assert.True(admission.TryReserve(12, isVisible: true, isReplacement: true, out var reservation, out _));
        Assert.True(reservation!.Commit());
        Assert.True(reservation.TransferToEntry());

        reservation.Dispose();
        reservation.Dispose();

        BitmapOutputAdmission.Snapshot snapshot = admission.GetSnapshot();
        Assert.Equal(12L, snapshot.ChargedBytes);
        Assert.Equal(0L, snapshot.ActiveBytes);
        Assert.Equal(12L, snapshot.UncertainBytes);
        Assert.Equal(1, snapshot.UncertainReservationCount);
        Assert.False(reservation.ReleaseAfterSuccessfulCleanup());

        // The 12-byte uncertain owner plus three 16-byte replacements nearly
        // fill the entire 64-byte test budget. A new 8-byte replacement must
        // be denied because uncertain native ownership remains charged.
        var held = new List<BitmapOutputAdmission.Reservation>();
        try
        {
            for (int i = 0; i < 3; i++)
            {
                Assert.True(admission.TryReserve(
                    16, isVisible: true, isReplacement: true, out var replacement, out _));
                held.Add(replacement!);
            }

            Assert.Equal(60L, admission.GetSnapshot().ChargedBytes);
            Assert.False(admission.TryReserve(8, isVisible: true, isReplacement: true, out _, out _));
        }
        finally
        {
            foreach (BitmapOutputAdmission.Reservation item in held)
                item.Dispose();
        }

        Assert.Equal(12L, admission.GetSnapshot().ChargedBytes);
    }

    [Fact]
    public void SuccessfulCleanupIsTheOnlyCommittedReleasePathAndIsIdempotent()
    {
        var admission = new BitmapOutputAdmission(capacityBytes: 64, transitionHeadroomBytes: 16);
        Assert.True(admission.TryReserve(16, isVisible: true, isReplacement: true, out var reservation, out _));
        Assert.False(reservation!.ReleaseAfterSuccessfulCleanup());
        Assert.True(reservation!.Commit());
        Assert.True(reservation.ReleaseAfterSuccessfulCleanup());
        Assert.True(reservation.ReleaseAfterSuccessfulCleanup());

        Assert.Equal(0L, admission.GetSnapshot().ChargedBytes);
        Assert.False(reservation.MarkUncertain());

        Assert.True(admission.TryReserve(16, isVisible: true, isReplacement: true, out var transferred, out _));
        Assert.True(transferred!.Commit());
        Assert.True(transferred.TransferToEntry());
        Assert.True(transferred.ReleaseAfterSuccessfulCleanup());
        Assert.True(transferred.ReleaseAfterSuccessfulCleanup());
        Assert.Equal(0L, admission.GetSnapshot().ChargedBytes);
    }

    [Fact]
    public void ReservationCanBeTransferredToOnlyOneCacheEntry()
    {
        var admission = new BitmapOutputAdmission(capacityBytes: 64, transitionHeadroomBytes: 16);
        Assert.True(admission.TryReserve(16, isVisible: true, isReplacement: true, out var reservation, out _));
        Assert.True(reservation!.Commit());

        Assert.True(reservation.TransferToEntry());
        Assert.False(reservation.TransferToEntry());
        Assert.Equal(16L, admission.GetSnapshot().ChargedBytes);
        Assert.Equal(1, admission.GetSnapshot().ActiveReservationCount);

        Assert.True(reservation.ReleaseAfterSuccessfulCleanup());
        Assert.Equal(0L, admission.GetSnapshot().ChargedBytes);
    }

    [Fact]
    public void CommittedReservationCanOnlyResizeDownAndWakesWhenChargeShrinks()
    {
        var queue = new ManualNotificationQueue();
        var admission = new BitmapOutputAdmission(64, 16, testNotificationScheduler: queue.Enqueue);
        Assert.True(admission.TryReserve(16, isVisible: true, isReplacement: true, out var reservation, out _));
        Assert.True(reservation!.Commit());
        Assert.True(reservation.TransferToEntry());
        long version = admission.GetSnapshot().CapacityVersion;

        Assert.True(reservation.ResizeDown(8));
        Assert.False(reservation.ResizeDown(9));
        Assert.Equal(8L, admission.GetSnapshot().ChargedBytes);
        Assert.Equal(version + 1, admission.GetSnapshot().CapacityVersion);
        Assert.Equal(0, queue.Count);
        Assert.True(reservation.ReleaseAfterSuccessfulCleanup());
    }

    [Fact]
    public void WaiterCapacityIsBoundedAndDuplicateLiveOwnersCannotUnregisterEachOther()
    {
        var admission = new BitmapOutputAdmission(64, 16, maximumWaiters: 2);
        List<BitmapOutputAdmission.Reservation> held = HoldFullBudget(admission);
        var firstOwner = new RecordingWaiter(_ => { });
        var secondOwner = new RecordingWaiter(_ => { });

        Assert.True(admission.TryRegisterWaiter(firstOwner, 16, false, false, 0, out var first));
        Assert.False(admission.TryRegisterWaiter(firstOwner, 16, true, true, 0, out var duplicate));
        Assert.Null(duplicate);
        Assert.True(admission.TryRegisterWaiter(secondOwner, 16, false, false, 0, out var second));
        Assert.False(admission.TryRegisterWaiter(new RecordingWaiter(_ => { }), 1, true, true, 0, out var overflow));
        Assert.Null(overflow);
        Assert.Equal(2, admission.GetSnapshot().WaiterCount);
        GC.KeepAlive(firstOwner);
        GC.KeepAlive(secondOwner);

        first!.Dispose();
        second!.Dispose();
        foreach (BitmapOutputAdmission.Reservation item in held)
            item.Dispose();
    }

    [Fact]
    public void SpeculativeWaitersLeaveEightSlotsForVisibleDemandWithoutIncreasingTotalCap()
    {
        var queue = new ManualNotificationQueue();
        var admission = new BitmapOutputAdmission(64, 16, testNotificationScheduler: queue.Enqueue);
        var held = new List<BitmapOutputAdmission.Reservation>();
        var owners = new List<RecordingWaiter>();
        var registrations = new List<BitmapOutputAdmission.WaiterRegistration>();
        try
        {
            // Forty charged bytes exceed the speculative tier (32) while
            // leaving exactly eight bytes in the visible-initial tier (48).
            Assert.True(admission.TryReserve(16, true, true, out var firstCharge, out _));
            Assert.True(admission.TryReserve(16, true, true, out var secondCharge, out _));
            Assert.True(admission.TryReserve(8, true, true, out var thirdCharge, out _));
            held.Add(firstCharge!);
            held.Add(secondCharge!);
            held.Add(thirdCharge!);

            long capacityVersion = admission.GetSnapshot().CapacityVersion;
            for (int index = 0; index < 248; index++)
            {
                var owner = new RecordingWaiter(_ => { });
                owners.Add(owner);
                Assert.True(admission.TryRegisterWaiter(
                    owner, 16, isVisible: false, isReplacement: false,
                    capacityVersion, out var registration));
                registrations.Add(registration!);
            }

            Assert.False(admission.TryRegisterWaiter(
                new RecordingWaiter(_ => { }), 16, isVisible: false,
                isReplacement: false, capacityVersion, out var speculativeOverflow));
            Assert.Null(speculativeOverflow);
            Assert.Equal(248, admission.GetSnapshot().WaiterCount);

            RecordingWaiter? firstVisible = null;
            for (int index = 0; index < 8; index++)
            {
                var owner = new RecordingWaiter(_ => { });
                owners.Add(owner);
                if (index == 0)
                    firstVisible = owner;
                Assert.True(admission.TryRegisterWaiter(
                    owner, 8, isVisible: true, isReplacement: false,
                    capacityVersion, out var registration));
                registrations.Add(registration!);
            }

            // WaiterEntry deliberately weakly references its owner. Force a
            // collection while this fixture's owner list is still strongly
            // rooted so the admission snapshot proves the live queue survives
            // GC rather than depending on incidental JIT local lifetimes.
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            BitmapOutputAdmission.Snapshot fullQueue = admission.GetSnapshot();
            Assert.Equal(256, fullQueue.WaiterCount);
            Assert.Equal(capacityVersion, fullQueue.CapacityVersion);
            Assert.False(admission.TryRegisterWaiter(
                new RecordingWaiter(_ => { }), 1, isVisible: true,
                isReplacement: false, capacityVersion, out var visibleOverflow));
            Assert.Null(visibleOverflow);

            // The visible request is notified at the visible tier even though
            // every speculative waiter is dormant above its lower byte limit.
            queue.RunAll();
            Assert.Single(Assert.IsType<RecordingWaiter>(firstVisible).Versions);
            Assert.Equal(capacityVersion, admission.GetSnapshot().CapacityVersion);
            Assert.Equal(256, admission.GetSnapshot().WaiterCount);
            GC.KeepAlive(owners);
        }
        finally
        {
            foreach (BitmapOutputAdmission.WaiterRegistration registration in registrations)
                registration.Dispose();
            queue.RunAll();
            foreach (BitmapOutputAdmission.Reservation reservation in held)
                reservation.Dispose();
        }

        Assert.Equal(0, admission.GetSnapshot().WaiterCount);
        Assert.Equal(0L, admission.GetSnapshot().ChargedBytes);
    }

    [Fact]
    public void PromotingSpeculativeWaiterFreesQuotaAndAdvancesOnlySlotVersion()
    {
        var admission = new BitmapOutputAdmission(64, 16, maximumWaiters: 9);
        List<BitmapOutputAdmission.Reservation> held = HoldFullBudget(admission);
        BitmapOutputAdmission.WaiterRegistration? registration = null;
        BitmapOutputAdmission.WaiterRegistration? replacement = null;
        try
        {
            var promoted = new RecordingWaiter(_ => { });
            Assert.True(admission.TryRegisterWaiter(
                promoted, 8, isVisible: false, isReplacement: false, 0, out registration));
            var overflow = new RecordingWaiter(_ => { });
            BitmapOutputAdmission.Snapshot beforePromotion = admission.GetSnapshot();
            Assert.False(admission.TryRegisterWaiter(
                overflow, 8, isVisible: false, isReplacement: false,
                beforePromotion.CapacityVersion, out _));

            Assert.True(registration!.UpdateDemand(
                8,
                isVisible: true,
                isReplacement: false,
                beforePromotion.CapacityVersion,
                out long capacityVersionAtAttempt,
                out long slotVersionAtAttempt));

            BitmapOutputAdmission.Snapshot afterPromotion = admission.GetSnapshot();
            Assert.Equal(beforePromotion.CapacityVersion, capacityVersionAtAttempt);
            Assert.Equal(beforePromotion.CapacityVersion, afterPromotion.CapacityVersion);
            Assert.NotEqual(beforePromotion.WaiterSlotVersion, afterPromotion.WaiterSlotVersion);
            Assert.Equal(afterPromotion.WaiterSlotVersion, slotVersionAtAttempt);
            Assert.True(admission.TryRegisterWaiter(
                overflow, 8, isVisible: false, isReplacement: false,
                afterPromotion.CapacityVersion, out replacement));
            Assert.Equal(2, admission.GetSnapshot().WaiterCount);
            GC.KeepAlive(promoted);
            GC.KeepAlive(overflow);
        }
        finally
        {
            registration?.Dispose();
            replacement?.Dispose();
            foreach (BitmapOutputAdmission.Reservation item in held)
                item.Dispose();
        }
    }

    [Fact]
    public void CancellationReleasesSpeculativeQuotaWithoutChangingCapacityGeneration()
    {
        var admission = new BitmapOutputAdmission(64, 16, maximumWaiters: 9);
        List<BitmapOutputAdmission.Reservation> held = HoldFullBudget(admission);
        BitmapOutputAdmission.WaiterRegistration? first = null;
        BitmapOutputAdmission.WaiterRegistration? retried = null;
        try
        {
            var firstOwner = new RecordingWaiter(_ => { });
            Assert.True(admission.TryRegisterWaiter(
                firstOwner, 8, isVisible: false,
                isReplacement: false, 0, out first));
            var overflow = new RecordingWaiter(_ => { });
            BitmapOutputAdmission.Snapshot beforeCancellation = admission.GetSnapshot();
            Assert.False(admission.TryRegisterWaiter(
                overflow, 8, isVisible: false, isReplacement: false,
                beforeCancellation.CapacityVersion, out _));

            first!.Dispose();
            BitmapOutputAdmission.Snapshot afterCancellation = admission.GetSnapshot();
            Assert.Equal(beforeCancellation.CapacityVersion, afterCancellation.CapacityVersion);
            Assert.NotEqual(beforeCancellation.WaiterSlotVersion, afterCancellation.WaiterSlotVersion);
            Assert.True(admission.TryRegisterWaiter(
                overflow, 8, isVisible: false, isReplacement: false,
                afterCancellation.CapacityVersion, out retried));
            Assert.Equal(1, admission.GetSnapshot().WaiterCount);
            GC.KeepAlive(firstOwner);
            GC.KeepAlive(overflow);
        }
        finally
        {
            first?.Dispose();
            retried?.Dispose();
            foreach (BitmapOutputAdmission.Reservation item in held)
                item.Dispose();
        }
    }

    [Fact]
    public void CanceledWaiterFreesQueueSlotWithoutChangingBitmapCapacityAndOverflowRetries()
    {
        var queue = new ManualNotificationQueue();
        var admission = new BitmapOutputAdmission(
            64, 16, maximumWaiters: 1, testNotificationScheduler: queue.Enqueue);
        var held = new List<BitmapOutputAdmission.Reservation>();
        BitmapOutputAdmission.WaiterRegistration? firstRegistration = null;
        BitmapOutputAdmission.WaiterRegistration? overflowRegistration = null;
        try
        {
            // Leave 16 bytes free. The first waiter is eligible but occupies
            // the sole bounded slot; its notification is not a byte grant.
            for (int i = 0; i < 3; i++)
            {
                Assert.True(admission.TryReserve(
                    16, isVisible: true, isReplacement: true, out var reservation, out _));
                held.Add(reservation!);
            }

            var first = new RecordingWaiter(_ => { });
            Assert.True(admission.TryRegisterWaiter(
                first, 16, true, true, 0, out firstRegistration));
            queue.RunAll();

            BitmapOutputAdmission.Snapshot beforeOverflow = admission.GetSnapshot();
            var overflow = new RecordingWaiter(_ => { });
            Assert.False(admission.TryRegisterWaiter(
                overflow,
                16,
                isVisible: true,
                isReplacement: true,
                beforeOverflow.CapacityVersion,
                out _,
                out long capacityAtFailure,
                out long slotAtFailure));
            Assert.Equal(beforeOverflow.CapacityVersion, capacityAtFailure);
            Assert.Equal(beforeOverflow.WaiterSlotVersion, slotAtFailure);
            Assert.Equal(1, admission.GetSnapshot().WaiterCount);

            // Repeated paint observations at unchanged generations remain
            // dormant rather than retrying registration every frame.
            for (int i = 0; i < 100; i++)
            {
                Assert.False(BitmapOutputAdmission.ShouldRetryWaiterRegistration(
                    registrationFailed: true,
                    waiterPresent: false,
                    observedCapacityVersion: capacityAtFailure,
                    currentCapacityVersion: beforeOverflow.CapacityVersion,
                    observedWaiterSlotVersion: slotAtFailure,
                    currentWaiterSlotVersion: beforeOverflow.WaiterSlotVersion,
                    visibilityPromoted: false));
            }
            Assert.True(BitmapOutputAdmission.ShouldRetryWaiterRegistration(
                registrationFailed: true,
                waiterPresent: false,
                observedCapacityVersion: capacityAtFailure,
                currentCapacityVersion: beforeOverflow.CapacityVersion,
                observedWaiterSlotVersion: slotAtFailure,
                currentWaiterSlotVersion: beforeOverflow.WaiterSlotVersion,
                visibilityPromoted: true));

            firstRegistration!.Dispose(); // Cancellation frees only a waiter slot.
            BitmapOutputAdmission.Snapshot afterCancellation = admission.GetSnapshot();
            Assert.Equal(beforeOverflow.CapacityVersion, afterCancellation.CapacityVersion);
            Assert.Equal(beforeOverflow.ChargedBytes, afterCancellation.ChargedBytes);
            Assert.NotEqual(slotAtFailure, afterCancellation.WaiterSlotVersion);
            Assert.True(BitmapOutputAdmission.ShouldRetryWaiterRegistration(
                registrationFailed: true,
                waiterPresent: false,
                observedCapacityVersion: capacityAtFailure,
                currentCapacityVersion: afterCancellation.CapacityVersion,
                observedWaiterSlotVersion: slotAtFailure,
                currentWaiterSlotVersion: afterCancellation.WaiterSlotVersion,
                visibilityPromoted: false));

            Assert.True(admission.TryRegisterWaiter(
                overflow,
                16,
                isVisible: true,
                isReplacement: true,
                afterCancellation.CapacityVersion,
                out overflowRegistration,
                out long retryCapacityVersion,
                out long retrySlotVersion));
            Assert.Equal(afterCancellation.CapacityVersion, retryCapacityVersion);
            Assert.Equal(afterCancellation.WaiterSlotVersion, retrySlotVersion);
            Assert.Equal(1, admission.GetSnapshot().WaiterCount);
            GC.KeepAlive(first);
            GC.KeepAlive(overflow);
        }
        finally
        {
            firstRegistration?.Dispose();
            overflowRegistration?.Dispose();
            foreach (BitmapOutputAdmission.Reservation reservation in held)
                reservation.Dispose();
            queue.RunAll();
        }
        Assert.Equal(0, admission.GetSnapshot().WaiterCount);
    }

    [Fact]
    public void AdmittedCacheHitSettlementFreesWaiterSlotWithoutChangingByteCapacity()
    {
        var queue = new ManualNotificationQueue();
        var admission = new BitmapOutputAdmission(
            64, 16, maximumWaiters: 1, testNotificationScheduler: queue.Enqueue);
        var held = new List<BitmapOutputAdmission.Reservation>();
        BitmapOutputAdmission.WaiterRegistration? cacheHitRegistration = null;
        BitmapOutputAdmission.WaiterRegistration? overflowRegistration = null;
        try
        {
            // The existing charge stands for a bitmap already admitted in the
            // cache; the callback's cache hit will not add another charge.
            for (int i = 0; i < 3; i++)
            {
                Assert.True(admission.TryReserve(
                    16, isVisible: true, isReplacement: true, out var reservation, out _));
                held.Add(reservation!);
            }

            var cacheHit = new RecordingWaiter(_ => { });
            Assert.True(admission.TryRegisterWaiter(
                cacheHit, 16, true, true, 0, out cacheHitRegistration));
            queue.RunAll();
            Assert.Equal(16L, admission.GetSnapshot().OfferedBytes);

            var overflow = new RecordingWaiter(_ => { });
            BitmapOutputAdmission.Snapshot beforeOverflow = admission.GetSnapshot();
            Assert.False(admission.TryRegisterWaiter(
                overflow,
                16,
                isVisible: true,
                isReplacement: true,
                beforeOverflow.CapacityVersion,
                out _,
                out long capacityAtFailure,
                out long slotAtFailure));

            Assert.True(cacheHitRegistration!.CompleteRetry(capacityClaimed: true));
            BitmapOutputAdmission.Snapshot afterCacheHit = admission.GetSnapshot();
            Assert.Equal(beforeOverflow.CapacityVersion, afterCacheHit.CapacityVersion);
            Assert.Equal(beforeOverflow.ChargedBytes, afterCacheHit.ChargedBytes);
            Assert.NotEqual(slotAtFailure, afterCacheHit.WaiterSlotVersion);
            Assert.True(BitmapOutputAdmission.ShouldRetryWaiterRegistration(
                registrationFailed: true,
                waiterPresent: false,
                observedCapacityVersion: capacityAtFailure,
                currentCapacityVersion: afterCacheHit.CapacityVersion,
                observedWaiterSlotVersion: slotAtFailure,
                currentWaiterSlotVersion: afterCacheHit.WaiterSlotVersion,
                visibilityPromoted: false));

            Assert.True(admission.TryRegisterWaiter(
                overflow,
                16,
                isVisible: true,
                isReplacement: true,
                afterCacheHit.CapacityVersion,
                out overflowRegistration,
                out long retryCapacityVersion,
                out long retrySlotVersion));
            Assert.Equal(afterCacheHit.CapacityVersion, retryCapacityVersion);
            Assert.Equal(afterCacheHit.WaiterSlotVersion, retrySlotVersion);
            GC.KeepAlive(cacheHit);
            GC.KeepAlive(overflow);
        }
        finally
        {
            cacheHitRegistration?.Dispose();
            overflowRegistration?.Dispose();
            foreach (BitmapOutputAdmission.Reservation reservation in held)
                reservation.Dispose();
            queue.RunAll();
        }
        Assert.Equal(0, admission.GetSnapshot().WaiterCount);
    }

    [Fact]
    public void WaiterStoresOnlyWeakOwnerIdentityAndPrunesCollectedOwners()
    {
        var admission = new BitmapOutputAdmission(64, 16);
        List<BitmapOutputAdmission.Reservation> held = HoldFullBudget(admission);
        BitmapOutputAdmission.Snapshot beforeRegistration = admission.GetSnapshot();
        (WeakReference<IBitmapOutputWaiter> weak, BitmapOutputAdmission.WaiterRegistration token) =
            RegisterEphemeralWaiter(admission);

        // Do not probe TryGetTarget in the loop condition: the returned strong
        // reference may remain live in this frame across the next collection.
        for (int i = 0; i < 4; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

        Assert.False(IsWaiterAlive(weak));
        BitmapOutputAdmission.Snapshot afterPrune = admission.GetSnapshot();
        Assert.Equal(0, afterPrune.WaiterCount);
        Assert.Equal(beforeRegistration.CapacityVersion, afterPrune.CapacityVersion);
        Assert.NotEqual(beforeRegistration.WaiterSlotVersion, afterPrune.WaiterSlotVersion);
        token.Dispose();
        foreach (BitmapOutputAdmission.Reservation item in held)
            item.Dispose();
    }

    [Fact]
    public void StaleCapacityVersionRegistrationImmediatelyClosesLostWakeWindow()
    {
        var queue = new ManualNotificationQueue();
        var admission = new BitmapOutputAdmission(64, 16, testNotificationScheduler: queue.Enqueue);
        List<BitmapOutputAdmission.Reservation> held = HoldFullBudget(admission);
        long deniedVersion = admission.GetSnapshot().CapacityVersion;
        foreach (BitmapOutputAdmission.Reservation item in held)
            item.Dispose();

        var waiter = new RecordingWaiter(_ => { });
        Assert.True(admission.TryRegisterWaiter(
            waiter, 16, isVisible: true, isReplacement: false, deniedVersion, out var registration));
        Assert.NotNull(registration);
        queue.RunAll();

        Assert.Single(waiter.Versions);
        Assert.True(waiter.Versions[0] > deniedVersion);
        registration!.Dispose();
    }

    [Fact]
    public void CapacityWaveIsVisibleFirstFifoAndDoesNotWakeBeyondItsVirtualBudget()
    {
        var queue = new ManualNotificationQueue();
        var admission = new BitmapOutputAdmission(64, 16, testNotificationScheduler: queue.Enqueue);
        List<BitmapOutputAdmission.Reservation> held = HoldFullBudget(admission);
        var delivered = new List<string>();
        var background = new RecordingWaiter(_ => delivered.Add("background"));
        var visibleFirst = new RecordingWaiter(_ => delivered.Add("visible-first"));
        var visibleSecond = new RecordingWaiter(_ => delivered.Add("visible-second"));

        Assert.True(admission.TryRegisterWaiter(background, 8, false, false, 0, out var backgroundRegistration));
        Assert.True(admission.TryRegisterWaiter(visibleFirst, 8, true, false, 0, out var firstRegistration));
        Assert.True(admission.TryRegisterWaiter(visibleSecond, 8, true, true, 0, out var secondRegistration));
        held[0].Dispose();
        held[1].Dispose();
        held[2].Dispose();
        queue.RunAll();

        // At 16 bytes of actual charge, the two visible demands fit before
        // the speculative demand. The latter cannot borrow either reserve.
        Assert.Equal(new[] { "visible-first", "visible-second" }, delivered);
        Assert.Empty(background.Versions);
        GC.KeepAlive(background);
        GC.KeepAlive(visibleFirst);
        GC.KeepAlive(visibleSecond);
        backgroundRegistration!.Dispose();
        firstRegistration!.Dispose();
        secondRegistration!.Dispose();
        held[3].Dispose();
    }

    [Fact]
    public void OneTransitionHeadroomReleaseOffersOnlyOneHeadroomSizedRetry()
    {
        var queue = new ManualNotificationQueue();
        var admission = new BitmapOutputAdmission(64, 16, testNotificationScheduler: queue.Enqueue);
        List<BitmapOutputAdmission.Reservation> held = HoldFullBudget(admission);
        int callbacks = 0;
        var firstOwner = new RecordingWaiter(_ => callbacks++);
        var secondOwner = new RecordingWaiter(_ => callbacks++);
        Assert.True(admission.TryRegisterWaiter(firstOwner, 16, true, true, 0, out var first));
        Assert.True(admission.TryRegisterWaiter(secondOwner, 16, true, true, 0, out var second));

        held[0].Dispose();
        queue.RunOne();

        Assert.Equal(1, callbacks);
        first!.ReturnUnusedNotification();
        queue.RunAll();
        Assert.Equal(2, callbacks);
        GC.KeepAlive(firstOwner);
        GC.KeepAlive(secondOwner);
        second!.Dispose();
        foreach (BitmapOutputAdmission.Reservation item in held)
            item.Dispose();
    }

    [Fact]
    public void CanceledSelectedNotificationReturnsItsOfferToTheNextWaiter()
    {
        var sweepQueue = new ManualNotificationQueue();
        var deliveryQueue = new ManualNotificationQueue();
        var admission = new BitmapOutputAdmission(
            64, 16,
            testNotificationScheduler: sweepQueue.Enqueue,
            testNotificationDeliveryScheduler: deliveryQueue.Enqueue);
        List<BitmapOutputAdmission.Reservation> held = HoldFullBudget(admission);
        BitmapOutputAdmission.WaiterRegistration? canceledRegistration = null;
        BitmapOutputAdmission.WaiterRegistration? nextRegistration = null;
        bool canceledBeforeDelivery = false;
        int canceledCallbacks = 0;
        int nextCallbacks = 0;
        var canceledOwner = new RecordingWaiter(_ =>
        {
            if (!canceledBeforeDelivery)
                canceledCallbacks++;
        });
        var nextOwner = new RecordingWaiter(_ => nextCallbacks++);

        try
        {
            Assert.True(admission.TryRegisterWaiter(
                canceledOwner, 16, isVisible: true, isReplacement: true, 0,
                out canceledRegistration));
            Assert.True(admission.TryRegisterWaiter(
                nextOwner, 16, isVisible: true, isReplacement: true, 0,
                out nextRegistration));

            held[0].Dispose();
            sweepQueue.RunAll();
            Assert.Equal(16L, admission.GetSnapshot().OfferedBytes);

            // The offer has been selected but the callback is still queued.
            // Cancellation must return its virtual demand and let the next
            // waiter receive a same-capacity-version offer.
            canceledBeforeDelivery = true;
            canceledRegistration!.Dispose();

            Assert.Equal(0L, admission.GetSnapshot().OfferedBytes);
            Assert.Equal(1, admission.GetSnapshot().WaiterCount);

            sweepQueue.RunAll();
            Assert.Equal(16L, admission.GetSnapshot().OfferedBytes);
            deliveryQueue.RunAll();

            Assert.Equal(0, canceledCallbacks);
            Assert.Equal(1, nextCallbacks);
            Assert.Equal(16L, admission.GetSnapshot().OfferedBytes);
            GC.KeepAlive(canceledOwner);
            GC.KeepAlive(nextOwner);
            nextRegistration!.ReturnUnusedNotification();
            sweepQueue.RunAll();
        }
        finally
        {
            canceledBeforeDelivery = true;
            canceledRegistration?.Dispose();
            nextRegistration?.Dispose();
            foreach (BitmapOutputAdmission.Reservation item in held)
                item.Dispose();
            sweepQueue.RunAll();
            deliveryQueue.RunAll();
        }
    }

    [Fact]
    public void SuccessfulRetryReplacesVirtualOfferWithActualCharge()
    {
        var queue = new ManualNotificationQueue();
        var admission = new BitmapOutputAdmission(64, 16, testNotificationScheduler: queue.Enqueue);
        List<BitmapOutputAdmission.Reservation> held = HoldFullBudget(admission);
        var firstOwner = new RecordingWaiter(_ => { });
        var secondOwner = new RecordingWaiter(_ => { });
        Assert.True(admission.TryRegisterWaiter(firstOwner, 16, true, true, 0, out var first));
        Assert.True(admission.TryRegisterWaiter(secondOwner, 16, true, true, 0, out var second));

        held[0].Dispose(); // Actual charge is now 48 bytes.
        queue.RunOne();    // One 16-byte offer is delivered.
        Assert.Single(firstOwner.Versions);
        Assert.Empty(secondOwner.Versions);
        Assert.Equal(16L, admission.GetSnapshot().OfferedBytes);

        // A new same-version waiter cannot be notified against the first
        // owner's outstanding offer.
        var lateOwner = new RecordingWaiter(_ => { });
        Assert.True(admission.TryRegisterWaiter(lateOwner, 16, true, true, 0, out var late));
        queue.RunAll();
        Assert.Empty(lateOwner.Versions);

        Assert.True(admission.TryReserve(16, true, true, out var actual, out _));
        Assert.True(actual!.Commit());
        Assert.True(first!.CompleteRetry(capacityClaimed: true));
        Assert.Equal(0L, admission.GetSnapshot().OfferedBytes);
        queue.RunAll();
        Assert.Empty(secondOwner.Versions);
        Assert.Empty(lateOwner.Versions);

        Assert.True(actual.ReleaseAfterSuccessfulCleanup());
        queue.RunAll();
        Assert.Single(secondOwner.Versions);
        Assert.Empty(lateOwner.Versions);

        second!.ReturnUnusedNotification();
        late!.Dispose();
        queue.RunAll();
        foreach (BitmapOutputAdmission.Reservation item in held)
            item.Dispose();
    }

    [Fact]
    public void FailedRetryReturnsOfferAndPassesBoundedSameVersionWave()
    {
        var queue = new ManualNotificationQueue();
        var admission = new BitmapOutputAdmission(64, 16, testNotificationScheduler: queue.Enqueue);
        List<BitmapOutputAdmission.Reservation> held = HoldFullBudget(admission);
        var firstOwner = new RecordingWaiter(_ => { });
        var secondOwner = new RecordingWaiter(_ => { });
        Assert.True(admission.TryRegisterWaiter(firstOwner, 16, true, true, 0, out var first));
        Assert.True(admission.TryRegisterWaiter(secondOwner, 16, true, true, 0, out var second));

        held[0].Dispose();
        queue.RunOne();
        Assert.Single(firstOwner.Versions);
        Assert.True(first!.CompleteRetry(capacityClaimed: false));
        queue.RunAll();

        Assert.Single(firstOwner.Versions);
        Assert.Single(secondOwner.Versions);
        first.Dispose();
        second!.ReturnUnusedNotification();
        foreach (BitmapOutputAdmission.Reservation item in held)
            item.Dispose();
    }

    [Fact]
    public void CallbackCanReenterAdmissionAndExceptionsDoNotStrandNextWaiter()
    {
        var queue = new ManualNotificationQueue();
        var admission = new BitmapOutputAdmission(64, 16, testNotificationScheduler: queue.Enqueue);
        List<BitmapOutputAdmission.Reservation> held = HoldFullBudget(admission);
        var observed = new List<string>();
        var throwing = new RecordingWaiter(_ => throw new InvalidOperationException("fixture"));
        var following = new RecordingWaiter(_ =>
        {
            // GetSnapshot takes the admission lock. It would deadlock if this
            // callback ran under that lock.
            Assert.Equal(64, admission.GetSnapshot().CapacityBytes);
            observed.Add("following");
        });
        Assert.True(admission.TryRegisterWaiter(throwing, 16, true, true, 0, out var first));
        Assert.True(admission.TryRegisterWaiter(following, 8, true, true, 0, out var second));
        held[0].Dispose();
        queue.RunAll();

        Assert.Equal(new[] { "following" }, observed);
        Assert.Equal(1, admission.GetSnapshot().CallbackFailureCount);
        GC.KeepAlive(throwing);
        GC.KeepAlive(following);
        first!.Dispose();
        second!.Dispose();
        foreach (BitmapOutputAdmission.Reservation item in held)
            item.Dispose();
    }

    [Fact]
    public void ProductionNotificationsAreAsynchronousAndCallbackMayReserve()
    {
        var admission = new BitmapOutputAdmission(64, 16);
        Assert.True(admission.TryReserve(16, false, false, out var background1, out _));
        Assert.True(admission.TryReserve(16, false, false, out var background2, out _));
        using var callbackCompleted = new ManualResetEventSlim();
        BitmapOutputAdmission.Reservation? callbackReservation = null;
        var waiter = new RecordingWaiter(_ =>
        {
            if (admission.TryReserve(16, true, false, out BitmapOutputAdmission.Reservation? value, out _))
                callbackReservation = value;
            callbackCompleted.Set();
        });

        Assert.True(admission.TryRegisterWaiter(waiter, 16, true, false, 0, out var registration));
        Assert.True(callbackCompleted.Wait(TimeSpan.FromSeconds(5)));
        GC.KeepAlive(waiter);
        Assert.NotNull(callbackReservation);
        Assert.True(registration!.CompleteRetry(capacityClaimed: true));
        Assert.True(callbackReservation!.Commit());
        Assert.True(callbackReservation!.ReleaseAfterSuccessfulCleanup());
        background1!.Dispose();
        background2!.Dispose();
    }

    [Fact]
    public void SchedulerFailureIsContainedAndNoWaitersMeansNoQueueWork()
    {
        var failedScheduler = new BitmapOutputAdmission(
            64,
            16,
            testNotificationScheduler: _ => throw new OutOfMemoryException("fixture"));
        var waiter = new RecordingWaiter(_ => { });
        Assert.True(failedScheduler.TryRegisterWaiter(waiter, 1, true, true, 0, out var registration));
        Assert.Equal(1, failedScheduler.GetSnapshot().CallbackFailureCount);
        registration!.Dispose();

        var queue = new ManualNotificationQueue();
        var empty = new BitmapOutputAdmission(64, 16, testNotificationScheduler: queue.Enqueue);
        Assert.True(empty.TryReserve(8, true, false, out var reservation, out _));
        reservation!.Dispose();
        Assert.Equal(0, queue.Count);
    }

    private static List<BitmapOutputAdmission.Reservation> HoldFullBudget(BitmapOutputAdmission admission)
    {
        var held = new List<BitmapOutputAdmission.Reservation>(4);
        Assert.True(admission.TryReserve(16, true, false, out var first, out _));
        Assert.True(admission.TryReserve(16, true, false, out var second, out _));
        Assert.True(admission.TryReserve(16, true, false, out var third, out _));
        Assert.True(admission.TryReserve(16, true, true, out var fourth, out _));
        held.Add(first!);
        held.Add(second!);
        held.Add(third!);
        held.Add(fourth!);
        return held;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (WeakReference<IBitmapOutputWaiter>, BitmapOutputAdmission.WaiterRegistration)
        RegisterEphemeralWaiter(BitmapOutputAdmission admission)
    {
        var waiter = new RecordingWaiter(_ => { });
        var weak = new WeakReference<IBitmapOutputWaiter>(waiter);
        Assert.True(admission.TryRegisterWaiter(waiter, 16, false, false, 0, out var registration));
        return (weak, registration!);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool IsWaiterAlive(WeakReference<IBitmapOutputWaiter> weak) =>
        weak.TryGetTarget(out _);

    private sealed class RecordingWaiter(Action<long> onCapacityAvailable) : IBitmapOutputWaiter
    {
        internal List<long> Versions { get; } = new();

        public void OnBitmapOutputCapacityAvailable(long capacityVersion)
        {
            Versions.Add(capacityVersion);
            onCapacityAvailable(capacityVersion);
        }
    }

    private sealed class ManualNotificationQueue
    {
        private readonly Queue<Action> _actions = new();

        internal int Count => _actions.Count;

        internal void Enqueue(Action action) => _actions.Enqueue(action);

        internal void RunOne()
        {
            Assert.NotEmpty(_actions);
            _actions.Dequeue()();
        }

        internal void RunAll()
        {
            int guard = 0;
            while (_actions.Count > 0)
            {
                if (++guard > 1024)
                    throw new InvalidOperationException("Notification queue did not drain.");
                _actions.Dequeue()();
            }
        }
    }
}
