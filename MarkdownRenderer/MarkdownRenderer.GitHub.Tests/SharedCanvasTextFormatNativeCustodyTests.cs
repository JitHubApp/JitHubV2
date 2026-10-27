using System.Runtime.CompilerServices;
using System.Threading;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Theming;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI.Xaml;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

[Collection("Shared Canvas text format cache")]
public sealed class SharedCanvasTextFormatNativeCustodyTests
{
    [Fact]
    public void CacheHitReusesFormatWithoutReservingAnotherNativeRecord()
    {
        var retirement = new NativeResourceRetirement(createTimer: false);
        var disposer = new FormatDisposalProbe();
        var cache = new SharedCanvasTextFormatCache.Store(8_192, retirement, disposer.Dispose);
        var style = CreateStyle("Cache hit family");

        using SharedCanvasTextFormatCache.Lease first = Acquire(cache, style);
        long reservationsAfterMiss = cache.RecordReservationCountForTests;
        using SharedCanvasTextFormatCache.Lease second = Acquire(cache, style);

        Assert.Same(first.Format, second.Format);
        Assert.Equal(reservationsAfterMiss, cache.RecordReservationCountForTests);
        Assert.Equal(1, reservationsAfterMiss);
        Assert.Equal(3, first.EntryForTests!.ReferenceCountForTests);
        cache.Clear();
        Assert.True(first.Format.FontSize > 0);
        GC.KeepAlive(retirement);
    }

    [Fact]
    public void CacheHitLeaseConstructionFailureBalancesOnlyTheAcquiredReference()
    {
        var retirement = new NativeResourceRetirement(createTimer: false);
        var disposer = new FormatDisposalProbe();
        var cache = new SharedCanvasTextFormatCache.Store(
            8_192,
            retirement,
            disposer.Dispose,
            failCacheHitLeaseConstructionForTests: true);
        var style = CreateStyle("Cache hit lease failure family");

        using SharedCanvasTextFormatCache.Lease existing = Acquire(cache, style);
        SharedCanvasTextFormatCache.Entry entry = Assert.IsType<SharedCanvasTextFormatCache.Entry>(existing.EntryForTests);
        long reservationsBeforeHit = cache.RecordReservationCountForTests;
        int referencesBeforeHit = entry.ReferenceCountForTests;

        InvalidOperationException failure = Assert.Throws<InvalidOperationException>(() => Acquire(cache, style));

        Assert.Equal("Injected cache-hit lease allocation failure.", failure.Message);
        Assert.Equal(referencesBeforeHit, entry.ReferenceCountForTests);
        Assert.Equal(reservationsBeforeHit, cache.RecordReservationCountForTests);
        Assert.Equal(0, retirement.PendingRecordCount);
        Assert.Equal(0, disposer.AttemptCount);
        GC.KeepAlive(retirement);
    }

    [Fact]
    public void VerticalAlignmentPartitionsFormatsWithoutChangingTheLegacyTopDefault()
    {
        var retirement = new NativeResourceRetirement(createTimer: false);
        var disposer = new FormatDisposalProbe();
        var cache = new SharedCanvasTextFormatCache.Store(8_192, retirement, disposer.Dispose);
        var style = CreateStyle("Vertical alignment family");

        using SharedCanvasTextFormatCache.Lease top = Acquire(cache, style);
        using SharedCanvasTextFormatCache.Lease centered = cache.Acquire(
            style,
            CanvasWordWrapping.Wrap,
            CanvasHorizontalAlignment.Left,
            FlowDirection.LeftToRight,
            "en-US",
            useContentAwareLineSpacing: false,
            verticalAlignment: CanvasVerticalAlignment.Center);
        using SharedCanvasTextFormatCache.Lease centeredAgain = cache.Acquire(
            style,
            CanvasWordWrapping.Wrap,
            CanvasHorizontalAlignment.Left,
            FlowDirection.LeftToRight,
            "en-US",
            useContentAwareLineSpacing: false,
            verticalAlignment: CanvasVerticalAlignment.Center);

        Assert.NotSame(top.Format, centered.Format);
        Assert.Same(centered.Format, centeredAgain.Format);
        Assert.Equal(CanvasVerticalAlignment.Top, top.Format.VerticalAlignment);
        Assert.Equal(CanvasVerticalAlignment.Center, centered.Format.VerticalAlignment);
        Assert.Equal(2, cache.Statistics.Count);
        Assert.Equal(2, cache.RecordReservationCountForTests);

        cache.Clear();
        GC.KeepAlive(retirement);
    }

