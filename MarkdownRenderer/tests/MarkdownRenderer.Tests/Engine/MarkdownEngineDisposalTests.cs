using MarkdownRenderer.Extensions;
using MarkdownRenderer.Extensions.Internal;
using MarkdownRenderer.Tests.TestSupport;
using Xunit;

namespace MarkdownRenderer.Tests.Engine;

/// <summary>
/// Checks engine disposal: work is canceled before owned extension
/// resources are released, resources outlive concurrent and looked-up
/// callbacks, and the default engine stays usable.
/// </summary>
public sealed class MarkdownEngineDisposalTests
{
    [Fact]
    public void CallbackLifetimeReportsRetirementWhenLastLeaseExits()
    {
        var lifetime = new MarkdownExtensionCallbackLifetime();
        MarkdownExtensionCallbackLifetime.Lease lease = lifetime.Enter();
        Task retirement = lifetime.BeginDispose();

        Assert.False(lifetime.IsRetired);
        lease.Dispose();

        Assert.True(lifetime.IsRetired);
        Assert.True(retirement.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task EngineDisposalCancelsWorkBeforeReleasingOwnedExtensionResources()
    {
        int callbackExited = 0;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resources = new List<TrackingDisposable>();
        var resourceObservedCallbackExit = new List<int>();
        var builder = new MarkdownEngineBuilder();
        builder.UseOwnedExtensionFactory(
            "tests.owned-resource",
            () =>
            {
                int resourceIndex = resources.Count;
                var resource = new TrackingDisposable(() =>
                    resourceObservedCallbackExit[resourceIndex] = Volatile.Read(ref callbackExited));
                resources.Add(resource);
                resourceObservedCallbackExit.Add(0);
                return new MarkdownOwnedExtension(
                    new ConfigurableExtension("tests.owned-resource", extensionBuilder =>
                        extensionBuilder.RegisterBlockAsync(
                            MarkdownSyntaxKinds.Block.Paragraph,
                            async (context, _) =>
                            {
                                entered.TrySetResult();
                                try
                                {
                                    await Task.Delay(Timeout.InfiniteTimeSpan, context.CancellationToken);
                                }
                                finally
                                {
                                    Interlocked.Exchange(ref callbackExited, 1);
                                }
                            })),
                    resource);
            });
        MarkdownEngine engine = builder.Build();
        MarkdownEngine derived = new MarkdownEngineBuilder()
            .UseExtensions(engine.Extensions)
            .Build();
        Assert.Equal(2, resources.Count);
        Task parse = engine.ParseAsync("active");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        engine.Dispose();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => parse.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(SpinWait.SpinUntil(() => resources[0].IsDisposed, TimeSpan.FromSeconds(5)));
        Assert.False(resources[1].IsDisposed);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => engine.ParseAsync("later"));

        derived.Dispose();
        Assert.True(SpinWait.SpinUntil(() => resources[1].IsDisposed, TimeSpan.FromSeconds(5)));
        Assert.Equal(1, resourceObservedCallbackExit[0]);
    }

    [Fact]
    public async Task PublicOwnedExtensionFactoryDefersResourceDisposalUntilConcurrentCallbacksRetire()
    {
        int activeCallbacks = 0;
        int activeCallbacksWhenDisposed = -1;
        var bothEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        TrackingDisposable? resource = null;

        MarkdownEngine engine = new MarkdownEngineBuilder()
            .UseOwnedExtensionFactory(
                "tests.public-owned-resource",
                () =>
                {
                    resource = new TrackingDisposable(() =>
                        activeCallbacksWhenDisposed = Volatile.Read(ref activeCallbacks));
                    return new MarkdownOwnedExtension(
                        new ConfigurableExtension(
                            "tests.public-owned-resource",
                            extensionBuilder => extensionBuilder.RegisterBlockAsync(
                                MarkdownSyntaxKinds.Block.Paragraph,
                                async (context, _) =>
                                {
                                    if (Interlocked.Increment(ref activeCallbacks) == 2)
                                        bothEntered.TrySetResult();
                                    try
                                    {
                                        await release.Task.ConfigureAwait(false);
                                        context.CancellationToken.ThrowIfCancellationRequested();
                                    }
                                    finally
                                    {
                                        Interlocked.Decrement(ref activeCallbacks);
                                    }
                                })),
                        resource);
                })
            .WithParseLimits(new MarkdownParseLimits(
                maximumSourceLength: 100,
                maximumConcurrentParseCount: 2,
                maximumOutstandingParseCount: 2,
                maximumOutstandingSourceBytes: 200))
            .Build();

        Task first = engine.ParseAsync("first");
        Task second = engine.ParseAsync("second");
        try
        {
            await bothEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

            engine.Dispose();

            TrackingDisposable ownedResource = Assert.IsType<TrackingDisposable>(resource);
            Assert.False(ownedResource.IsDisposed);
            Assert.Equal(2, Volatile.Read(ref activeCallbacks));

            release.TrySetResult();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.True(SpinWait.SpinUntil(
                () => ownedResource.IsDisposed,
                TimeSpan.FromSeconds(5)));
            Assert.Equal(0, activeCallbacksWhenDisposed);
        }
        finally
        {
            release.TrySetResult();
            engine.Dispose();
        }
    }

    [Fact]
    public async Task PubliclyLookedUpOwnedCallbackKeepsResourceAliveThroughEngineDisposal()
    {
        var entered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        TrackingDisposable? resource = null;
        MarkdownEngine engine = new MarkdownEngineBuilder()
            .UseOwnedExtensionFactory(
                "tests.direct-owned-callback",
                () =>
                {
                    resource = new TrackingDisposable(static () => { });
                    return new MarkdownOwnedExtension(
                        new ConfigurableExtension(
                            "tests.direct-owned-callback",
                            extensionBuilder => extensionBuilder.RegisterBlockAsync(
                                MarkdownSyntaxKinds.Block.Paragraph,
                                async (_, _) =>
                                {
                                    entered.TrySetResult();
                                    await release.Task.ConfigureAwait(false);
                                })),
                        resource);
                })
            .Build();

        Assert.True(engine.Extensions.TryGetAsyncBlockRenderer(
            MarkdownSyntaxKinds.Block.Paragraph,
            out MarkdownAsyncNodeRenderer? renderer));
        var context = new MarkdownExtensionContext(new MarkdownSyntaxNode(
            MarkdownSyntaxKinds.Block.Paragraph,
            new SourceSpan(0, 1),
            literal: "x"));
        var content = new MarkdownContentBuilder();
        Task callback = renderer!(context, content).AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        engine.Dispose();

        TrackingDisposable ownedResource = Assert.IsType<TrackingDisposable>(resource);
        Assert.False(ownedResource.IsDisposed);
        release.TrySetResult();
        await callback.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(SpinWait.SpinUntil(
            () => ownedResource.IsDisposed,
            TimeSpan.FromSeconds(5)));

        await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
            await renderer!(context, new MarkdownContentBuilder()).AsTask());
    }

