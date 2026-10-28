using MarkdownRenderer.Performance;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

[Collection(AllocationSensitiveContractCollection.Name)]
public sealed class MarkdownImageResidencyPolicyTests
{
    [Fact]
    public void OffscreenResourceReleaseIsOptInAndSeparateFromTheSourceCacheBudget()
    {
        var defaults = new MarkdownPerformanceOptions();
        Assert.False(defaults.ReleaseOffscreenImageResources);
        Assert.True(MarkdownPerformanceOptions.Progressive.ReleaseOffscreenImageResources);

        var configured = defaults with
        {
            SourceCacheBudgetBytes = 1024,
            ReleaseOffscreenImageResources = true,
        };
        Assert.Equal(1024, configured.SourceCacheBudgetBytes);
        Assert.True(configured.ReleaseOffscreenImageResources);

        using var disabledSession = new MarkdownPerformanceSession(defaults);
        var disabledApi = (IMarkdownPerformanceSessionInternal)disabledSession;
        Assert.Null(disabledApi.CreateImageResidencyPolicy(static () => { }));

        using var enabledSession = new MarkdownPerformanceSession(configured);
        var enabledApi = (IMarkdownPerformanceSessionInternal)enabledSession;
        using IMarkdownImageResidencyPolicy policy = Assert.IsType<MarkdownImageResidencyPolicy>(
            enabledApi.CreateImageResidencyPolicy(static () => { }));
    }

    [Fact]
    public void PublishedPlanIsVisitedOnlyDuringRebuildAndStableImagesKeepReferenceIdentity()
    {
        using var policy = new MarkdownImageResidencyPolicy(static () => { });
        var active = new FakeTarget("same-identity");
        FakeTarget[] published = Enumerable.Range(0, 128)
            .Select(index => new FakeTarget($"offscreen-{index}"))
            .Append(active)
            .ToArray();

        policy.Reconcile([active], published);
        Assert.Equal(1, active.ResumeCalls);
        Assert.Equal(0, active.SuspensionCalls);
        Assert.Equal(0, active.ReleaseCalls);
        Assert.All(published[..^1], target =>
        {
            Assert.Equal(1, target.SuspensionCalls);
            Assert.Equal(1, target.ReleaseCalls);
            Assert.Equal(0, target.ResumeCalls);
        });

        // Scroll reconciliation omits the document-wide plan. Stable active
        // objects are not reactivated, and released offscreen objects are not
        // revisited or retained in a permanent seen set.
        policy.Reconcile([active]);
        Assert.Equal(1, active.ResumeCalls);
        Assert.All(published[..^1], target =>
        {
            Assert.Equal(1, target.SuspensionCalls);
            Assert.Equal(1, target.ReleaseCalls);
        });

        var equalButDistinct = new FakeTarget("same-identity");
        policy.Reconcile([equalButDistinct]);
        Assert.Equal(1, equalButDistinct.ResumeCalls);
        Assert.Equal(1, active.SuspensionCalls);
        Assert.Equal(1, active.ReleaseCalls);
    }

    [Fact]
    public void PendingResumeWaitsForDrainAndDrainEventOnlyRequestsReconciliation()
    {
        int reconciliationRequests = 0;
        using var policy = new MarkdownImageResidencyPolicy(() => reconciliationRequests++);
        var image = new FakeTarget("pending")
        {
            AllowResume = false,
            AllowRelease = false,
        };

        // A just-published offscreen box may still own constructor-acquired
        // resources while an earlier generation is draining.
        policy.Reconcile([], [image]);
        Assert.Equal(0, image.ResumeCalls);
        Assert.Equal(1, image.SuspensionCalls);
        Assert.Equal(1, image.ReleaseCalls);
        Assert.Equal(1, image.SubscriberCount);

        // Re-entry is remembered, but no resource restart is attempted until
        // the canceled work and its publication callbacks have drained.
        policy.Reconcile([image]);
        Assert.Equal(1, image.ResumeCalls);
        Assert.Equal(2, image.ReleaseCalls);

        int resumeCallsBeforeDrainNotification = image.ResumeCalls;
        image.AllowResume = true;
        image.AllowRelease = true;
        image.RaiseResidencyWorkDrained();
        Assert.Equal(1, reconciliationRequests);
        Assert.Equal(resumeCallsBeforeDrainNotification, image.ResumeCalls);
        Assert.Equal(1, image.SubscriberCount);

        policy.Reconcile([image]);
        Assert.Equal(2, image.ResumeCalls);
        Assert.Equal(0, image.SubscriberCount);

        policy.Reconcile([]);
        Assert.Equal(2, image.SuspensionCalls);
        Assert.Equal(3, image.ReleaseCalls);
        Assert.Equal(0, image.SubscriberCount);
    }

    [Fact]
    public void StableResidencyBandDoesNotAllocateOrRepeatLifecycleTransitions()
    {
        using var policy = new MarkdownImageResidencyPolicy(static () => { });
        FakeTarget[] band = Enumerable.Range(0, 40)
            .Select(index => new FakeTarget($"stable-{index}"))
            .ToArray();
        policy.Reconcile(band, band);
        for (int iteration = 0; iteration < 100; iteration++)
            policy.Reconcile(band);

        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        for (int iteration = 0; iteration < 1000; iteration++)
            policy.Reconcile(band);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

        Assert.Equal(0, allocated);
        Assert.All(band, image =>
        {
            Assert.Equal(1, image.ResumeCalls);
            Assert.Equal(0, image.SuspensionCalls);
            Assert.Equal(0, image.ReleaseCalls);
        });
    }