    [Fact]
    public void ActiveLeaseKeepsNativeFormatAliveAcrossEvictionAndLastReleaseDisposesIt()
    {
        var retirement = new NativeResourceRetirement(createTimer: false);
        var disposer = new FormatDisposalProbe();
        var cache = new SharedCanvasTextFormatCache.Store(500, retirement, disposer.Dispose);

        EvictionEvidence evidence = AcquireAcrossEviction(cache);

        Assert.Equal(1, cache.Statistics.Count);
        Assert.Equal(0, disposer.AttemptCount);
        AssertRetained(evidence.EvictedFormat, "The active lease did not retain its evicted format.");
        Assert.Equal(1, evidence.EvictedEntryReferenceCount);
        disposer.AllowDisposal();
        evidence.EvictedLease.Dispose();
        Assert.Equal(1, disposer.AttemptCount);
        Assert.Equal(1, disposer.SuccessfulDisposalCount);
        cache.Clear();
        evidence.RetainedLease.Dispose();
        Assert.Equal(2, disposer.AttemptCount);
        Assert.Equal(2, disposer.SuccessfulDisposalCount);
        Assert.Equal(0, retirement.PendingRecordCount);
        GC.KeepAlive(retirement);
    }

    [Fact]
    public void FailedLastReleaseRetainsOnlyFormatAndCollectsEntryLeaseAndStyle()
    {
        var retirement = new NativeResourceRetirement(createTimer: false);
        var disposer = new FormatDisposalProbe();
        disposer.FailPersistently();
        var cache = new SharedCanvasTextFormatCache.Store(8_192, retirement, disposer.Dispose);

        NativeFormatEvidence evidence = CreateFailedLastRelease(cache, disposer);

        Assert.Equal(1, retirement.PendingRecordCount);
        Assert.Equal(1, retirement.PendingResourceCount);
        CollectGarbage();
        AssertCollected(evidence.Entry);
        AssertCollected(evidence.Lease);
        AssertCollected(evidence.Style);
        AssertRetained(evidence.Format, "The live retirement manager lost the failed CanvasTextFormat slot.");
        Assert.Equal(1, disposer.AttemptCount);

        disposer.AllowDisposal();
        Assert.True(retirement.DrainOnce());
        Assert.Equal(0, retirement.PendingRecordCount);
        Assert.Equal(0, retirement.PendingResourceCount);
        CollectGarbage();
        AssertCollected(evidence.Format);
        Assert.Equal(2, disposer.AttemptCount);
        Assert.Equal(1, disposer.SuccessfulDisposalCount);
        GC.KeepAlive(cache);
        GC.KeepAlive(disposer);
        GC.KeepAlive(retirement);
    }

    [Fact]
    public void InitializationFailureRemainsPrimaryWhileCleanupFailureIsRetired()
    {
        var retirement = new NativeResourceRetirement(createTimer: false);
        var disposer = new FormatDisposalProbe();
        disposer.FailPersistently();
        var primary = new InvalidOperationException("Injected initializer failure.");
        var cache = new SharedCanvasTextFormatCache.Store(
            8_192,
            retirement,
            disposer.Dispose,
            afterInitializeForTests: _ => throw primary);

        WeakReference<ElementStyle> style = AcquireWithInitializerFailure(cache, primary);
        WeakReference<CanvasTextFormat> format = Assert.Single(disposer.ObservedFormats);

        Assert.Equal(0, cache.Statistics.Count);
        Assert.Equal(1, retirement.PendingRecordCount);
        Assert.Equal(1, retirement.PendingResourceCount);
        CollectGarbage();
        AssertCollected(style);
        AssertRetained(format);

        disposer.AllowDisposal();
        Assert.True(retirement.DrainOnce());
        CollectGarbage();
        AssertCollected(format);
        GC.KeepAlive(retirement);
    }

