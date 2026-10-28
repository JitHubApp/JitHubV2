using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using MarkdownRenderer.Extensions;
using MarkdownRenderer.Layout;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

[Collection(AllocationSensitiveContractCollection.Name)]
public sealed class VectorNativeResourceAdmissionTests
{
    [Fact]
    public void ReservationIsBoundedDistinguishesSceneLimitAndRefundsExactlyOnce()
    {
        var admission = new VectorNativeResourceAdmission(capacityWeightUnits: 10, maximumSceneWeightUnits: 8);
        Assert.Equal(VectorNativeAdmissionResult.Granted,
            admission.TryReserve(7, out VectorNativeResourceCredit? first, out long initialGeneration));
        Assert.NotNull(first);
        Assert.Equal(0, initialGeneration);
        Assert.Equal(7, admission.ReservedWeightUnits);

        Assert.Equal(VectorNativeAdmissionResult.DeferredCapacity,
            admission.TryReserve(4, out VectorNativeResourceCredit? deniedByCapacity, out long unchangedGeneration));
        Assert.Null(deniedByCapacity);
        Assert.Equal(initialGeneration, unchangedGeneration);
        Assert.Equal(VectorNativeAdmissionResult.TooLarge,
            admission.TryReserve(9, out VectorNativeResourceCredit? tooLarge, out _));
        Assert.Null(tooLarge);

        first!.Dispose();
        first.Dispose();
        Assert.Equal(0, admission.ReservedWeightUnits);
        Assert.Equal(1, admission.CapacityGeneration);
        Assert.Equal(VectorNativeAdmissionResult.Granted,
            admission.TryReserve(8, out VectorNativeResourceCredit? fullCapacity, out _));
        Assert.Equal(8, admission.ReservedWeightUnits);
        fullCapacity!.Dispose();
        Assert.Equal(0, admission.ReservedWeightUnits);
    }

    [Fact]
    public async Task CompetingReservationsNeverExceedAggregateCapacity()
    {
        const int capacity = 100;
        const int contenderCount = 40;
        var admission = new VectorNativeResourceAdmission(capacity, capacity);
        using var start = new ManualResetEventSlim();
        var credits = new ConcurrentBag<VectorNativeResourceCredit>();
        var results = new ConcurrentBag<VectorNativeAdmissionResult>();

        Task[] contenders = Enumerable.Range(0, contenderCount)
            .Select(contender => Task.Run(() =>
            {
                _ = contender;
                start.Wait();
                VectorNativeAdmissionResult result = admission.TryReserve(
                    4, out VectorNativeResourceCredit? credit, out long observedGeneration);
                _ = observedGeneration;
                results.Add(result);
                if (credit is not null)
                    credits.Add(credit);
                Assert.InRange(admission.ReservedWeightUnits, 0, capacity);
            }))
            .ToArray();

        start.Set();
        await Task.WhenAll(contenders);

        Assert.Equal(capacity, admission.ReservedWeightUnits);
        Assert.Equal(capacity / 4, results.Count(static result => result == VectorNativeAdmissionResult.Granted));
        Assert.Equal(contenderCount - capacity / 4,
            results.Count(static result => result == VectorNativeAdmissionResult.DeferredCapacity));
        foreach (VectorNativeResourceCredit credit in credits)
            credit.Dispose();
        Assert.Equal(0, admission.ReservedWeightUnits);
    }

    [Fact]
    public void WeakCapacityObserversAreCalledOutsideLedgerGateAndFaultsAreIsolated()
    {
        var admission = new VectorNativeResourceAdmission(1, 1);
        Assert.Equal(VectorNativeAdmissionResult.Granted,
            admission.TryReserve(1, out VectorNativeResourceCredit? held, out _));

        var holder = new CreditHolder();
        bool callbackCouldAcquireFromAnotherThread = false;
        long callbackGeneration = -1;
        var observer = new DelegateObserver(generation =>
        {
            callbackGeneration = generation;
            Task<bool> probe = Task.Run(() =>
            {
                VectorNativeAdmissionResult result = admission.TryReserve(1, out VectorNativeResourceCredit? credit, out _);
                holder.Credit = credit;
                return result == VectorNativeAdmissionResult.Granted;
            });
            callbackCouldAcquireFromAnotherThread = probe.Wait(TimeSpan.FromSeconds(2)) && probe.Result;
        });
        using IDisposable registration = admission.RegisterCapacityObserver(observer);
        using IDisposable throwingRegistration = admission.RegisterCapacityObserver(
            new DelegateObserver(_ => throw new InvalidOperationException("observer fault")));

        held!.Dispose();

        Assert.True(callbackCouldAcquireFromAnotherThread);
        Assert.Equal(admission.CapacityGeneration, callbackGeneration);
        Assert.Equal(1, admission.ReservedWeightUnits);
        throwingRegistration.Dispose();
        registration.Dispose();
        holder.Credit!.Dispose();
        Assert.Equal(0, admission.ReservedWeightUnits);
        GC.KeepAlive(observer);
    }

