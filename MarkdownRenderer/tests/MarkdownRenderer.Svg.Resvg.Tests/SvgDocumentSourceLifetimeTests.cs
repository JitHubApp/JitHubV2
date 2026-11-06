using System.Runtime.CompilerServices;
using MarkdownRenderer.Images;
using MarkdownRenderer.Svg.Resvg.Internal;
using Xunit;

namespace MarkdownRenderer.Svg.Resvg.Tests;

public sealed class SvgDocumentSourceLifetimeTests
{
    [Fact]
    public void DisposedRetainedSourceSnapshotDoesNotKeepItsSvgBytesAlive()
    {
        SnapshotFixture fixture = CreateSnapshotFixture(withActiveRender: false);

        fixture.Lifetime.Dispose();

        Assert.Throws<ObjectDisposedException>(() => fixture.Snapshot.GetForRender());
        ForceCollection();
        Assert.False(IsAlive(fixture.SourceBytes));
        Assert.False(IsAlive(fixture.CallerBytes));
        GC.KeepAlive(fixture.Snapshot);
        GC.KeepAlive(fixture.Lifetime);
    }

    [Fact]
    public void SourceSnapshotRemainsAliveUntilAnAdmittedRenderLeaseRetires()
    {
        SnapshotFixture fixture = CreateSnapshotFixture(withActiveRender: true);

        fixture.Lifetime.Dispose();

        Assert.True(ReferencesTrackedSource(fixture.Snapshot, fixture.SourceOwner));
        ForceCollection();
        Assert.True(IsAlive(fixture.SourceBytes));
        Assert.False(IsAlive(fixture.CallerBytes));

        fixture.RenderLease!.Dispose();

        Assert.Throws<ObjectDisposedException>(() => fixture.Snapshot.GetForRender());
        ForceCollection();
        Assert.False(IsAlive(fixture.SourceBytes));
        Assert.False(IsAlive(fixture.SourceOwner));
        GC.KeepAlive(fixture.Snapshot);
        GC.KeepAlive(fixture.Lifetime);
        GC.KeepAlive(fixture.RenderLease);
    }

    [Fact]
    public void ThrowingCancellationAndCleanupStillClearSourceAndReleaseDelegate()
    {
        ThrowingLifetimeFixture fixture = CreateThrowingLifetimeFixture();
        using CancellationTokenRegistration registration = fixture.RenderLease.Token.Register(
            static state =>
            {
                int[] counts = (int[])state!;
                Interlocked.Increment(ref counts[0]);
                throw new InvalidOperationException("Injected cancellation callback failure.");
            },
            fixture.CallCounts);

        Assert.Throws<AggregateException>(() => fixture.Lifetime.Dispose());
        Assert.Equal(1, Volatile.Read(ref fixture.CallCounts[0]));
        Assert.True(IsAlive(fixture.SourceBytes));

        Assert.Throws<InvalidOperationException>(() => fixture.RenderLease.Dispose());
        Assert.Equal(1, Volatile.Read(ref fixture.CallCounts[1]));
        fixture.Lifetime.Dispose();

        ForceCollection();
        Assert.False(IsAlive(fixture.SourceBytes));
        GC.KeepAlive(fixture.Lifetime);
        GC.KeepAlive(fixture.RenderLease);
        GC.KeepAlive(registration);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static SnapshotFixture CreateSnapshotFixture(bool withActiveRender)
    {
        byte[] callerBytes = new byte[512];
        byte[] preflightBytes = new byte[4096];
        SvgSourceBuffer sourceBuffer = SvgSourceBuffer.TakeOwnedArray(preflightBytes);
        var preflight = new SvgPreflightResult(
            sourceBuffer,
            new byte[32],
            new MarkdownSvgDocumentInfo(IntrinsicWidthDips: 16, IntrinsicHeightDips: 16),
            StructuralCost: 1,
            EmbeddedBytes: 0,
            EmbeddedPixels: 0,
            FilterPrimitives: 0);
        var request = new MarkdownSvgOpenRequest(callerBytes);
        var snapshot = new SvgDocumentSourceSnapshot(preflight, request);
        var weakSourceOwner = new WeakReference<SvgSourceBuffer>(sourceBuffer);
        var weakSourceBytes = new WeakReference<byte[]>(preflightBytes);
        var weakCallerBytes = new WeakReference<byte[]>(callerBytes);

        SvgDocumentRenderSnapshot initial = snapshot.GetForRender();
        if (!ReferenceEquals(initial.Preflight.Source, sourceBuffer) ||
            !initial.OpenRequest.SanitizedSvgBytes.IsEmpty)
        {
            throw new InvalidOperationException("The render snapshot did not retain exactly one non-aliased source owner.");
        }

        GC.KeepAlive(callerBytes);
        var lifetime = new SvgDocumentLifetime(snapshot.Release);
        SvgDocumentLifetime.RenderLease? renderLease = withActiveRender
            ? lifetime.TryEnterRender()
            : null;
        return new SnapshotFixture(
            snapshot, lifetime, renderLease, weakSourceOwner, weakSourceBytes, weakCallerBytes);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ThrowingLifetimeFixture CreateThrowingLifetimeFixture()
    {
        byte[] sourceBytes = new byte[4096];
        var weakBytes = new WeakReference<byte[]>(sourceBytes);
        int[] callCounts = new int[2];
        Action releaseResources = () =>
        {
            Interlocked.Increment(ref callCounts[1]);
            GC.KeepAlive(sourceBytes);
            throw new InvalidOperationException("Injected cleanup failure.");
        };

        var lifetime = new SvgDocumentLifetime(releaseResources);
        SvgDocumentLifetime.RenderLease renderLease = lifetime.TryEnterRender()!;
        return new ThrowingLifetimeFixture(lifetime, renderLease, weakBytes, callCounts);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ForceCollection()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool ReferencesTrackedSource(
        SvgDocumentSourceSnapshot snapshot,
        WeakReference<SvgSourceBuffer> sourceBuffer) =>
        sourceBuffer.TryGetTarget(out SvgSourceBuffer? source) &&
        ReferenceEquals(source, snapshot.GetForRender().Preflight.Source);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool IsAlive<T>(WeakReference<T> target) where T : class => target.TryGetTarget(out _);

    private sealed record SnapshotFixture(
        SvgDocumentSourceSnapshot Snapshot,
        SvgDocumentLifetime Lifetime,
        SvgDocumentLifetime.RenderLease? RenderLease,
        WeakReference<SvgSourceBuffer> SourceOwner,
        WeakReference<byte[]> SourceBytes,
        WeakReference<byte[]> CallerBytes);

    private sealed record ThrowingLifetimeFixture(
        SvgDocumentLifetime Lifetime,
        SvgDocumentLifetime.RenderLease RenderLease,
        WeakReference<byte[]> SourceBytes,
        int[] CallCounts);
}
