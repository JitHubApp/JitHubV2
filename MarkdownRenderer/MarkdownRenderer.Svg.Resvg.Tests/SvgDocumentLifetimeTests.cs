using MarkdownRenderer.Svg.Resvg.Internal;
using Xunit;

namespace MarkdownRenderer.Svg.Resvg.Tests;

public sealed class SvgDocumentLifetimeTests
{
    [Fact]
    public async Task ConcurrentDuplicateLeaseRetirementReleasesDocumentExactlyOnce()
    {
        int releases = 0;
        var lifetime = new SvgDocumentLifetime(() => Interlocked.Increment(ref releases));
        SvgDocumentLifetime.RenderLease[] leases = Enumerable.Range(0, 64)
            .Select(_ => lifetime.TryEnterRender()!).ToArray();
        lifetime.Dispose();
        Assert.Null(lifetime.TryEnterRender());

        // All but one real ticket may retire in any order and may be disposed
        // repeatedly; the last retained ticket still owns the document.
        await Task.WhenAll(leases.Skip(1).Select(lease => Task.Run(() =>
        {
            lease.Dispose();
            lease.Dispose();
        }))).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(lifetime.RetirementTask.IsCompleted);
        Assert.Equal(0, Volatile.Read(ref releases));

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(leases[0].Dispose)))
            .WaitAsync(TimeSpan.FromSeconds(10));
        await lifetime.RetirementTask.WaitAsync(TimeSpan.FromSeconds(10));
        lifetime.Dispose();
        Assert.Equal(1, Volatile.Read(ref releases));
    }

    [Fact]
    public async Task RetirementBarrierCompletesOnlyAfterFinalActualRenderCleanup()
    {
        int releases = 0;
        var lifetime = new SvgDocumentLifetime(() => Interlocked.Increment(ref releases));
        SvgDocumentLifetime.RenderLease first = lifetime.TryEnterRender()!;
        SvgDocumentLifetime.RenderLease second = lifetime.TryEnterRender()!;

        lifetime.Dispose();
        first.Dispose();

        Assert.False(lifetime.RetirementTask.IsCompleted);
        Assert.Equal(0, releases);
        second.Dispose();
        await lifetime.RetirementTask.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, releases);
    }

    [Fact]
    public async Task RetirementBarrierReportsDeferredCleanupFailureAfterDrain()
    {
        var expected = new InvalidOperationException("Injected final cleanup failure.");
        var lifetime = new SvgDocumentLifetime(() => throw expected);
        SvgDocumentLifetime.RenderLease lease = lifetime.TryEnterRender()!;

        lifetime.Dispose();
        Assert.False(lifetime.RetirementTask.IsCompleted);
        Assert.Same(expected, Assert.Throws<InvalidOperationException>(lease.Dispose));
        Assert.Same(expected, await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await lifetime.RetirementTask));
        lifetime.Dispose();
    }

    [Fact]
    public async Task RetirementBarrierIncludesCancellationFailureAfterActiveRenderDrains()
    {
        var lifetime = new SvgDocumentLifetime(static () => { });
        SvgDocumentLifetime.RenderLease lease = lifetime.TryEnterRender()!;
        using CancellationTokenRegistration registration = lease.Token.Register(
            static () => throw new InvalidOperationException("Injected cancellation failure."));

        Assert.Throws<AggregateException>(lifetime.Dispose);
        Assert.False(lifetime.RetirementTask.IsCompleted);
        lease.Dispose();
        AggregateException failure = await Assert.ThrowsAsync<AggregateException>(
            async () => await lifetime.RetirementTask);
        Assert.IsType<InvalidOperationException>(Assert.Single(failure.InnerExceptions));
    }

    [Fact]
    public void FinalRenderCanRetireReentrantlyDuringCancellation()
    {
        int releases = 0;
        var lifetime = new SvgDocumentLifetime(() => Interlocked.Increment(ref releases));
        SvgDocumentLifetime.RenderLease lease = lifetime.TryEnterRender()!;
        using CancellationTokenRegistration registration = lease.Token.Register(() =>
        {
            lease.Dispose();
            Assert.Equal(0, Volatile.Read(ref releases));
        });

        lifetime.Dispose();

        Assert.Equal(1, releases);
        Assert.Null(lifetime.TryEnterRender());
    }

    [Fact]
    public async Task FinalRenderExitWaitsUntilConcurrentCancellationCallbackReturns()
    {
        int releases = 0;
        using var callbackEntered = new ManualResetEventSlim();
        using var allowCallbackToReturn = new ManualResetEventSlim();
        var lifetime = new SvgDocumentLifetime(() => Interlocked.Increment(ref releases));
        SvgDocumentLifetime.RenderLease lease = lifetime.TryEnterRender()!;
        using CancellationTokenRegistration registration = lease.Token.Register(() =>
        {
            callbackEntered.Set();
            allowCallbackToReturn.Wait();
        });

        Task dispose = Task.Run(lifetime.Dispose);
        try
        {
            Assert.True(callbackEntered.Wait(TimeSpan.FromSeconds(10)), "Cancellation callback did not start.");
            lease.Dispose();
            Assert.Equal(0, releases);
        }
        finally
        {
            allowCallbackToReturn.Set();
        }

        await dispose.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, releases);
    }

    [Fact]
    public void RepeatedDisposeAndLateRenderAdmissionAreSafe()
    {
        int releases = 0;
        var lifetime = new SvgDocumentLifetime(() => Interlocked.Increment(ref releases));

        lifetime.Dispose();
        lifetime.Dispose();

        Assert.Equal(1, releases);
        Assert.Null(lifetime.TryEnterRender());
    }

    [Fact]
    public void MultipleRenderLeasesDelayCleanupUntilTheLastLeaseExits()
    {
        int releases = 0;
        var lifetime = new SvgDocumentLifetime(() => Interlocked.Increment(ref releases));
        SvgDocumentLifetime.RenderLease first = lifetime.TryEnterRender()!;
        SvgDocumentLifetime.RenderLease second = lifetime.TryEnterRender()!;
        SvgDocumentLifetime.RenderLease third = lifetime.TryEnterRender()!;

        lifetime.Dispose();

        Assert.True(first.Token.IsCancellationRequested);
        Assert.True(second.Token.IsCancellationRequested);
        Assert.True(third.Token.IsCancellationRequested);
        first.Dispose();
        second.Dispose();
        Assert.Equal(0, releases);

        third.Dispose();

        Assert.Equal(1, releases);
        lifetime.Dispose();
        Assert.Equal(1, releases);
    }

    [Fact]
    public void ThrowingCancellationCallbackStillCompletesCleanup()
    {
        int releases = 0;
        var lifetime = new SvgDocumentLifetime(() => Interlocked.Increment(ref releases));
        SvgDocumentLifetime.RenderLease lease = lifetime.TryEnterRender()!;
        using CancellationTokenRegistration registration = lease.Token.Register(
            static () => throw new InvalidOperationException("Injected cancellation callback failure."));
        lease.Dispose();

        AggregateException exception = Assert.Throws<AggregateException>(lifetime.Dispose);

        Assert.IsType<InvalidOperationException>(Assert.Single(exception.InnerExceptions));
        Assert.Equal(1, releases);
        Assert.Null(lifetime.TryEnterRender());
    }

    [Fact]
    public async Task CleanupCallbackRunsOutsideTheLifetimeLock()
    {
        int releases = 0;
        SvgDocumentLifetime? lifetime = null;
        lifetime = new SvgDocumentLifetime(() =>
        {
            Interlocked.Increment(ref releases);
            Task probe = Task.Run(() =>
            {
                Assert.Null(lifetime!.TryEnterRender());
                lifetime.Dispose();
            });
            Assert.True(probe.Wait(TimeSpan.FromSeconds(5)), "Cleanup held the lifetime lock.");
        });

        await Task.Run(lifetime.Dispose).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(1, releases);
    }
}