    [Fact]
    public void RegisterThenRecheckDetectsCapacityReturnedBeforeRegistration()
    {
        var admission = new VectorNativeResourceAdmission(4, 4);
        Assert.Equal(VectorNativeAdmissionResult.Granted,
            admission.TryReserve(4, out VectorNativeResourceCredit? held, out long before));

        held!.Dispose();
        bool callbackObserved = false;
        using IDisposable registration = admission.RegisterCapacityObserver(new DelegateObserver(_ => callbackObserved = true));

        // A release in the capture/register gap has no observer to notify. The
        // control's post-registration generation comparison is the wake path.
        Assert.False(callbackObserved);
        Assert.True(admission.CapacityGeneration > before);
        Assert.Equal(0, admission.ReservedWeightUnits);
    }

    [Fact]
    public void ExistingStationaryObserverSurvivesRegistrationChainCompaction()
    {
        var admission = new VectorNativeResourceAdmission(1, 1);
        Assert.Equal(VectorNativeAdmissionResult.Granted,
            admission.TryReserve(1, out VectorNativeResourceCredit? held, out _));

        int wakeCount = 0;
        var keeperObserver = new DelegateObserver(_ => wakeCount++);
        using IDisposable keeper = admission.RegisterCapacityObserver(keeperObserver);
        for (int i = 0; i < 300; i++)
        {
            using IDisposable transient = admission.RegisterCapacityObserver(new DelegateObserver(static _ => { }));
        }

        // The retained waiter is deliberately not re-registering or polling.
        // Churn forces immutable-chain compaction before its only capacity wake.
        held!.Dispose();

        Assert.Equal(1, wakeCount);
        Assert.Equal(0, admission.ReservedWeightUnits);
        GC.KeepAlive(keeperObserver);
    }

    [Fact]
    public void CapacityDenialAndTooLargeAreTypedAndDoNotTouchCanvasFactory()
    {
        MarkdownVectorScene scene = PathScene();
        long weight = VectorSceneDrawing.EstimateNativeResourceWeightUnits(scene, CancellationToken.None);
        Assert.True(weight > 1);

        var capacity = new VectorNativeResourceAdmission(weight, weight);
        Assert.Equal(VectorNativeAdmissionResult.Granted,
            capacity.TryReserve(weight, out VectorNativeResourceCredit? occupyingCredit, out _));
        using (var deferredDrawing = new VectorSceneDrawing(
                   new ForbiddenResourceCreator(),
                   scene,
                   CancellationToken.None,
                   deferNativeResources: true,
                   nativeAdmission: capacity))
        {
            Assert.False(deferredDrawing.TryPrepareNativeResources(CancellationToken.None, out VectorNativePreparationOutcome deferred));
            Assert.Equal(VectorNativePreparationStatus.DeferredCapacity, deferred.Status);
            Assert.Equal(weight, deferred.WeightUnits);
            Assert.Equal(capacity.CapacityGeneration, deferred.CapacityGeneration);
            Assert.Equal(VectorNativePreparationStatus.DeferredCapacity, deferredDrawing.PreparationStatus);
            Assert.Equal(weight, capacity.ReservedWeightUnits);
        }
        occupyingCredit!.Dispose();

        var sceneLimit = new VectorNativeResourceAdmission(weight, weight - 1);
        using var oversizedDrawing = new VectorSceneDrawing(
            new ForbiddenResourceCreator(),
            scene,
            CancellationToken.None,
            deferNativeResources: true,
            nativeAdmission: sceneLimit);
        Assert.False(oversizedDrawing.TryPrepareNativeResources(CancellationToken.None, out VectorNativePreparationOutcome tooLarge));
        Assert.Equal(VectorNativePreparationStatus.TooLarge, tooLarge.Status);
        Assert.Equal(0, sceneLimit.ReservedWeightUnits);
        Assert.Same(sceneLimit, oversizedDrawing.NativeAdmission);

        Assert.Throws<ArgumentException>(() => new VectorSceneDrawing(
            new ForbiddenResourceCreator(),
            scene,
            CancellationToken.None,
            nativeAdmission: sceneLimit));
    }