    [Fact]
    public void EntryStageFailureRemainsPrimaryAndReleasesCreatedEntryAfterNativeRetry()
    {
        var retirement = new NativeResourceRetirement(createTimer: false);
        var disposer = new FormatDisposalProbe();
        disposer.FailPersistently();
        var primary = new InvalidOperationException("Injected post-entry setup failure.");
        WeakReference<SharedCanvasTextFormatCache.Entry>? entryWitness = null;
        var cache = new SharedCanvasTextFormatCache.Store(
            8_192,
            retirement,
            disposer.Dispose,
            afterEntryCreatedForTests: entry =>
            {
                entryWitness = new WeakReference<SharedCanvasTextFormatCache.Entry>(entry);
                throw primary;
            });

        WeakReference<ElementStyle> style = AcquireAndExpectFailure(cache, primary, "Entry failure family");
        WeakReference<CanvasTextFormat> format = Assert.Single(disposer.ObservedFormats);

        Assert.Equal(0, cache.Statistics.Count);
        Assert.Equal(1, retirement.PendingRecordCount);
        Assert.Equal(1, retirement.PendingResourceCount);
        WeakReference<SharedCanvasTextFormatCache.Entry> entryReference =
            Assert.IsType<WeakReference<SharedCanvasTextFormatCache.Entry>>(entryWitness);
        Assert.Equal(0, ReadReferenceCount(entryReference));
        CollectGarbage();
        AssertCollected(entryReference);
        AssertCollected(style);
        AssertRetained(format);

        disposer.AllowDisposal();
        Assert.True(retirement.DrainOnce());
        CollectGarbage();
        AssertCollected(format);
        GC.KeepAlive(retirement);
    }

    [Fact]
    public void LeaseConstructionFailurePreservesPrimaryAndRetiresCleanupFailedFormat()
    {
        var retirement = new NativeResourceRetirement(createTimer: false);
        var disposer = new FormatDisposalProbe();
        disposer.FailPersistently();
        var cache = new SharedCanvasTextFormatCache.Store(
            8_192,
            retirement,
            disposer.Dispose,
            failLeaseConstructionForTests: true);
        var style = CreateStyle("Lease failure family");

        InvalidOperationException failure = Assert.Throws<InvalidOperationException>(() => Acquire(cache, style));

        Assert.Equal("Injected text-format lease allocation failure.", failure.Message);
        Assert.IsType<InvalidOperationException>(failure.Data[SharedCanvasTextFormatCache.NativeCleanupFailureDataKey]);
        Assert.Equal(0, cache.Statistics.Count);
        Assert.Equal(1, retirement.PendingRecordCount);
        Assert.Equal(1, retirement.PendingResourceCount);
        WeakReference<CanvasTextFormat> format = Assert.Single(disposer.ObservedFormats);
        AssertRetained(format);

        disposer.AllowDisposal();
        Assert.True(retirement.DrainOnce());
        CollectGarbage();
        AssertCollected(format);
        GC.KeepAlive(style);
        GC.KeepAlive(retirement);
    }

    [Fact]
    public void CachePublicationFailurePreservesPrimaryAndBalancesBothReferences()
    {
        var retirement = new NativeResourceRetirement(createTimer: false);
        var disposer = new FormatDisposalProbe();
        disposer.FailPersistently();
        var primary = new InvalidOperationException("Injected cache publication failure.");
        WeakReference<SharedCanvasTextFormatCache.Entry>? entryWitness = null;
        var cache = new SharedCanvasTextFormatCache.Store(
            8_192,
            retirement,
            disposer.Dispose,
            afterEntryCreatedForTests: entry =>
                entryWitness = new WeakReference<SharedCanvasTextFormatCache.Entry>(entry),
            beforeCachePublicationForTests: () => throw primary);

        WeakReference<ElementStyle> style = AcquireAndExpectFailure(cache, primary, "Publication failure family");
        Assert.Equal((0, 0L), cache.Statistics);
        Assert.Equal(1, retirement.PendingRecordCount);
        Assert.Equal(1, retirement.PendingResourceCount);
        Assert.Single(disposer.ObservedFormats);
        AssertRetained(disposer.ObservedFormats[0]);
        WeakReference<SharedCanvasTextFormatCache.Entry> entryReference =
            Assert.IsType<WeakReference<SharedCanvasTextFormatCache.Entry>>(entryWitness);
        Assert.Equal(0, ReadReferenceCount(entryReference));
        CollectGarbage();
        AssertCollected(entryReference);
        AssertCollected(style);

        disposer.AllowDisposal();
        Assert.True(retirement.DrainOnce());
        Assert.Equal(0, retirement.PendingRecordCount);
        Assert.Equal(0, retirement.PendingResourceCount);
        GC.KeepAlive(cache);
        GC.KeepAlive(retirement);
    }

