using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.CompilerServices;
using MarkdownRenderer.Controls;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

public sealed class WeakSnapshotPublicationOwnershipTests
{
    [Fact]
    public void DiscardRunsOwnedCleanupExactlyOnceAndPreventsPublication()
    {
        object snapshot = new();
        int publications = 0;
        int cleanups = 0;
        var publication = new WeakSnapshotPublication<object>(
            snapshot,
            _ => publications++,
            () => cleanups++);

        publication.Discard();
        publication.Discard();
        publication.Publish();

        Assert.Equal(0, publications);
        Assert.Equal(1, cleanups);
        GC.KeepAlive(snapshot);
    }

    [Fact]
    public void PublishWinnerDoesNotDiscardAndReentrantCallsCannotReplayEnvelope()
    {
        object snapshot = new();
        int publications = 0;
        int cleanups = 0;
        WeakSnapshotPublication<object>? publication = null;
        publication = new WeakSnapshotPublication<object>(
            snapshot,
            delivered =>
            {
                Assert.Same(snapshot, delivered);
                publications++;
                publication!.Publish();
                publication!.Discard();
            },
            () => cleanups++);

        publication.Publish();
        publication.Discard();

        Assert.Equal(1, publications);
        Assert.Equal(0, cleanups);
        GC.KeepAlive(snapshot);
    }

    [Fact]
    public async Task PublishAndDiscardRaceHasOneWholeEnvelopeWinner()
    {
        for (int iteration = 0; iteration < 128; iteration++)
        {
            object snapshot = new();
            using var start = new ManualResetEventSlim();
            int publications = 0;
            int cleanups = 0;
            var publication = new WeakSnapshotPublication<object>(
                snapshot,
                delivered =>
                {
                    Assert.Same(snapshot, delivered);
                    Interlocked.Increment(ref publications);
                },
                () => Interlocked.Increment(ref cleanups));

            Task publish = Task.Run(() =>
            {
                start.Wait();
                publication.Publish();
            });
            Task discard = Task.Run(() =>
            {
                start.Wait();
                publication.Discard();
            });

            start.Set();
            await Task.WhenAll(publish, discard).WaitAsync(TimeSpan.FromSeconds(5));

            Assert.InRange(publications, 0, 1);
            Assert.InRange(cleanups, 0, 1);
            Assert.Equal(1, publications + cleanups);
            GC.KeepAlive(snapshot);
        }
    }

    [Fact]
    public void PublishPreservesWeakSnapshotIdentityWithoutKeepingItAlive()
    {
        (WeakSnapshotPublication<object> publication,
            WeakReference<object> snapshotReference,
            List<object?> delivered,
            int[] cleanups) = CreatePublicationForCollectedSnapshot();

        for (int attempt = 0; attempt < 3 && IsAlive(snapshotReference); attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

        Assert.False(IsAlive(snapshotReference));
        publication.Publish();

        Assert.Collection(delivered, value => Assert.Null(value));
        Assert.Equal(0, Volatile.Read(ref cleanups[0]));
    }

    [Fact]
    public void PublishCallbackExceptionDoesNotReplayOrInvokeDiscardCleanup()
    {
        object snapshot = new();
        var expected = new InvalidOperationException("publish callback failed");
        int publications = 0;
        int cleanups = 0;
        var publication = new WeakSnapshotPublication<object>(
            snapshot,
            _ =>
            {
                publications++;
                throw expected;
            },
            () => cleanups++);

        InvalidOperationException observed = Assert.Throws<InvalidOperationException>(publication.Publish);
        publication.Publish();
        publication.Discard();

        Assert.Same(expected, observed);
        Assert.Equal(1, publications);
        Assert.Equal(0, cleanups);
        GC.KeepAlive(snapshot);
    }

    [Fact]
    public void DiscardCleanupExceptionCannotReplayTheEnvelope()
    {
        object snapshot = new();
        var expected = new InvalidOperationException("discard cleanup failed");
        int publications = 0;
        int cleanups = 0;
        var publication = new WeakSnapshotPublication<object>(
            snapshot,
            _ => publications++,
            () =>
            {
                cleanups++;
                throw expected;
            });

        InvalidOperationException observed = Assert.Throws<InvalidOperationException>(publication.Discard);
        publication.Discard();
        publication.Publish();

        Assert.Same(expected, observed);
        Assert.Equal(1, cleanups);
        Assert.Equal(0, publications);
        GC.KeepAlive(snapshot);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool IsAlive(WeakReference<object> reference) => reference.TryGetTarget(out _);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (
        WeakSnapshotPublication<object> Publication,
        WeakReference<object> SnapshotReference,
        List<object?> Delivered,
        int[] Cleanups) CreatePublicationForCollectedSnapshot()
    {
        object snapshot = new();
        var snapshotReference = new WeakReference<object>(snapshot);
        var delivered = new List<object?>();
        var cleanups = new int[1];
        var publication = new WeakSnapshotPublication<object>(
            snapshot,
            delivered.Add,
            () => Interlocked.Increment(ref cleanups[0]));
        return (publication, snapshotReference, delivered, cleanups);
    }
}
