using System.Runtime.CompilerServices;
using System.Threading;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using MarkdownRenderer.Extensions;
using MarkdownRenderer.Layout;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>
/// Exercises durable native-slot custody after the scene/drawing owner is gone.
/// The injected disposer records only weak resource references and scalar state;
/// it deliberately never captures a scene or Canvas resource creator.
/// </summary>
[Collection(AllocationSensitiveContractCollection.Name)]
public sealed class VectorNativeDurableRetirementTests
{
    [Fact]
    public void RetryQueueServicesAtMostEightRecordsPerPassAndRotatesFailuresFairly()
    {
        var retirement = new NativeResourceRetirement(createTimer: false);
        var resources = Enumerable.Range(0, 9)
            .Select(_ => new ProbeResource { FailDisposal = true })
            .ToArray();

        foreach (ProbeResource resource in resources)
        {
            NativeResourceRetirementRecord record = retirement.Reserve(
                resourceGroupCount: 1,
                temporarySlotCount: 0,
                disposer: static value => value.Dispose());
            record.SetGroup(0, [resource]);
            record.Publish();
        }

        Assert.False(retirement.DrainOnce());
        Assert.Equal(9, retirement.PendingRecordCount);
        Assert.Equal(9, retirement.PendingResourceCount);
        Assert.All(resources.Take(8), resource => Assert.Equal(1, resource.DisposeCount));
        Assert.Equal(0, resources[^1].DisposeCount);

        // The unvisited ninth record moves to the head after the first quantum.
        // It must be attempted before the previously failing records are retried.
        foreach (ProbeResource resource in resources)
            resource.FailDisposal = false;

        Assert.True(retirement.DrainOnce());
        Assert.Equal(1, retirement.PendingRecordCount);
        Assert.Equal(1, retirement.PendingResourceCount);
        Assert.Equal(1, resources[^1].DisposeCount);

        Assert.True(retirement.DrainOnce());
        Assert.Equal(0, retirement.PendingRecordCount);
        Assert.Equal(0, retirement.PendingResourceCount);
        for (int index = 0; index < resources.Length; index++)
            Assert.Equal(index == resources.Length - 1 ? 1 : 2, resources[index].DisposeCount);
    }

    [Fact]
    public void RetryQueueVisitsAtMost128SlotsAndDuplicatePublishIsIdempotent()
    {
        var retirement = new NativeResourceRetirement(createTimer: false);
        ProbeResource[] resources = Enumerable.Range(0, 129)
            .Select(_ => new ProbeResource())
            .ToArray();
        NativeResourceRetirementRecord record = retirement.Reserve(
            resourceGroupCount: 1,
            temporarySlotCount: 0,
            disposer: static value => value.Dispose());
        var ownedResources = new IDisposable?[resources.Length];
        Array.Copy(resources, ownedResources, resources.Length);
        record.SetGroup(0, ownedResources);
        record.Publish();
        record.Publish();

        Assert.Equal(1, retirement.PendingRecordCount);
        Assert.Equal(129, retirement.PendingResourceCount);
        Assert.True(retirement.DrainOnce());
        Assert.Equal(1, retirement.PendingRecordCount);
        Assert.Equal(1, retirement.PendingResourceCount);
        Assert.All(resources.Take(128), resource => Assert.Equal(1, resource.DisposeCount));
        Assert.Equal(0, resources[^1].DisposeCount);

        Assert.True(retirement.DrainOnce());
        Assert.Equal(0, retirement.PendingRecordCount);
        Assert.Equal(0, retirement.PendingResourceCount);
        Assert.All(resources, resource => Assert.Equal(1, resource.DisposeCount));
    }