    [Fact]
    public void LongTraversalOnlyTouchesTheChangedBandAndUnsubscribesReleasedImages()
    {
        using var policy = new MarkdownImageResidencyPolicy(static () => { });
        FakeTarget[] images = Enumerable.Range(0, 5000)
            .Select(index => new FakeTarget($"traversal-{index}"))
            .ToArray();
        const int bandSize = 40;
        policy.Reconcile(images[..bandSize], images);
        AssertTrackingStorageIsBandSized(policy);
        Assert.All(images[bandSize..], image =>
        {
            Assert.Equal(1, image.SuspensionCalls);
            Assert.Equal(1, image.ReleaseCalls);
            Assert.Equal(0, image.SubscriberCount);
        });

        // After the single published-plan pass, scroll supplies only the
        // indexed band. Inactive boxes must never be revisited on each frame.
        for (int start = bandSize; start < images.Length; start += bandSize)
        {
            FakeTarget[] band = images[start..System.Math.Min(images.Length, start + bandSize)];
            policy.Reconcile(band);
            policy.Reconcile(band);
            Assert.All(band, image => Assert.Equal(1, image.ResumeCalls));
        }
        policy.Reconcile([]);
        AssertTrackingStorageIsBandSized(policy);

        Assert.All(images, image =>
        {
            Assert.Equal(1, image.ResumeCalls);
            Assert.Equal(0, image.SubscriberCount);
        });
        Assert.All(images[..bandSize], image =>
        {
            Assert.Equal(1, image.SuspensionCalls);
            Assert.Equal(1, image.ReleaseCalls);
        });
        Assert.All(images[bandSize..], image =>
        {
            Assert.Equal(2, image.SuspensionCalls);
            Assert.Equal(2, image.ReleaseCalls);
        });

        // A reverse jump is an ordinary re-entry, not a retained seen-set hit.
        policy.Reconcile(images[..bandSize]);
        Assert.All(images[..bandSize], image => Assert.Equal(2, image.ResumeCalls));
        Assert.All(images[bandSize..], image => Assert.Equal(1, image.ResumeCalls));
    }

    private static void AssertTrackingStorageIsBandSized(MarkdownImageResidencyPolicy policy)
    {
        const System.Reflection.BindingFlags flags =
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        foreach (string name in new[] { "_active", "_pending", "_subscribed", "_requested" })
        {
            var targets = (HashSet<IMarkdownImageResidencyTarget>)
                typeof(MarkdownImageResidencyPolicy).GetField(name, flags)!.GetValue(policy)!;
            Assert.InRange(targets.EnsureCapacity(0), 0, 512);
        }
        foreach (string name in new[] { "_retireScratch", "_pendingScratch" })
        {
            var targets = (List<IMarkdownImageResidencyTarget>)
                typeof(MarkdownImageResidencyPolicy).GetField(name, flags)!.GetValue(policy)!;
            Assert.InRange(targets.Capacity, 0, 512);
        }
    }

    [Fact]
    public void ResetUnsubscribesPendingBoxesAndDropsThePriorSnapshotReferences()
    {
        int reconciliationRequests = 0;
        using var policy = new MarkdownImageResidencyPolicy(() => reconciliationRequests++);
        var active = new FakeTarget("active");
        var pending = new FakeTarget("pending") { AllowRelease = false };
        policy.Reconcile([active], [active, pending]);
        Assert.Equal(1, pending.SubscriberCount);

        policy.Reset();
        Assert.Equal(0, pending.SubscriberCount);

        pending.RaiseResidencyWorkDrained();
        Assert.Equal(0, reconciliationRequests);
        Assert.Equal(1, pending.ReleaseCalls);

        var replacement = new FakeTarget("replacement");
        policy.Reconcile([replacement]);
        Assert.Equal(1, replacement.ResumeCalls);
        Assert.Equal(0, active.SuspensionCalls);
        Assert.Equal(1, pending.ReleaseCalls);
    }

    private sealed class FakeTarget(string identity) : IMarkdownImageResidencyTarget
    {
        private readonly string _identity = identity;
        private EventHandler? _residencyWorkDrained;

        public bool AllowResume { get; set; } = true;
        public bool AllowRelease { get; set; } = true;
        public int ResumeCalls { get; private set; }
        public int SuspensionCalls { get; private set; }
        public int ReleaseCalls { get; private set; }
        public int SubscriberCount => _residencyWorkDrained?.GetInvocationList().Length ?? 0;

        public event EventHandler? ResidencyWorkDrained
        {
            add => _residencyWorkDrained += value;
            remove => _residencyWorkDrained -= value;
        }

        public void SuspendResources() => SuspensionCalls++;

        public bool TryReleaseSuspendedResources()
        {
            ReleaseCalls++;
            return AllowRelease;
        }

        public bool TryResumeResources()
        {
            ResumeCalls++;
            return AllowResume;
        }

        public void RaiseResidencyWorkDrained() =>
            _residencyWorkDrained?.Invoke(this, EventArgs.Empty);

        public override bool Equals(object? obj) =>
            obj is FakeTarget other && string.Equals(_identity, other._identity, StringComparison.Ordinal);

        public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(_identity);
    }
}
