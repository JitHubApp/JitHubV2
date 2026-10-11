using System.Runtime.CompilerServices;
using MarkdownRenderer.Svg.Resvg.Internal;
using Xunit;

namespace MarkdownRenderer.Svg.Resvg.Tests;

public sealed class SvgRendererDocumentRegistryTests
{
    [Fact]
    public void CloseReturnsRegisteredLifetimesAndRejectsLaterRegistration()
    {
        var registry = new SvgRendererDocumentRegistry();
        var registered = new SvgDocumentLifetime(static () => { });
        var late = new SvgDocumentLifetime(static () => { });

        Assert.True(registry.TryRegister(registered));
        Assert.True(registry.TryRegister(registered));

        IReadOnlyList<SvgDocumentLifetime> snapshot = registry.Close();

        Assert.Same(registered, Assert.Single(snapshot));
        Assert.False(registry.TryRegister(late));
        Assert.Empty(registry.Close());

        registered.Dispose();
        late.Dispose();
    }

    [Fact]
    public void AlreadyDisposedAndDisposalRequestedLifetimesRemainSafeToRegister()
    {
        var registry = new SvgRendererDocumentRegistry();
        int disposedReleaseCount = 0;
        int drainingReleaseCount = 0;
        var alreadyDisposed = new SvgDocumentLifetime(
            () => Interlocked.Increment(ref disposedReleaseCount));
        alreadyDisposed.Dispose();

        var draining = new SvgDocumentLifetime(
            () => Interlocked.Increment(ref drainingReleaseCount));
        SvgDocumentLifetime.RenderLease lease = draining.TryEnterRender()!;
        draining.Dispose();
        Assert.Equal(0, drainingReleaseCount);

        // Registry membership is governed only by Close. Disposed entries are
        // harmless because shutdown disposal is idempotent and render drains
        // still own their existing retirement barriers.
        Assert.True(registry.TryRegister(alreadyDisposed));
        Assert.True(registry.TryRegister(draining));
        IReadOnlyList<SvgDocumentLifetime> snapshot = registry.Close();

        Assert.Contains(alreadyDisposed, snapshot);
        Assert.Contains(draining, snapshot);
        alreadyDisposed.Dispose();
        draining.Dispose();
        Assert.Equal(1, disposedReleaseCount);
        Assert.Equal(0, drainingReleaseCount);

        lease.Dispose();
        Assert.Equal(1, drainingReleaseCount);
    }

    [Fact]
    public void OpenRegistryDoesNotRootDocumentOrLifetimeGraphs()
    {
        var registry = new SvgRendererDocumentRegistry();
        (WeakReference document, WeakReference lifetime) = RegisterDocumentGraph(registry);

        ForceFullCollection();

        Assert.False(document.IsAlive);
        Assert.False(lifetime.IsAlive);
        GC.KeepAlive(registry);
        Assert.Empty(registry.Close());
    }

    [Fact]
    public async Task ConcurrentRegistrationAndCloseHaveDisjointOutcomes()
    {
        const int rounds = 12;
        const int registrationCount = 24;

        for (int round = 0; round < rounds; round++)
        {
            var registry = new SvgRendererDocumentRegistry();
            SvgDocumentLifetime[] lifetimes = Enumerable.Range(0, registrationCount)
                .Select(static _ => new SvgDocumentLifetime(static () => { }))
                .ToArray();
            using var ready = new CountdownEvent(registrationCount + 1);
            var start = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            Task<bool>[] registrations = lifetimes.Select(lifetime => Task.Run(async () =>
            {
                ready.Signal();
                await start.Task.ConfigureAwait(false);
                return registry.TryRegister(lifetime);
            })).ToArray();
            Task<IReadOnlyList<SvgDocumentLifetime>> close = Task.Run(async () =>
            {
                ready.Signal();
                await start.Task.ConfigureAwait(false);
                return registry.Close();
            });

            Assert.True(ready.Wait(TimeSpan.FromSeconds(10)), "Concurrent operations did not reach the start gate.");
            start.SetResult(true);

            bool[] accepted = await Task.WhenAll(registrations).WaitAsync(TimeSpan.FromSeconds(10));
            IReadOnlyList<SvgDocumentLifetime> snapshot = await close.WaitAsync(TimeSpan.FromSeconds(10));
            var snapshotSet = new HashSet<SvgDocumentLifetime>(snapshot, ReferenceEqualityComparer.Instance);

            for (int index = 0; index < lifetimes.Length; index++)
            {
                Assert.Equal(accepted[index], snapshotSet.Contains(lifetimes[index]));
                Assert.False(registry.TryRegister(lifetimes[index]));
            }

            foreach (SvgDocumentLifetime lifetime in lifetimes)
                lifetime.Dispose();
        }
    }

    [Fact]
    public async Task CloseDoesNotRunCancellationOrReleaseCallbacksUnderItsGate()
    {
        var registry = new SvgRendererDocumentRegistry();
        var late = new SvgDocumentLifetime(static () => { });
        int cancellationCallbacks = 0;
        int releaseCallbacks = 0;
        var lifetime = new SvgDocumentLifetime(() =>
        {
            Interlocked.Increment(ref releaseCallbacks);
            AssertRegistryCanBeReenteredFromAnotherThread(registry, late);
        });
        SvgDocumentLifetime.RenderLease lease = lifetime.TryEnterRender()!;
        using CancellationTokenRegistration cancellation = lease.Token.Register(() =>
        {
            Interlocked.Increment(ref cancellationCallbacks);
            AssertRegistryCanBeReenteredFromAnotherThread(registry, late);
        });
        Assert.True(registry.TryRegister(lifetime));

        IReadOnlyList<SvgDocumentLifetime> snapshot = registry.Close();

        Assert.Same(lifetime, Assert.Single(snapshot));
        Assert.Equal(0, cancellationCallbacks);
        Assert.Equal(0, releaseCallbacks);

        await Task.Run(lifetime.Dispose).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, cancellationCallbacks);
        Assert.Equal(0, releaseCallbacks);

        lease.Dispose();
        Assert.Equal(1, releaseCallbacks);
        late.Dispose();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (WeakReference Document, WeakReference Lifetime) RegisterDocumentGraph(
        SvgRendererDocumentRegistry registry)
    {
        var document = new DocumentLifetimeOwner();
        var lifetime = new SvgDocumentLifetime(() => GC.KeepAlive(document));
        document.Lifetime = lifetime;
        Assert.True(registry.TryRegister(lifetime));
        return (new WeakReference(document), new WeakReference(lifetime));
    }

    private static void ForceFullCollection()
    {
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
    }

    private static void AssertRegistryCanBeReenteredFromAnotherThread(
        SvgRendererDocumentRegistry registry,
        SvgDocumentLifetime late)
    {
        Task<bool> registration = Task.Run(() => registry.TryRegister(late));
        Assert.True(registration.Wait(TimeSpan.FromSeconds(3)), "A registry callback ran while its gate was held.");
        Assert.False(registration.GetAwaiter().GetResult());
    }

    private sealed class DocumentLifetimeOwner
    {
        internal SvgDocumentLifetime? Lifetime { get; set; }
    }
}