    [Fact]
    public void MixedRetirementCompactsNullsQuarantinesNonretryableSlotsAndReleasesCompletedSlots()
    {
        var retirement = new NativeResourceRetirement(createTimer: false);
        WeakReference<ProbeResource> retryable = PublishMixedRetryAndQuarantineRecord(
            retirement,
            out WeakReference<ProbeResource> quarantined,
            out var disposer,
            out long compactResourceCount);

        // Only the non-null retryable resource is scheduled. The non-retryable
        // resource is retained in custody, but is never a background Dispose
        // candidate; duplicate publication does not change either count.
        Assert.Equal(2, compactResourceCount);
        Assert.Equal(1, retirement.PendingRecordCount);
        Assert.Equal(1, retirement.PendingResourceCount);
        Assert.Equal(1, retirement.QuarantinedResourceCount);

        Assert.False(retirement.DrainOnce());
        Assert.Equal(1, disposer.AttemptCount);
        Assert.Equal(0, disposer.SuccessfulDisposalCount);
        Assert.Equal(1, retirement.PendingRecordCount);
        Assert.Equal(1, retirement.PendingResourceCount);
        Assert.Equal(1, retirement.QuarantinedResourceCount);
        AssertQuarantinedResourceWasNeverDisposed(quarantined);

        disposer.AllowDisposal();
        Assert.True(retirement.DrainOnce());
        Assert.Equal(2, disposer.AttemptCount);
        Assert.Equal(1, disposer.SuccessfulDisposalCount);
        Assert.Equal(0, retirement.PendingRecordCount);
        Assert.Equal(0, retirement.PendingResourceCount);
        Assert.Equal(1, retirement.QuarantinedResourceCount);
        AssertQuarantinedResourceWasNeverDisposed(quarantined);

        CollectGarbage();
        AssertCollected(retryable, "A successfully released slot remained rooted by retirement custody.");
        AssertRetained(quarantined, "The non-retryable slot was not retained in quarantine custody.");
        GC.KeepAlive(retirement);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference<ProbeResource> PublishMixedRetryAndQuarantineRecord(
        NativeResourceRetirement retirement,
        out WeakReference<ProbeResource> quarantinedReference,
        out ControlledRetryDisposer disposer,
        out long compactResourceCount)
    {
        var retryable = new ProbeResource();
        var quarantined = new ProbeResource();
        var retryableReference = new WeakReference<ProbeResource>(retryable);
        quarantinedReference = new WeakReference<ProbeResource>(quarantined);
        disposer = new ControlledRetryDisposer();

        NativeResourceRetirementRecord record = retirement.Reserve(
            resourceGroupCount: 2,
            temporarySlotCount: 2,
            disposer: disposer.Dispose);
        record.SetGroup(0, [null, retryable, null]);
        record.SetGroup(1, [null, quarantined, null], retryable: false);
        record.SetTemporary(0, null);
        record.SetTemporary(1, null);
        record.Publish();
        compactResourceCount = record.RemainingResourceCount;
        record.Publish();

        var rejectedMutation = new ProbeResource();
        Assert.Throws<ObjectDisposedException>(() => record.SetGroup(0, [rejectedMutation]));
        Assert.Throws<ObjectDisposedException>(() => record.SetTemporary(0, rejectedMutation));
        Assert.Equal(0, rejectedMutation.DisposeCount);
        Assert.Equal(1, retirement.PendingRecordCount);
        Assert.Equal(1, retirement.PendingResourceCount);
        Assert.Equal(1, retirement.QuarantinedResourceCount);

        return retryableReference;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void AssertQuarantinedResourceWasNeverDisposed(WeakReference<ProbeResource> resourceReference)
    {
        Assert.True(resourceReference.TryGetTarget(out ProbeResource? resource));
        Assert.Equal(0, resource!.DisposeCount);
    }

    private static void AssertCollected<T>(WeakReference<T> reference, string? message = null)
        where T : class =>
        Assert.False(IsAlive(reference), message);

    private static void AssertRetained<T>(WeakReference<T> reference, string? message = null)
        where T : class =>
        Assert.True(IsAlive(reference), message);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool IsAlive<T>(WeakReference<T> reference)
        where T : class =>
        reference.TryGetTarget(out _);

    [Fact]
    public async Task RetryQueueAllowsOnlyOneNativeDrainAtATime()
    {
        var retirement = new NativeResourceRetirement(createTimer: false);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var resource = new ProbeResource();
        NativeResourceRetirementRecord record = retirement.Reserve(
            resourceGroupCount: 1,
            temporarySlotCount: 0,
            disposer: value =>
            {
                entered.Set();
                if (!release.Wait(TimeSpan.FromSeconds(10)))
                    throw new TimeoutException("The native-disposal gate was not released.");
                value.Dispose();
            });
        record.SetGroup(0, [resource]);
        record.Publish();

        Task<bool> firstDrain = Task.Run(retirement.DrainOnce);
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            Assert.False(retirement.DrainOnce());
        }
        finally
        {
            release.Set();
        }

        Assert.True(await firstDrain.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(1, resource.DisposeCount);
        Assert.Equal(0, retirement.PendingRecordCount);
        Assert.Equal(0, retirement.PendingResourceCount);
    }

    [Fact]
    public void TerminalFailureRetainsOnlyFailedGeometryWhileSceneCreatorAndSiblingCollect()
    {
        NativeRetirementEvidence evidence = RunFaultedNativeRetirement<CanvasGeometry>(CreateTwoPathScene);
        Assert.Equal(0, evidence.Owner.PendingRecordCount);
        CollectGarbage();
        AssertCollected(evidence.FailedResource);
        Assert.All(evidence.ReleasedResources, resource => AssertCollected(resource));
        AssertCollected(evidence.Scene);
        AssertCollected(evidence.Creator);
        GC.KeepAlive(evidence.Owner);
    }

    [Fact]
    public void EagerConstructorFailureTransfersPartialPrefixWithoutReturningDrawing()
    {
        var retirement = new NativeResourceRetirement(createTimer: false);
        var disposer = new NativeDisposalProbe();
        disposer.FailPersistentlyOnFirst<CanvasGeometry>();

        WeakReference<MarkdownVectorScene> scene = CreateEagerPartialPrefixFailure(
            retirement, disposer, out WeakReference<FailAtDeviceRequestCreator> creator);

        Assert.Equal(1, retirement.PendingRecordCount);
        Assert.True(retirement.PendingResourceCount >= 1);
        ObservedResource failedGeometry = Assert.Single(
            disposer.GetObserved<CanvasGeometry>(), item => item.IsFaultTarget);

        CollectGarbage();
        AssertCollected(scene);
        AssertCollected(creator);
        AssertRetained(failedGeometry.Resource);

        disposer.ClearFault();
        Assert.True(retirement.DrainOnce());
        Assert.Equal(0, retirement.PendingRecordCount);
        Assert.Equal(0, retirement.PendingResourceCount);
        CollectGarbage();
        AssertCollected(failedGeometry.Resource);
    }

    [Fact]
    public void PathBuilderDisposeFailureRetainsItsSlotAndNotTheCreatedGeometryOrScene()
    {
        NativeRetirementEvidence evidence = RunFaultedNativeRetirement<CanvasPathBuilder>(CreateSinglePathScene);
        Assert.Equal(0, evidence.Owner.PendingRecordCount);
        CollectGarbage();
        AssertCollected(evidence.FailedResource);
        Assert.All(evidence.ReleasedResources, resource => AssertCollected(resource));
        AssertCollected(evidence.Scene);
        AssertCollected(evidence.Creator);
        GC.KeepAlive(evidence.Owner);
    }

    [Fact]
    public void TemporaryTextFormatDisposeFailureIsRetiredWithoutRetainingSceneOrLayout()
    {
        NativeRetirementEvidence evidence = RunFaultedNativeRetirement<CanvasTextFormat>(CreateTextScene);
        Assert.Equal(0, evidence.Owner.PendingRecordCount);
        CollectGarbage();
        AssertCollected(evidence.FailedResource);
        Assert.All(evidence.ReleasedResources, resource => AssertCollected(resource));
        AssertCollected(evidence.Scene);
        AssertCollected(evidence.Creator);
        GC.KeepAlive(evidence.Owner);
    }

    [Fact]
    public void StrokeStyleDisposeFailureTransfersTheRealWin2DSlot()
    {
        NativeRetirementEvidence evidence = RunFaultedNativeRetirement<CanvasStrokeStyle>(CreateStrokeStyleScene);
        Assert.Equal(0, evidence.Owner.PendingRecordCount);
        CollectGarbage();
        AssertCollected(evidence.FailedResource);
        Assert.All(evidence.ReleasedResources, resource => AssertCollected(resource));
        AssertCollected(evidence.Scene);
        AssertCollected(evidence.Creator);
        GC.KeepAlive(evidence.Owner);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static NativeRetirementEvidence RunFaultedNativeRetirement<TResource>(
        Func<MarkdownVectorScene> sceneFactory)
        where TResource : class, IDisposable
    {
        var retirement = new NativeResourceRetirement(createTimer: false);
        var disposer = new NativeDisposalProbe();
        disposer.FailPersistentlyOnFirst<TResource>();

        WeakReference<MarkdownVectorScene> scene = CreateAndRetireDrawing(
            sceneFactory, retirement, disposer, eager: false,
            out WeakReference<TestResourceCreator> creator);
        Assert.Equal(1, retirement.PendingRecordCount);
        Assert.Equal(1, retirement.PendingResourceCount);

        ObservedResource failed = Assert.Single(
            disposer.GetObserved<TResource>(), item => item.IsFaultTarget);
        WeakReference<IDisposable>[] released = disposer.GetObserved<IDisposable>()
            .Where(static item => !item.IsFaultTarget)
            .Select(static item => item.Resource)
            .ToArray();

        CollectGarbage();
        AssertCollected(scene);
        AssertCollected(creator);
        AssertRetained(failed.Resource, "The failed native slot was not durably rooted before retry.");
        Assert.All(released, resource => AssertCollected(resource, "A successful sibling remained rooted before retry."));

        DrainAfterPersistentFailure(
            retirement, disposer, expectedPendingRecords: 1, expectedPendingResources: 1);
        Assert.Equal(0, retirement.PendingRecordCount);
        Assert.Equal(0, retirement.PendingResourceCount);

        // Keep the manager alive for the outer test's final custody proof, but
        // return no probe, drawing, scene, creator, or strong native wrapper.
        // The caller's final GC therefore proves completed retirement released
        // its last resource owner, not merely that a live helper cleared a field.
        return new NativeRetirementEvidence(retirement, scene, creator, failed.Resource, released);
    }

    private sealed record NativeRetirementEvidence(
        NativeResourceRetirement Owner,
        WeakReference<MarkdownVectorScene> Scene,
        WeakReference<TestResourceCreator> Creator,
        WeakReference<IDisposable> FailedResource,
        WeakReference<IDisposable>[] ReleasedResources);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference<MarkdownVectorScene> CreateAndRetireDrawing(
        Func<MarkdownVectorScene> sceneFactory,
        NativeResourceRetirement retirement,
        NativeDisposalProbe disposer,
        bool eager,
        out WeakReference<TestResourceCreator> creatorReference)
    {
        MarkdownVectorScene scene = sceneFactory();
        var creator = new TestResourceCreator(CanvasDevice.GetSharedDevice());
        creatorReference = new WeakReference<TestResourceCreator>(creator);
        var sceneReference = new WeakReference<MarkdownVectorScene>(scene);
        var drawing = new VectorSceneDrawing(
            creator,
            scene,
            CancellationToken.None,
            deferNativeResources: !eager,
            nativeRetirement: retirement,
            nativeDisposer: disposer.Dispose);
        try
        {
            drawing.PrepareNativeResources(CancellationToken.None);
        }
        catch (Exception)
        {
            // Temporary builder/format failures are expected in their focused
            // rows; the terminal call below transfers any unreleased slots.
        }

        try
        {
            drawing.Dispose();
        }
        catch (Exception)
        {
            // Persistent native-dispose faults are expected and are represented
            // only by the manager's source-free retirement record.
        }

        return sceneReference;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference<MarkdownVectorScene> CreateEagerPartialPrefixFailure(
        NativeResourceRetirement retirement,
        NativeDisposalProbe disposer,
        out WeakReference<FailAtDeviceRequestCreator> creatorReference)
    {
        MarkdownVectorScene scene = CreateTwoPathScene();
        var creator = new FailAtDeviceRequestCreator(CanvasDevice.GetSharedDevice(), failAtRequest: 2);
        creatorReference = new WeakReference<FailAtDeviceRequestCreator>(creator);
        var sceneReference = new WeakReference<MarkdownVectorScene>(scene);

        Assert.Throws<AggregateException>(() => new VectorSceneDrawing(
            creator,
            scene,
            CancellationToken.None,
            deferNativeResources: false,
            nativeRetirement: retirement,
            nativeDisposer: disposer.Dispose));

        return sceneReference;
    }

    private static MarkdownVectorScene CreateTwoPathScene() => new(
        40,
        40,
        40,
        [MarkdownVectorCommand.FillPath(CreatePath(0)), MarkdownVectorCommand.FillPath(CreatePath(20))]);

    private static MarkdownVectorScene CreateSinglePathScene() => new(
        40,
        40,
        40,
        [MarkdownVectorCommand.FillPath(CreatePath(0))]);

    private static MarkdownVectorScene CreateTextScene() => new(
        100,
        32,
        24,
        [MarkdownVectorCommand.DrawText(
            "native label",
            new MarkdownVectorPoint(4, 24),
            new MarkdownVectorTextStyle("Arial", 12),
            new MarkdownVectorPaintStyle(fillArgb: 0xFF202020))]);

    private static MarkdownVectorScene CreateStrokeStyleScene() => new(
        40,
        40,
        40,
        [MarkdownVectorCommand.DrawLine(
            new MarkdownVectorPoint(2, 2),
            new MarkdownVectorPoint(30, 30),
            new MarkdownVectorPaintStyle(
                strokeArgb: 0xFF202020,
                strokeWidth: 1,
                lineCap: MarkdownVectorLineCap.Round))]);

    private static MarkdownVectorPathOperation[] CreatePath(float offset) =>
    [
        MarkdownVectorPathOperation.MoveTo(new MarkdownVectorPoint(offset, 0)),
        MarkdownVectorPathOperation.LineTo(new MarkdownVectorPoint(offset + 10, 10)),
        MarkdownVectorPathOperation.Close(),
    ];

    private static void CollectGarbage()
    {
        for (int iteration = 0; iteration < 3; iteration++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void DrainAfterPersistentFailure(
        NativeResourceRetirement retirement,
        NativeDisposalProbe disposer,
        int expectedPendingRecords,
        long expectedPendingResources)
    {
        Assert.False(retirement.DrainOnce());
        Assert.Equal(expectedPendingRecords, retirement.PendingRecordCount);
        Assert.Equal(expectedPendingResources, retirement.PendingResourceCount);

        disposer.ClearFault();
        Assert.True(retirement.DrainOnce());
        Assert.Equal(0, retirement.PendingRecordCount);
        Assert.Equal(0, retirement.PendingResourceCount);
    }

    private sealed class ProbeResource : IDisposable
    {
        internal int DisposeCount { get; private set; }
        internal bool FailDisposal { get; set; }

        public void Dispose()
        {
            DisposeCount++;
            if (FailDisposal)
                throw new InvalidOperationException("Injected retirement queue failure.");
        }
    }

    private sealed class ControlledRetryDisposer
    {
        private bool _allowDisposal;

        internal int AttemptCount { get; private set; }
        internal int SuccessfulDisposalCount { get; private set; }

        internal void AllowDisposal() => _allowDisposal = true;

        internal void Dispose(IDisposable resource)
        {
            AttemptCount++;
            if (!_allowDisposal)
                throw new InvalidOperationException("Injected retryable retirement failure.");

            resource.Dispose();
            SuccessfulDisposalCount++;
        }
    }

    private sealed class NativeDisposalProbe
    {
        private readonly ConditionalWeakTable<IDisposable, FaultState> _states = new();
        private readonly List<ObservedResource> _observed = [];
        private Type? _faultType;
        private bool _faultAssigned;
        private bool _faultEnabled;

        internal void FailPersistentlyOnFirst<TResource>() where TResource : class, IDisposable
        {
            _faultType = typeof(TResource);
            _faultAssigned = false;
            _faultEnabled = true;
        }

        internal void ClearFault() => _faultEnabled = false;

        internal ObservedResource[] GetObserved<TResource>() where TResource : class, IDisposable =>
            _observed.Where(item => typeof(TResource).IsAssignableFrom(item.ResourceType)).ToArray();

        internal void Dispose(IDisposable resource)
        {
            if (!_states.TryGetValue(resource, out FaultState? state))
            {
                bool isFaultTarget = _faultEnabled &&
                    _faultType is not null &&
                    _faultType.IsAssignableFrom(resource.GetType()) &&
                    !_faultAssigned;
                state = new FaultState(isFaultTarget);
                _states.Add(resource, state);
                _observed.Add(new ObservedResource(
                    resource.GetType(),
                    new WeakReference<IDisposable>(resource),
                    isFaultTarget));
                if (isFaultTarget)
                    _faultAssigned = true;
            }

            state.DisposeAttempts++;
            if (_faultEnabled && state.IsFaultTarget)
            {
                throw new InvalidOperationException(
                    $"Injected persistent Dispose failure for {resource.GetType().Name}.");
            }

            resource.Dispose();
        }

        private sealed class FaultState(bool isFaultTarget)
        {
            internal bool IsFaultTarget { get; } = isFaultTarget;
            internal int DisposeAttempts;
        }
    }

    private sealed record ObservedResource(
        Type ResourceType,
        WeakReference<IDisposable> Resource,
        bool IsFaultTarget);

    private sealed class TestResourceCreator(CanvasDevice device) : ICanvasResourceCreator
    {
        public CanvasDevice Device { get; } = device;
    }

    private sealed class FailAtDeviceRequestCreator(CanvasDevice device, int failAtRequest) : ICanvasResourceCreator
    {
        private int _requestCount;

        public CanvasDevice Device
        {
            get
            {
                int request = Interlocked.Increment(ref _requestCount);
                if (request == failAtRequest)
                    throw new InvalidOperationException("Injected second-path device request failure.");
                return device;
            }
        }
    }
}