    [Fact]
    public async Task EngineDisposalDoesNotWaitForCancellationRegistrationsAndRetiresResourcesAfterThem()
    {
        using var blocking = new CancellationBlockingParagraphRenderer();
        TrackingDisposable? resource = null;

        var builder = new MarkdownEngineBuilder();
        builder.UseOwnedExtensionFactory(
            "tests.dispose-blocking-cancellation",
            () =>
            {
                resource = new TrackingDisposable(() => { });
                return new MarkdownOwnedExtension(
                    new ConfigurableExtension("tests.dispose-blocking-cancellation", blocking.Register),
                    resource);
            });
        MarkdownEngine engine = builder.Build();
        Task parse = engine.ParseAsync("active");
        await blocking.CallbackRegistered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Task dispose = Task.Run(engine.Dispose);
        try
        {
            await dispose.WaitAsync(TimeSpan.FromSeconds(1));
            // CancelAsync deliberately queues registrations away from the
            // disposing thread. A saturated hosted runner may take longer
            // than two seconds to inject that worker even though Dispose has
            // already met its bounded-return contract above. This wait checks
            // eventual ordering, not callback-start latency.
            await blocking.CallbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(15));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => parse.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.NotNull(resource);
            Assert.False(resource.IsDisposed);
        }
        finally
        {
            blocking.CallbackRelease.Set();
        }

        await blocking.CallbackExited.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(SpinWait.SpinUntil(
            () => resource!.IsDisposed,
            TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task DefaultRemainsUsableButOrdinaryUnownedEngineIsDisposed()
    {
        MarkdownEngine.Default.Dispose();
        var unowned = new MarkdownEngineBuilder().Build();
        unowned.Dispose();

        Assert.NotNull(await MarkdownEngine.Default.ParseAsync("default"));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => unowned.ParseAsync("unowned"));
    }
}