    [Fact]
    public void SceneWeightIncludesPathsTextStrokeDashAndNestingWithSaturatedArithmeticInputs()
    {
        MarkdownVectorScene simple = new(64, 48, 36,
        [
            MarkdownVectorCommand.FillRectangle(new MarkdownVectorRectangle(0, 0, 8, 8), 0xFF000000),
        ]);
        MarkdownVectorPaintStyle dashedStroke = new(
            strokeArgb: 0xFF000000,
            strokeWidth: 1,
            lineCap: MarkdownVectorLineCap.Round,
            lineJoin: MarkdownVectorLineJoin.Bevel,
            dashArray: new[] { 2f, 1f, 3f, 1f });
        MarkdownVectorScene complex = new(64, 48, 36,
        [
            MarkdownVectorCommand.BeginGroup(MarkdownVectorTransform.Identity),
            MarkdownVectorCommand.BeginClip(Path(0)),
            MarkdownVectorCommand.DrawPath(Path(0), dashedStroke),
            MarkdownVectorCommand.DrawText(
                new string('x', 4096),
                new MarkdownVectorPoint(4, 24),
                new MarkdownVectorTextStyle("Segoe UI", 12),
                dashedStroke),
            MarkdownVectorCommand.EndClip(),
            MarkdownVectorCommand.EndGroup(),
        ]);

        long simpleWeight = VectorSceneDrawing.EstimateNativeResourceWeightUnits(simple, CancellationToken.None);
        long complexWeight = VectorSceneDrawing.EstimateNativeResourceWeightUnits(complex, CancellationToken.None);

        Assert.True(simpleWeight > 0);
        Assert.True(complexWeight > simpleWeight);
        Assert.True(complexWeight >= 4096L * 64);
    }

    [Fact]
    public void ReversibleReleaseRefundsOnlyAfterEveryNativeSlotCloses()
    {
        using var device = new CanvasDevice(forceSoftwareRenderer: false);
        var retirement = new NativeResourceRetirement(createTimer: false);
        var faultPlan = new GeometryDisposeFaultPlan(failures: 1);
        MarkdownVectorScene scene = PathScene();
        long weight = VectorSceneDrawing.EstimateNativeResourceWeightUnits(scene, CancellationToken.None);
        var admission = new VectorNativeResourceAdmission(weight, weight);
        using var drawing = new VectorSceneDrawing(
            device,
            scene,
            CancellationToken.None,
            deferNativeResources: true,
            nativeRetirement: retirement,
            nativeDisposer: faultPlan.Dispose,
            nativeAdmission: admission);

        Assert.True(drawing.TryPrepareNativeResources(CancellationToken.None, out _));
        Assert.Equal(weight, admission.ReservedWeightUnits);
        Assert.ThrowsAny<Exception>(() => drawing.ReleaseNativeResources());
        Assert.Equal(weight, admission.ReservedWeightUnits);
        Assert.False(drawing.IsPrepared);

        drawing.ReleaseNativeResources();
        Assert.Equal(0, admission.ReservedWeightUnits);
        Assert.Equal(VectorNativePreparationStatus.NotPrepared, drawing.PreparationStatus);
    }

