using System.Reflection;
using System.Runtime.CompilerServices;
using System.Collections.Concurrent;
using MarkdownRenderer.Images;
using MarkdownRenderer.Layout.Boxes;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

// These bare managed-owner tests never construct a XAML object, load images,
// create a device or invoke a native queue. Injected queues retain callbacks.
public sealed class SvgHostRetryReadinessTests
{
    private const int Generation = 7;

    [Fact]
    public async Task ObservedSelfReleaseReregistersOnlyTheSourceFreeThresholdWait()
    {
        var (owner, demand, _) = CreateWaiting();
        using var lifetime = new CancellationTokenSource();
        var admission = new ThresholdProbe();
        var queued = new ConcurrentQueue<Action>();
        Set(owner, "_svgWorkSync", new object());
        Set(owner, "_imageLifetimeCancellation", lifetime);
        Set(owner, "_svgHostDeferredAdmission", new WeakReference<IMarkdownSvgAdmission>(admission));
        Set(owner, "_svgDeferredRetryEnqueueOverride", (Func<Action, bool>)(work => { queued.Enqueue(work); return true; }));
        try
        {
            typeof(ImageBox).GetMethod("TryResumeDeferredSvgLoadForObservedDemandOrCapacity",
                BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(owner, [demand]);
            await admission.WaitStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Empty(queued);
            Assert.Equal(1, admission.WaitCount);
            Assert.Equal(73728, admission.RequiredBytes);
            Assert.Equal(MarkdownSvgRenderPriority.Overscan, admission.Priority);
            Assert.Equal(12, Field<long>(owner, "_svgHostDeferredCapacityVersion"));
            Assert.False(Field<bool>(owner, "_svgHostDeferredCapacityReady"));
            Assert.Equal(1, Field<int>(owner, "_svgHostDeferred"));

            admission.Ready.TrySetResult(); // No additional numeric version change.
            Assert.True(SpinWait.SpinUntil(() => queued.Count == 1, TimeSpan.FromSeconds(5)));
            Assert.Equal(1, admission.WaitCount);
            Assert.Equal(0, admission.OpenCount);
            Assert.True(Field<bool>(owner, "_svgHostDeferredCapacityReady"));
            Assert.Equal(2, Field<int>(owner, "_svgHostDeferred"));
        }
        finally
        {
            typeof(ImageBox).GetMethod("ClearSvgHostDeferral",
                BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(owner, null);
            await lifetime.CancelAsync();
        }
        GC.KeepAlive(admission);
        GC.KeepAlive(owner);
    }

    [Fact]
    public void NumericCapacityPulseAloneCannotRetryAnInsufficientSourceDenial()
    {
        var (owner, demand, identity) = CreateWaiting();
        int calls = 0;
        Set(owner, "_svgDeferredRetryEnqueueOverride", (Func<Action, bool>)(_ => { calls++; return true; }));
        owner.TryQueueDeferredSvgHostRetry(demand, Generation, 0, 12, 0);
        Assert.Equal(0, calls);
        Assert.Equal(1, Field<int>(owner, "_svgHostDeferred"));
        Assert.False(Field<bool>(owner, "_svgHostDeferredCapacityReady"));
        Assert.Same(identity, Field<object>(owner, "_svgHostDeferredWaiterIdentity"));
    }

    [Fact]
    public void AcceptedThresholdCanRetryWithoutAdvancingNumericCapacityVersion()
    {
        var (owner, demand, identity) = CreateWaiting();
        int calls = 0;
        Set(owner, "_svgDeferredRetryEnqueueOverride", (Func<Action, bool>)(_ => { calls++; return true; }));
        owner.TryQueueDeferredSvgHostRetry(demand, Generation, 0, 11, 0,
            capacityThresholdSatisfied: true, waiterIdentity: identity);
        Assert.Equal(1, calls);
        Assert.Equal(2, Field<int>(owner, "_svgHostDeferred"));
        Assert.Equal(11, Field<long>(owner, "_svgHostDeferredCapacityVersion"));
        Assert.True(Field<bool>(owner, "_svgHostDeferredCapacityReady"));
        owner.OnSvgHostAdmissionWaiterFinished(demand, Generation, identity);
        Assert.Same(identity, Field<object>(owner, "_svgHostDeferredWaiterIdentity"));
    }

    [Fact]
    public void OldWaiterThresholdCannotQueueAReplacementWithTheSameDemandAndGeneration()
    {
        var (owner, demand, identity) = CreateWaiting();
        object replacement = new();
        Set(owner, "_svgHostDeferredWaiterIdentity", replacement);
        int calls = 0;
        Set(owner, "_svgDeferredRetryEnqueueOverride", (Func<Action, bool>)(_ => { calls++; return true; }));
        owner.TryQueueDeferredSvgHostRetry(demand, Generation, 0, 11, 0,
            capacityThresholdSatisfied: true, waiterIdentity: identity);
        Assert.Equal(0, calls);
        Assert.Equal(1, Field<int>(owner, "_svgHostDeferred"));
        Assert.False(Field<bool>(owner, "_svgHostDeferredCapacityReady"));
        Assert.Same(replacement, Field<object>(owner, "_svgHostDeferredWaiterIdentity"));
    }

    [Fact]
    public void SourceThresholdCannotSatisfyAnOwnerSlotDenial()
    {
        var (owner, demand, identity) = CreateWaiting();
        Set(owner, "_svgHostDeferredRequiresOwnerSlot", true);
        int calls = 0;
        Set(owner, "_svgDeferredRetryEnqueueOverride", (Func<Action, bool>)(_ => { calls++; return true; }));
        owner.TryQueueDeferredSvgHostRetry(demand, Generation, 0, 11, 0,
            capacityThresholdSatisfied: true, waiterIdentity: identity);
        Assert.Equal(0, calls);
        Assert.False(Field<bool>(owner, "_svgHostDeferredCapacityReady"));
        owner.TryQueueDeferredSvgHostRetry(demand, Generation, 0, 11, 1);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void DualQueueRejectionRetainsThresholdForOneLaterObservation()
    {
        var (owner, demand, identity) = CreateWaiting();
        int calls = 0;
        Set(owner, "_svgDeferredRetryEnqueueOverride", (Func<Action, bool>)(_ => ++calls > 2));
        owner.TryQueueDeferredSvgHostRetry(demand, Generation, 0, 11, 0,
            capacityThresholdSatisfied: true, waiterIdentity: identity);
        Assert.Equal(2, calls);
        Assert.Equal(1, Field<int>(owner, "_svgHostDeferred"));
        owner.OnSvgHostAdmissionWaiterFinished(demand, Generation, identity);
        Assert.True(Field<bool>(owner, "_svgHostDeferredCapacityReady"));
        Assert.Same(identity, Field<object>(owner, "_svgHostDeferredWaiterIdentity"));
        owner.TryQueueDeferredSvgHostRetry(demand, Generation, 0, 11, 0);
        owner.TryQueueDeferredSvgHostRetry(demand, Generation, 0, 11, 0);
        Assert.Equal(3, calls);
        Assert.Equal(2, Field<int>(owner, "_svgHostDeferred"));
    }

    [Fact]
    public void LateQueuedWorkCannotClearANewerDenialForTheSameDemandAndGeneration()
    {
        var (owner, demand, identity) = CreateWaiting();
        Action? oldWork = null;
        Set(owner, "_svgDeferredRetryEnqueueOverride", (Func<Action, bool>)(work => { oldWork = work; return true; }));
        owner.TryQueueDeferredSvgHostRetry(demand, Generation, 0, 11, 0,
            capacityThresholdSatisfied: true, waiterIdentity: identity);
        Assert.NotNull(oldWork);
        object newerDenial = new();
        Set(owner, "_svgHostDeferredDenialIdentity", newerDenial);
        oldWork!();
        Assert.Equal(2, Field<int>(owner, "_svgHostDeferred"));
        Assert.Same(newerDenial, Field<object>(owner, "_svgHostDeferredDenialIdentity"));
        Assert.True(Field<bool>(owner, "_svgHostDeferredCapacityReady"));
    }

    [Fact]
    public void DelayedRetryCannotStartAReplacementLoadGenerationWithTheSameDemandAndDenialIdentity()
    {
        var (owner, demand, identity) = CreateWaiting();
        Action? queued = null;
        Set(owner, "_svgDeferredRetryEnqueueOverride", (Func<Action, bool>)(work => { queued = work; return true; }));
        owner.TryQueueDeferredSvgHostRetry(demand, Generation, 0, 11, 0,
            capacityThresholdSatisfied: true, waiterIdentity: identity);
        Assert.NotNull(queued);
        Assert.Equal(2, Field<int>(owner, "_svgHostDeferred"));

        // Keep demand and denial identity equal deliberately: the generation
        // fence itself must reject an old queued callback before StartLoad.
        Set(owner, "_svgLoadDemandGeneration", Generation + 1);
        queued!();

        Assert.Equal(Generation + 1, Field<int>(owner, "_svgLoadDemandGeneration"));
        Assert.Equal(2, Field<int>(owner, "_svgHostDeferred"));
        Assert.True(Field<bool>(owner, "_svgHostDeferredCapacityReady"));
        Assert.Same(identity, Field<object>(owner, "_svgHostDeferredWaiterIdentity"));
    }

    [Fact]
    public void LateThresholdCannotQueueANewerGenerationEvenWhenDemandAndWaiterIdentityMatch()
    {
        var (owner, demand, identity) = CreateWaiting();
        int queues = 0;
        Set(owner, "_svgDeferredRetryEnqueueOverride", (Func<Action, bool>)(_ => { queues++; return true; }));
        Set(owner, "_svgLoadDemandGeneration", Generation + 1);

        owner.TryQueueDeferredSvgHostRetry(demand, Generation, 0, 11, 0,
            capacityThresholdSatisfied: true, waiterIdentity: identity);

        Assert.Equal(0, queues);
        Assert.Equal(1, Field<int>(owner, "_svgHostDeferred"));
        Assert.False(Field<bool>(owner, "_svgHostDeferredCapacityReady"));
        Assert.Same(identity, Field<object>(owner, "_svgHostDeferredWaiterIdentity"));
    }

    [Fact]
    public void ReentrantQueueFailureCannotRollBackANewerDenial()
    {
        var (owner, demand, identity) = CreateWaiting();
        object newerDenial = new();
        int calls = 0;
        Set(owner, "_svgDeferredRetryEnqueueOverride", (Func<Action, bool>)(_ =>
        {
            calls++;
            Set(owner, "_svgHostDeferredDenialIdentity", newerDenial);
            return false;
        }));
        owner.TryQueueDeferredSvgHostRetry(demand, Generation, 0, 11, 0,
            capacityThresholdSatisfied: true, waiterIdentity: identity);
        Assert.Equal(2, calls);
        Assert.Equal(2, Field<int>(owner, "_svgHostDeferred"));
        Assert.Same(newerDenial, Field<object>(owner, "_svgHostDeferredDenialIdentity"));
    }

    [Fact]
    public void ThrowingPrimaryQueueUsesTheFallbackOnce()
    {
        var (owner, demand, identity) = CreateWaiting();
        int calls = 0;
        Set(owner, "_svgDeferredRetryEnqueueOverride", (Func<Action, bool>)(_ =>
        {
            if (++calls == 1) throw new InvalidOperationException("primary queue");
            return true;
        }));
        owner.TryQueueDeferredSvgHostRetry(demand, Generation, 0, 11, 0,
            capacityThresholdSatisfied: true, waiterIdentity: identity);
        Assert.Equal(2, calls);
        Assert.Equal(2, Field<int>(owner, "_svgHostDeferred"));
        Assert.True(Field<bool>(owner, "_svgHostDeferredCapacityReady"));
    }

    [Fact]
    public void ThrowingFallbackRestoresWaitingAndExplicitDeferralClearRetiresReadiness()
    {
        var (owner, demand, identity) = CreateWaiting();
        int calls = 0;
        Set(owner, "_svgDeferredRetryEnqueueOverride", (Func<Action, bool>)(_ =>
        {
            calls++;
            throw new InvalidOperationException("queue");
        }));
        owner.TryQueueDeferredSvgHostRetry(demand, Generation, 0, 11, 0,
            capacityThresholdSatisfied: true, waiterIdentity: identity);
        Assert.Equal(2, calls);
        Assert.Equal(1, Field<int>(owner, "_svgHostDeferred"));
        Assert.True(Field<bool>(owner, "_svgHostDeferredCapacityReady"));
        typeof(ImageBox).GetMethod("ClearSvgHostDeferral", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(owner, null);
        owner.TryQueueDeferredSvgHostRetry(demand, Generation, 0, 11, 0,
            capacityThresholdSatisfied: true, waiterIdentity: identity);
        Assert.Equal(2, calls);
        Assert.Equal(0, Field<int>(owner, "_svgHostDeferred"));
        Assert.False(Field<bool>(owner, "_svgHostDeferredCapacityReady"));
    }

    [Fact]
    public void ThresholdWithoutAnAcceptedWaiterIdentityCannotQueue()
    {
        var (owner, demand, _) = CreateWaiting();
        int calls = 0;
        Set(owner, "_svgDeferredRetryEnqueueOverride", (Func<Action, bool>)(_ => { calls++; return true; }));
        owner.TryQueueDeferredSvgHostRetry(demand, Generation, 0, 12, 0,
            capacityThresholdSatisfied: true);
        Assert.Equal(0, calls);
        Assert.Equal(1, Field<int>(owner, "_svgHostDeferred"));
        Assert.False(Field<bool>(owner, "_svgHostDeferredCapacityReady"));
    }

    [Fact]
    public void FinishedUnreadyWaiterRetiresIdentityWithoutClearingItsScalarDenial()
    {
        var (owner, demand, identity) = CreateWaiting();
        object denial = Field<object>(owner, "_svgHostDeferredDenialIdentity");
        owner.OnSvgHostAdmissionWaiterFinished(demand, Generation, identity);
        Assert.Equal(1, Field<int>(owner, "_svgHostDeferred"));
        Assert.False(Field<bool>(owner, "_svgHostDeferredCapacityReady"));
        Assert.Null(Value(owner, "_svgHostDeferredWaiterIdentity"));
        Assert.Null(Value(owner, "_svgHostDeferredRegistrationAttempt"));
        Assert.Same(denial, Field<object>(owner, "_svgHostDeferredDenialIdentity"));
    }

    [Fact]
    public void ClearingDeferralRetiresEveryScalarReadinessIdentity()
    {
        var (owner, demand, identity) = CreateWaiting();
        Set(owner, "_svgDeferredRetryEnqueueOverride", (Func<Action, bool>)(_ => true));
        owner.TryQueueDeferredSvgHostRetry(demand, Generation, 0, 11, 0,
            capacityThresholdSatisfied: true, waiterIdentity: identity);
        typeof(ImageBox).GetMethod("ClearSvgHostDeferral", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(owner, null);
        Assert.Equal(0, Field<int>(owner, "_svgHostDeferred"));
        Assert.False(Field<bool>(owner, "_svgHostDeferredCapacityReady"));
        Assert.Null(Value(owner, "_svgHostDeferredDenialIdentity"));
        Assert.Null(Value(owner, "_svgHostDeferredWaiterIdentity"));
        Assert.Null(Value(owner, "_svgHostDeferredRegistrationAttempt"));
    }

    private static (ImageBox, MarkdownSvgRenderDemand, object) CreateWaiting()
    {
        var owner = (ImageBox)RuntimeHelpers.GetUninitializedObject(typeof(ImageBox));
        var demand = new MarkdownSvgRenderDemand(MarkdownSvgRenderPriority.Overscan);
        object waiterIdentity = new();
        object denialIdentity = new();
        Set(owner, "_svgHostDeferredSync", new object());
        Set(owner, "_svgLoadDemandTicket", demand);
        Set(owner, "_svgLoadDemandGeneration", Generation);
        Set(owner, "_svgHostDeferred", 1);
        Set(owner, "_svgHostDeferredCapacityVersion", 11L);
        Set(owner, "_svgHostDeferredRequiredSourceBytes", 73728L);
        Set(owner, "_svgHostDeferredWaiterIdentity", waiterIdentity);
        Set(owner, "_svgHostDeferredRegistrationAttempt", denialIdentity);
        Set(owner, "_svgHostDeferredDenialIdentity", denialIdentity);
        return (owner, demand, waiterIdentity);
    }

    private static T Field<T>(ImageBox owner, string name) => (T)Value(owner, name)!;
    private static object? Value(ImageBox owner, string name) =>
        typeof(ImageBox).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner);
    private static void Set(ImageBox owner, string name, object value) =>
        typeof(ImageBox).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(owner, value);

    private sealed class ThresholdProbe : IMarkdownSvgAdmission
    {
        public object AdmissionDomain { get; } = new();
        public long CapacityVersion => 12;
        internal readonly TaskCompletionSource WaitStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int WaitCount;
        internal int OpenCount;
        internal long RequiredBytes;
        internal MarkdownSvgRenderPriority Priority;

        public Task WaitForCapacityAsync(long requiredFreeBytes, MarkdownSvgRenderPriority priority,
            CancellationToken cancellationToken)
        {
            RequiredBytes = requiredFreeBytes;
            Priority = priority;
            Interlocked.Increment(ref WaitCount);
            WaitStarted.TrySetResult();
            return Ready.Task.WaitAsync(cancellationToken);
        }

        public ValueTask<MarkdownSvgAdmissionResult> TryOpenAsync(MarkdownSvgOpenRequest request,
            MarkdownSvgRenderDemand demand, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref OpenCount);
            throw new InvalidOperationException("A scalar retry observation must not open source bytes.");
        }
    }
}