    [Fact]
    public async Task ConcurrentLastLeaseReleaseDisposesExactlyOnce()
    {
        var retirement = new NativeResourceRetirement(createTimer: false);
        var disposer = new FormatDisposalProbe();
        var cache = new SharedCanvasTextFormatCache.Store(8_192, retirement, disposer.Dispose);

        ConcurrentReleaseEvidence evidence = await ReleaseLeasesTogether(cache);

        Assert.Equal(1, disposer.AttemptCount);
        Assert.Equal(1, disposer.SuccessfulDisposalCount);
        Assert.Equal(0, retirement.PendingRecordCount);
        CollectGarbage();
        AssertCollected(evidence.Entry);
        AssertCollected(evidence.Format);
        GC.KeepAlive(cache);
        GC.KeepAlive(retirement);
    }

    [Fact]
    public void FaultInjectionCannotBeAttachedToProcessRetirementOwner()
    {
        var disposer = new FormatDisposalProbe();
        Assert.Throws<ArgumentException>(() => new SharedCanvasTextFormatCache.Store(
            1_024,
            NativeResourceRetirement.Shared,
            disposer.Dispose));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static NativeFormatEvidence CreateFailedLastRelease(
        SharedCanvasTextFormatCache.Store cache,
        FormatDisposalProbe disposer)
    {
        ElementStyle style = CreateStyle("Durable format family");
        SharedCanvasTextFormatCache.Lease lease = Acquire(cache, style);
        SharedCanvasTextFormatCache.Entry entry = Assert.IsType<SharedCanvasTextFormatCache.Entry>(lease.EntryForTests);
        CanvasTextFormat format = lease.Format;
        var evidence = new NativeFormatEvidence(
            new WeakReference<SharedCanvasTextFormatCache.Entry>(entry),
            new WeakReference<SharedCanvasTextFormatCache.Lease>(lease),
            new WeakReference<ElementStyle>(style),
            new WeakReference<CanvasTextFormat>(format));

        cache.Clear();
        Assert.Equal(1, entry.ReferenceCountForTests);
        lease.Dispose();
        Assert.Equal(1, disposer.AttemptCount);
        return evidence;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference<ElementStyle> AcquireWithInitializerFailure(
        SharedCanvasTextFormatCache.Store cache,
        Exception primary)
    {
        ElementStyle style = CreateStyle("Initializer failure family");
        var styleReference = new WeakReference<ElementStyle>(style);
        Exception observed = Assert.Throws<InvalidOperationException>(() => Acquire(cache, style));
        Assert.Same(primary, observed);
        Assert.IsType<InvalidOperationException>(observed.Data[SharedCanvasTextFormatCache.NativeCleanupFailureDataKey]);
        return styleReference;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference<ElementStyle> AcquireAndExpectFailure(
        SharedCanvasTextFormatCache.Store cache,
        Exception primary,
        string family)
    {
        ElementStyle style = CreateStyle(family);
        var styleReference = new WeakReference<ElementStyle>(style);
        Exception observed = Assert.Throws<InvalidOperationException>(() => Acquire(cache, style));
        Assert.Same(primary, observed);
        Assert.IsType<InvalidOperationException>(observed.Data[SharedCanvasTextFormatCache.NativeCleanupFailureDataKey]);
        return styleReference;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static EvictionEvidence AcquireAcrossEviction(SharedCanvasTextFormatCache.Store cache)
    {
        SharedCanvasTextFormatCache.Lease first = Acquire(cache, CreateStyle("Test Font One"));
        var firstFormat = new WeakReference<CanvasTextFormat>(first.Format);
        CanvasTextFormat originalFormat = first.Format;
        SharedCanvasTextFormatCache.Entry firstEntry = Assert.IsType<SharedCanvasTextFormatCache.Entry>(first.EntryForTests);
        SharedCanvasTextFormatCache.Lease second = Acquire(cache, CreateStyle("Test Font Two"));
        Assert.Same(originalFormat, first.Format);
        return new EvictionEvidence(first, second, firstFormat, firstEntry.ReferenceCountForTests);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task<ConcurrentReleaseEvidence> ReleaseLeasesTogether(SharedCanvasTextFormatCache.Store cache)
    {
        SharedCanvasTextFormatCache.Lease first = Acquire(cache, CreateStyle("Concurrent format family"));
        SharedCanvasTextFormatCache.Lease second = Acquire(cache, CreateStyle("Concurrent format family"));
        SharedCanvasTextFormatCache.Entry entry = Assert.IsType<SharedCanvasTextFormatCache.Entry>(first.EntryForTests);
        Assert.Same(entry, second.EntryForTests);
        CanvasTextFormat format = first.Format;
        cache.Clear();
        using var start = new ManualResetEventSlim(false);
        var firstThread = new Thread(() => { start.Wait(); first.Dispose(); });
        var secondThread = new Thread(() => { start.Wait(); second.Dispose(); });
        firstThread.Start();
        secondThread.Start();
        start.Set();
        firstThread.Join();
        secondThread.Join();

        return Task.FromResult(new ConcurrentReleaseEvidence(
            new WeakReference<SharedCanvasTextFormatCache.Entry>(entry),
            new WeakReference<CanvasTextFormat>(format)));
    }

    private static SharedCanvasTextFormatCache.Lease Acquire(
        SharedCanvasTextFormatCache.Store cache,
        ElementStyle style) => cache.Acquire(
            style,
            CanvasWordWrapping.Wrap,
            CanvasHorizontalAlignment.Left,
            FlowDirection.LeftToRight,
            "en-US",
            useContentAwareLineSpacing: false);

    private static ElementStyle CreateStyle(string family) => new()
    {
        FontFamily = family,
        FontSize = 16,
    };

    private static void CollectGarbage()
    {
        for (int iteration = 0; iteration < 3; iteration++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
    }

    private static void AssertCollected<T>(WeakReference<T> reference)
        where T : class => Assert.False(IsAlive(reference), "A released cache object remained strongly rooted.");

    private static void AssertRetained<T>(WeakReference<T> reference, string? message = null)
        where T : class => Assert.True(IsAlive(reference), message);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int ReadReferenceCount(WeakReference<SharedCanvasTextFormatCache.Entry> reference) =>
        reference.TryGetTarget(out SharedCanvasTextFormatCache.Entry? entry)
            ? entry.ReferenceCountForTests
            : -1;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool IsAlive<T>(WeakReference<T> reference)
        where T : class => reference.TryGetTarget(out _);

    private sealed record NativeFormatEvidence(
        WeakReference<SharedCanvasTextFormatCache.Entry> Entry,
        WeakReference<SharedCanvasTextFormatCache.Lease> Lease,
        WeakReference<ElementStyle> Style,
        WeakReference<CanvasTextFormat> Format);

    private sealed record EvictionEvidence(
        SharedCanvasTextFormatCache.Lease EvictedLease,
        SharedCanvasTextFormatCache.Lease RetainedLease,
        WeakReference<CanvasTextFormat> EvictedFormat,
        int EvictedEntryReferenceCount);

    private sealed record ConcurrentReleaseEvidence(
        WeakReference<SharedCanvasTextFormatCache.Entry> Entry,
        WeakReference<CanvasTextFormat> Format);

    private sealed class FormatDisposalProbe
    {
        private readonly object _gate = new();
        private readonly List<WeakReference<CanvasTextFormat>> _observed = [];
        private int _failDisposal;
        private int _attemptCount;
        private int _successfulDisposalCount;

        internal int AttemptCount => Volatile.Read(ref _attemptCount);
        internal int SuccessfulDisposalCount => Volatile.Read(ref _successfulDisposalCount);

        internal WeakReference<CanvasTextFormat>[] ObservedFormats
        {
            get { lock (_gate) return _observed.ToArray(); }
        }

        internal void FailPersistently() => Volatile.Write(ref _failDisposal, 1);
        internal void AllowDisposal() => Volatile.Write(ref _failDisposal, 0);

        internal void Dispose(IDisposable resource)
        {
            CanvasTextFormat format = Assert.IsType<CanvasTextFormat>(resource);
            Interlocked.Increment(ref _attemptCount);
            lock (_gate)
                _observed.Add(new WeakReference<CanvasTextFormat>(format));
            if (Volatile.Read(ref _failDisposal) != 0)
                throw new InvalidOperationException("Injected persistent CanvasTextFormat disposal failure.");
            format.Dispose();
            Interlocked.Increment(ref _successfulDisposalCount);
        }
    }
}
