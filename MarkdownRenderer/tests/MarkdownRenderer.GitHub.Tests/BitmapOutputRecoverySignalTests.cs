using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MarkdownRenderer.Images;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Performance;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

[Collection(SharedCanvasBitmapCacheTestCollection.Name)]
public sealed class BitmapOutputRecoverySignalTests
{
    [Fact]
    public async Task SharedWaiterOverflowEmitsOneSourceFreeSignalAndRecoveryDoesNotStartLoading()
    {
        BitmapOutputAdmission admission = BitmapOutputAdmission.Shared;
        SharedCanvasBitmapCache.Clear();
        await DisplaySizedRasterDecodeTests.WaitUntilAsync(() =>
        {
            BitmapOutputAdmission.Snapshot snapshot = admission.GetSnapshot();
            return snapshot.ChargedBytes == 0 && snapshot.WaiterCount == 0;
        });

        var reservations = new List<BitmapOutputAdmission.Reservation>();
        var waiterOwners = new List<PassiveWaiter>();
        var waiterRegistrations = new List<BitmapOutputAdmission.WaiterRegistration>();
        ImageBox? image = null;
        EventHandler<LoadCompletedEventArgs>? handler = null;
        using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        var resolver = new RecoverySignalResolver(DisplaySizedRasterDecodeTests.CreatePng(2, 1));
        try
        {
            while (admission.GetSnapshot().ChargedBytes < admission.CapacityBytes)
            {
                BitmapOutputAdmission.Snapshot snapshot = admission.GetSnapshot();
                long remaining = snapshot.CapacityBytes - snapshot.ChargedBytes;
                long amount = System.Math.Min(remaining, snapshot.MaximumSingleOutputBytes);
                Assert.True(admission.TryReserve(
                    amount,
                    isVisible: true,
                    isReplacement: true,
                    out BitmapOutputAdmission.Reservation? reservation,
                    out _));
                reservations.Add(Assert.IsType<BitmapOutputAdmission.Reservation>(reservation));
            }

            long capacityVersion = admission.GetSnapshot().CapacityVersion;
            for (int index = 0; index < 248; index++)
            {
                var owner = new PassiveWaiter();
                Assert.True(admission.TryRegisterWaiter(
                    owner,
                    requiredBytes: 1,
                    isVisible: false,
                    isReplacement: false,
                    capacityVersion,
                    out BitmapOutputAdmission.WaiterRegistration? registration));
                waiterOwners.Add(owner);
                waiterRegistrations.Add(Assert.IsType<BitmapOutputAdmission.WaiterRegistration>(registration));
            }

            // The shared admission queue reserves eight of its unchanged 256
            // slots for visible work. Fill those too so the image exercises
            // true total-cap overflow, not the speculative quota boundary.
            for (int index = 0; index < 8; index++)
            {
                var owner = new PassiveWaiter();
                Assert.True(admission.TryRegisterWaiter(
                    owner,
                    requiredBytes: 1,
                    isVisible: true,
                    isReplacement: false,
                    capacityVersion,
                    out BitmapOutputAdmission.WaiterRegistration? registration));
                waiterOwners.Add(owner);
                waiterRegistrations.Add(Assert.IsType<BitmapOutputAdmission.WaiterRegistration>(registration));
            }
            Assert.Equal(256, admission.GetSnapshot().WaiterCount);
            GC.KeepAlive(waiterOwners);

            image = new ImageBox(
                DisplaySizedRasterDecodeTests.CreateContext(resolver, session, scale: 1),
                "https://images.example/waiter-overflow.png",
                "waiter overflow");
            var signal = new TaskCompletionSource<LoadCompletedEventArgs>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            int notificationCount = 0;
            handler = (_, args) =>
            {
                Interlocked.Increment(ref notificationCount);
                if (args.IsBitmapOutputRecoverySignal)
                    signal.TrySetResult(args);
            };
            image.LoadCompleted += handler;

            image.Measure(2);
            image.EnsureLoading();
            LoadCompletedEventArgs received = await signal.Task.WaitAsync(TimeSpan.FromSeconds(30));
            await DisplaySizedRasterDecodeTests.WaitUntilAsync(() =>
                image.ActiveImageWorkCountForTests == 0 && image.NeedsBitmapOutputRecovery);

            Assert.True(received.IsBitmapOutputRecoverySignal);
            Assert.False(received.LayoutInvalidated);
            Assert.Null(image.Context.Dispatcher);
            Assert.Null(image.Bitmap);
            Assert.Equal(1, resolver.ResolveCount);

            // Repeated timer-style observations at unchanged capacity and slot
            // generations stay dormant: the signal does not call EnsureLoading.
            for (int index = 0; index < 20; index++)
                Assert.True(image.ObserveBitmapOutputRecovery(isVisible: false));
            await Task.Delay(50);
            Assert.Equal(1, notificationCount);
            Assert.Equal(1, resolver.ResolveCount);
            Assert.Equal(0, image.ActiveImageWorkCountForTests);

            // A released queue slot is observed without releasing any bitmap
            // charge. Recovery joins the bounded governor queue but still does
            // not start a second source read until the governor grants a retry.
            waiterRegistrations[0].Dispose();
            Assert.True(image.ObserveBitmapOutputRecovery(isVisible: false));
            await DisplaySizedRasterDecodeTests.WaitUntilAsync(() => !image.NeedsBitmapOutputRecovery);
            Assert.Equal(admission.CapacityBytes, admission.GetSnapshot().ChargedBytes);
            Assert.Equal(256, admission.GetSnapshot().WaiterCount);
            GC.KeepAlive(waiterOwners);
            Assert.Equal(1, resolver.ResolveCount);
            Assert.Equal(1, notificationCount);
            Assert.Equal(0, image.ActiveImageWorkCountForTests);
        }
        finally
        {
            if (image is not null)
            {
                if (handler is not null)
                    image.LoadCompleted -= handler;
                image.Dispose();
                await image.RetirementTask.WaitAsync(TimeSpan.FromSeconds(15));
            }

            foreach (BitmapOutputAdmission.WaiterRegistration registration in waiterRegistrations)
                registration.Dispose();
            foreach (BitmapOutputAdmission.Reservation reservation in reservations)
                reservation.Dispose();
            SharedCanvasBitmapCache.Clear();
            GC.KeepAlive(waiterOwners);
        }

        await DisplaySizedRasterDecodeTests.WaitUntilAsync(() =>
        {
            BitmapOutputAdmission.Snapshot snapshot = admission.GetSnapshot();
            return snapshot.ChargedBytes == 0 && snapshot.WaiterCount == 0;
        });
    }

    private sealed class RecoverySignalResolver(byte[] bytes) : IMarkdownImageResolver
    {
        private int _resolveCount;
        internal int ResolveCount => Volatile.Read(ref _resolveCount);

        public ValueTask<MarkdownImageResolution> ResolveAsync(
            string source,
            MarkdownImageResolveContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _resolveCount);
            return ValueTask.FromResult(MarkdownImageResolution.Resolved(
                new MarkdownImageAsset(bytes, "image/png", CacheKey: "recovery-signal-test")));
        }
    }

    private sealed class PassiveWaiter : IBitmapOutputWaiter
    {
        public void OnBitmapOutputCapacityAvailable(long capacityVersion)
        {
        }
    }
}