    [Fact]
    public void CancellationBeforeCachedShapeReprepareDoesNotReserveOrReturnCapacityFallback()
    {
        using var device = new CanvasDevice(forceSoftwareRenderer: false);
        var retirement = new NativeResourceRetirement(createTimer: false);
        MarkdownVectorScene scene = PathScene();
        long weight = VectorSceneDrawing.EstimateNativeResourceWeightUnits(scene, CancellationToken.None);
        var admission = new VectorNativeResourceAdmission(weight, weight);
        using var drawing = new VectorSceneDrawing(
            device,
            scene,
            CancellationToken.None,
            deferNativeResources: true,
            nativeRetirement: retirement,
            nativeAdmission: admission);

        Assert.True(drawing.TryPrepareNativeResources(CancellationToken.None, out _));
        drawing.ReleaseNativeResources();
        Assert.Equal(0, admission.ReservedWeightUnits);

        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() =>
            drawing.TryPrepareNativeResources(canceled.Token, out _));
        Assert.Equal(VectorNativePreparationStatus.NotPrepared, drawing.PreparationStatus);
        Assert.Equal(0, admission.ReservedWeightUnits);
    }

    [Fact]
    public void TerminalRetirementKeepsCreditUntilDurableOwnerCompletes()
    {
        using var device = new CanvasDevice(forceSoftwareRenderer: false);
        var retirement = new NativeResourceRetirement(createTimer: false);
        var faultPlan = new GeometryDisposeFaultPlan(failures: 1);
        MarkdownVectorScene scene = PathScene();
        long weight = VectorSceneDrawing.EstimateNativeResourceWeightUnits(scene, CancellationToken.None);
        var admission = new VectorNativeResourceAdmission(weight, weight);
        var drawing = new VectorSceneDrawing(
            device,
            scene,
            CancellationToken.None,
            deferNativeResources: true,
            nativeRetirement: retirement,
            nativeDisposer: faultPlan.Dispose,
            nativeAdmission: admission);

        Assert.True(drawing.TryPrepareNativeResources(CancellationToken.None, out _));
        Assert.ThrowsAny<Exception>(() => drawing.Dispose());
        Assert.Equal(weight, admission.ReservedWeightUnits);
        Assert.Equal(1, retirement.PendingRecordCount);

        Assert.True(retirement.DrainOnce());
        Assert.Equal(0, retirement.PendingRecordCount);
        Assert.Equal(0, admission.ReservedWeightUnits);
        Assert.Equal(1, admission.CapacityGeneration);
        drawing.Dispose(); // Terminal disposal remains idempotent.
    }

    [Fact]
    public void UncertainClipQuarantineRemainsChargedAndCannotWakeCapacityWaiters()
    {
        using var device = new CanvasDevice(forceSoftwareRenderer: true);
        using var target = new CanvasRenderTarget(device, 24, 24, 96);
        using CanvasDrawingSession session = target.CreateDrawingSession();
        var retirement = new NativeResourceRetirement(createTimer: false);
        MarkdownVectorScene scene = new(24, 24, 24,
        [
            MarkdownVectorCommand.BeginClip(Path(0)),
            MarkdownVectorCommand.FillRectangle(new MarkdownVectorRectangle(0, 0, 24, 24), 0xFFFF0000),
            MarkdownVectorCommand.EndClip(),
        ]);
        long weight = VectorSceneDrawing.EstimateNativeResourceWeightUnits(scene, CancellationToken.None);
        var admission = new VectorNativeResourceAdmission(weight, weight);
        int capacityWakeCount = 0;
        var observer = new DelegateObserver(_ => Interlocked.Increment(ref capacityWakeCount));
        using IDisposable registration = admission.RegisterCapacityObserver(observer);
        int activeLayerDisposeAttempts = 0;
        var drawing = new VectorSceneDrawing(
            device,
            scene,
            CancellationToken.None,
            deferNativeResources: true,
            nativeRetirement: retirement,
            nativeDisposer: resource =>
            {
                if (resource is CanvasActiveLayer)
                {
                    Interlocked.Increment(ref activeLayerDisposeAttempts);
                    throw new InvalidOperationException("Injected uncertain active clip pop.");
                }
                resource.Dispose();
            },
            nativeAdmission: admission);
        drawing.SetClipFactoryForTest(static (drawingSession, geometry) =>
            drawingSession.CreateLayer(1f, geometry));

        Assert.True(drawing.TryPrepareNativeResources(CancellationToken.None, out _));
        Assert.Equal(weight, admission.ReservedWeightUnits);
        FieldInfo clipStackField = typeof(VectorSceneDrawing)
            .GetField("_clipStack", BindingFlags.Instance | BindingFlags.NonPublic)!;
        FieldInfo geometryField = typeof(VectorSceneDrawing)
            .GetField("_geometries", BindingFlags.Instance | BindingFlags.NonPublic)!;

        Assert.Throws<InvalidOperationException>(() => drawing.Paint(
            session,
            new Windows.Foundation.Rect(0, 0, 24, 24),
            new VectorScenePaintPalette(
                Windows.UI.Color.FromArgb(255, 0, 0, 0),
                Windows.UI.Color.FromArgb(255, 255, 255, 255),
                Windows.UI.Color.FromArgb(255, 0, 0, 255)),
            highContrast: false));
        Assert.Equal(1, activeLayerDisposeAttempts);

        var clipSlots = Assert.IsType<CanvasActiveLayer?[]>(clipStackField.GetValue(drawing));
        CanvasActiveLayer clipLayer = Assert.IsType<CanvasActiveLayer>(clipSlots[0]);
        var geometrySlots = Assert.IsType<CanvasGeometry?[]>(geometryField.GetValue(drawing));
        CanvasGeometry clipGeometry = Assert.IsType<CanvasGeometry>(geometrySlots[0]);
        try
        {
            Assert.Throws<InvalidOperationException>(() => drawing.Dispose());

            // Uncertain session-bound layers and their clip geometry are
            // quarantined, never retried after the paint session returns.
            Assert.Equal(2, retirement.QuarantinedResourceCount);
            Assert.Equal(0, retirement.PendingRecordCount);
            Assert.False(retirement.DrainOnce());
            Assert.Equal(weight, admission.ReservedWeightUnits);
            Assert.Equal(0, admission.CapacityGeneration);
            Assert.Equal(0, capacityWakeCount);
            Assert.Equal(1, activeLayerDisposeAttempts);
            GC.KeepAlive(observer);
        }
        finally
        {
            // This injected fault throws before the real PopLayer. The test
            // knows the session is still live and closes the borrowed native
            // objects manually; production correctly makes no such guess.
            clipLayer.Dispose();
            clipGeometry.Dispose();
        }
    }

    [Fact]
    public void LegacyDrawingWithoutAdmissionRemainsEagerAndOutsideLedger()
    {
        using var device = new CanvasDevice(forceSoftwareRenderer: false);
        using var drawing = new VectorSceneDrawing(device, PathScene(), CancellationToken.None);

        Assert.True(drawing.IsPrepared);
        Assert.Equal(VectorNativePreparationStatus.Prepared, drawing.PreparationStatus);
        Assert.Null(drawing.NativeAdmission);
        Assert.Equal(0, drawing.NativeResourceAdmission?.ReservedWeightUnits ?? 0);
    }

    [Fact]
    public void CapacityObserversAreWeaklyReferenced()
    {
        var admission = new VectorNativeResourceAdmission(2, 2);
        IDisposable registration = RegisterEphemeralObserver(admission, out WeakReference observerReference);
        for (int attempt = 0; attempt < 4 && observerReference.IsAlive; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

        Assert.False(observerReference.IsAlive);
        registration.Dispose();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static IDisposable RegisterEphemeralObserver(
        VectorNativeResourceAdmission admission,
        out WeakReference observerReference)
    {
        var observer = new DelegateObserver(static _ => { });
        observerReference = new WeakReference(observer);
        return admission.RegisterCapacityObserver(observer);
    }

    private static MarkdownVectorScene PathScene() => new(
        40,
        40,
        36,
        [MarkdownVectorCommand.FillPath(Path(0), 0xFF315A88)]);

    private static MarkdownVectorPathOperation[] Path(float offset) =>
    [
        MarkdownVectorPathOperation.MoveTo(new MarkdownVectorPoint(offset, 0)),
        MarkdownVectorPathOperation.LineTo(new MarkdownVectorPoint(offset + 12, 2)),
        MarkdownVectorPathOperation.CubicBezierTo(
            new MarkdownVectorPoint(offset + 18, 8),
            new MarkdownVectorPoint(offset + 4, 12),
            new MarkdownVectorPoint(offset, 20)),
        MarkdownVectorPathOperation.Close(),
    ];

    private sealed class ForbiddenResourceCreator : ICanvasResourceCreator
    {
        public CanvasDevice Device => throw new InvalidOperationException("Capacity rejection reached a native factory.");
    }

    private sealed class DelegateObserver(Action<long> callback) : IVectorNativeCapacityObserver
    {
        public void OnCapacityAvailable(long generation) => callback(generation);
    }

    private sealed class CreditHolder
    {
        internal VectorNativeResourceCredit? Credit;
    }

    private sealed class GeometryDisposeFaultPlan(int failures)
    {
        private int _remainingFailures = failures;

        internal void Dispose(IDisposable resource)
        {
            if (resource is CanvasGeometry && Interlocked.Decrement(ref _remainingFailures) >= 0)
                throw new InvalidOperationException("Injected transient vector geometry dispose failure.");
            resource.Dispose();
        }
    }
}
