using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Markdig.Extensions.Tables;
using MarkdownRenderer.Extensions;
using MarkdownRenderer.Theming;
using System.Text;
using Xunit;

namespace MarkdownRenderer.Tests;

public sealed class MarkdownEngineTests
{
    [Fact]
    public void BuiltInProfiles_AreExplicitAndStandardsVersioned()
    {
        Assert.Equal("commonmark-0.31.2", MarkdownProfiles.CommonMark.Id);
        Assert.Equal("https://spec.commonmark.org/0.31.2/", MarkdownProfiles.CommonMark.Specification);
        Assert.Equal("gfm-0.29", MarkdownProfiles.GfmStrict.Id);
        Assert.Equal("https://github.github.com/gfm/", MarkdownProfiles.GfmStrict.Specification);
        Assert.Contains("task-lists", MarkdownProfiles.GfmStrict.Features);
        Assert.Contains("disallowed-raw-html", MarkdownProfiles.GfmStrict.Features);
        Assert.Contains("footnotes", MarkdownProfiles.GitHubReadme.Features);
        Assert.Contains("definition-lists", MarkdownProfiles.MarkdownExtra.Features);
    }

    [Fact]
    public async Task ParseAsync_ReturnsReusableImmutableUtf16Document()
    {
        const string source = "😀 prefix\n\n# Heading\n\n[link](https://example.test)";
        MarkdownEngine engine = new MarkdownEngineBuilder()
            .UseProfile(MarkdownProfiles.CommonMark)
            .Build();

        var document = await engine.ParseAsync(source);

        Assert.Equal(source, document.Source);
        Assert.NotNull(document.ParsedDocument);
        Assert.Single(document.GetHeadings());
        var link = Assert.Single(document.GetLinks());
        Assert.Equal(source.IndexOf("[link]", StringComparison.Ordinal), link.SourceSpan.Start);
        Assert.Equal("[link](https://example.test)", source.Substring(link.SourceSpan.Start, link.SourceSpan.Length));
    }

    [Fact]
    public async Task StrictProfiles_DoNotExposeRawHtmlNodes()
    {
        const string source = "<div>unsafe</div>";
        var commonMark = await new MarkdownEngineBuilder()
            .UseProfile(MarkdownProfiles.CommonMark)
            .Build()
            .ParseAsync(source);
        var gfm = await new MarkdownEngineBuilder()
            .UseProfile(MarkdownProfiles.GfmStrict)
            .Build()
            .ParseAsync(source);

        Assert.DoesNotContain(commonMark.ParsedDocument!, static block => block is HtmlBlock);
        Assert.DoesNotContain(gfm.ParsedDocument!, static block => block is HtmlBlock);
    }

    [Fact]
    public async Task ParseAsync_ObservesPreCanceledToken()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            MarkdownEngine.Default.ParseAsync("# ignored", cancellation.Token));
    }

    [Fact]
    public void ParseCacheBudget_IsValidatedAndFrozenIntoEngine()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new MarkdownEngineBuilder().WithParseCacheBudgetBytes(-1));

        var engine = new MarkdownEngineBuilder()
            .WithParseCacheBudgetBytes(4096)
            .Build();

        Assert.Equal(4096, engine.ParseCacheBudgetBytes);
    }

    [Fact]
    public void ParseLimits_AreValidatedAndFrozenIntoEngine()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new MarkdownParseLimits(maximumConcurrentParseCount: 2, maximumOutstandingParseCount: 1));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new MarkdownParseLimits(maximumSourceLength: 100, maximumOutstandingSourceBytes: 199));

        var limits = new MarkdownParseLimits(
            maximumSourceLength: 100,
            maximumConcurrentParseCount: 1,
            maximumOutstandingParseCount: 3,
            maximumOutstandingSourceBytes: 200);
        MarkdownEngine engine = new MarkdownEngineBuilder().WithParseLimits(limits).Build();

        Assert.Same(limits, engine.ParseLimits);
    }

    [Fact]
    public async Task ParseAsync_RejectsSourceBeyondTheConfiguredMaximum()
    {
        var engine = new MarkdownEngineBuilder()
            .WithParseLimits(new MarkdownParseLimits(
                maximumSourceLength: 4,
                maximumConcurrentParseCount: 1,
                maximumOutstandingParseCount: 1,
                maximumOutstandingSourceBytes: 8))
            .Build();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => engine.ParseAsync("12345"));
        Assert.Equal(0, engine.OutstandingParseCount);
    }

    [Fact]
    public async Task ParseAsync_BoundsUniqueWorkAndStillDeduplicatesQueuedSources()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var engine = new MarkdownEngineBuilder()
            .UseExtension(new ConfigurableExtension("tests.bounded-async", builder =>
                builder.RegisterBlockAsync(MarkdownSyntaxKinds.Block.Paragraph, async (context, _) =>
                {
                    entered.TrySetResult();
                    await release.Task.WaitAsync(context.CancellationToken);
                })))
            .WithParseCacheBudgetBytes(0)
            .WithParseLimits(new MarkdownParseLimits(
                maximumSourceLength: 100,
                maximumConcurrentParseCount: 1,
                maximumOutstandingParseCount: 3,
                maximumOutstandingSourceBytes: 200))
            .Build();

        Task<MarkdownRenderer.Document.MarkdownDocument> first = engine.ParseAsync("one");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task<MarkdownRenderer.Document.MarkdownDocument> second = engine.ParseAsync("two");
        Task<MarkdownRenderer.Document.MarkdownDocument> duplicate = engine.ParseAsync("two");
        Task<MarkdownRenderer.Document.MarkdownDocument> third = engine.ParseAsync("three");

        Assert.Equal(1, engine.ActiveParseCount);
        Assert.Equal(2, engine.QueuedParseCount);
        Assert.Equal(3, engine.OutstandingParseCount);

        release.TrySetResult();
        MarkdownRenderer.Document.MarkdownDocument[] documents = await Task.WhenAll(
            first, second, duplicate, third).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Same(documents[1], documents[2]);
        Assert.Equal(0, engine.OutstandingParseCount);
    }

    [Fact]
    public async Task ParseAsync_FullAdmissionRejectsAnotherUniqueSource()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var engine = new MarkdownEngineBuilder()
            .UseExtension(new ConfigurableExtension("tests.full-admission", builder =>
                builder.RegisterBlockAsync(MarkdownSyntaxKinds.Block.Paragraph, async (context, _) =>
                {
                    entered.TrySetResult();
                    await release.Task.WaitAsync(context.CancellationToken);
                })))
            .WithParseLimits(new MarkdownParseLimits(
                maximumSourceLength: 100,
                maximumConcurrentParseCount: 1,
                maximumOutstandingParseCount: 2,
                maximumOutstandingSourceBytes: 200))
            .Build();

        Task first = engine.ParseAsync("one");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task second = engine.ParseAsync("two");
        await Assert.ThrowsAsync<InvalidOperationException>(() => engine.ParseAsync("three"));

        release.TrySetResult();
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task CancelingEveryQueuedWaiterImmediatelyReleasesAdmission()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var engine = new MarkdownEngineBuilder()
            .UseExtension(new ConfigurableExtension("tests.queued-cancellation", builder =>
                builder.RegisterBlockAsync(MarkdownSyntaxKinds.Block.Paragraph, async (context, _) =>
                {
                    entered.TrySetResult();
                    await release.Task.WaitAsync(context.CancellationToken);
                })))
            .WithParseLimits(new MarkdownParseLimits(
                maximumSourceLength: 100,
                maximumConcurrentParseCount: 1,
                maximumOutstandingParseCount: 2,
                maximumOutstandingSourceBytes: 200))
            .Build();

        Task first = engine.ParseAsync("one");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var cancellation = new CancellationTokenSource();
        Task queued = engine.ParseAsync("two", cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
        Assert.Equal(1, engine.OutstandingParseCount);
        Assert.Equal(0, engine.QueuedParseCount);

        Task replacement = engine.ParseAsync("three");
        Assert.Equal(2, engine.OutstandingParseCount);
        release.TrySetResult();
        await Task.WhenAll(first, replacement).WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task AsyncExtensionCallbacksMayRunConcurrentlyAcrossDocuments()
    {
        int active = 0;
        int maximumActive = 0;
        var bothEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var engine = new MarkdownEngineBuilder()
            .UseExtension(new ConfigurableExtension("tests.concurrent-async", builder =>
                builder.RegisterBlockAsync(MarkdownSyntaxKinds.Block.Paragraph, async (context, _) =>
                {
                    int now = Interlocked.Increment(ref active);
                    if (now == 2)
                        Interlocked.Exchange(ref maximumActive, 2);
                    else
                        Interlocked.CompareExchange(ref maximumActive, 1, 0);
                    if (now == 2)
                        bothEntered.TrySetResult();
                    try
                    {
                        await release.Task.WaitAsync(context.CancellationToken);
                    }
                    finally
                    {
                        Interlocked.Decrement(ref active);
                    }
                })))
            .WithParseLimits(new MarkdownParseLimits(
                maximumSourceLength: 100,
                maximumConcurrentParseCount: 2,
                maximumOutstandingParseCount: 2,
                maximumOutstandingSourceBytes: 200))
            .Build();

        Task first = engine.ParseAsync("one");
        Task second = engine.ParseAsync("two");
        await bothEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        release.TrySetResult();
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(2, maximumActive);
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
        var callbackRegistered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackExited = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var callbackRelease = new ManualResetEventSlim();
        TrackingDisposable? resource = null;

        var builder = new MarkdownEngineBuilder();
        builder.UseOwnedExtensionFactory(
            "tests.dispose-blocking-cancellation",
            () =>
            {
                resource = new TrackingDisposable(() => { });
                return new MarkdownOwnedExtension(
                    new ConfigurableExtension(
                        "tests.dispose-blocking-cancellation",
                        extensionBuilder => extensionBuilder.RegisterBlock(
                            MarkdownSyntaxKinds.Block.Paragraph,
                            (context, contentBuilder) =>
                            {
                                _ = context.CancellationToken.Register(() =>
                                {
                                    callbackEntered.TrySetResult();
                                    try
                                    {
                                        if (!callbackRelease.Wait(TimeSpan.FromSeconds(5)))
                                        {
                                            throw new TimeoutException(
                                                "Cancellation callback was not released.");
                                        }
                                    }
                                    finally
                                    {
                                        callbackExited.TrySetResult();
                                    }
                                });
                                callbackRegistered.TrySetResult();
                                if (!SpinWait.SpinUntil(
                                        () => context.CancellationToken.IsCancellationRequested,
                                        TimeSpan.FromSeconds(5)))
                                {
                                    throw new TimeoutException(
                                        "Engine disposal did not request cancellation.");
                                }

                                context.CancellationToken.ThrowIfCancellationRequested();
                            })),
                    resource);
            });
        MarkdownEngine engine = builder.Build();
        Task parse = engine.ParseAsync("active");
        await callbackRegistered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Task dispose = Task.Run(engine.Dispose);
        try
        {
            await dispose.WaitAsync(TimeSpan.FromSeconds(1));
            // CancelAsync deliberately queues registrations away from the
            // disposing thread. A saturated hosted runner may take longer
            // than two seconds to inject that worker even though Dispose has
            // already met its bounded-return contract above. This wait checks
            // eventual ordering, not callback-start latency.
            await callbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(15));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => parse.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.NotNull(resource);
            Assert.False(resource.IsDisposed);
        }
        finally
        {
            callbackRelease.Set();
        }

        await callbackExited.Task.WaitAsync(TimeSpan.FromSeconds(2));
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

    [Fact]
    public void OwnedExtensionBuildersAndSetsRemainReusableAfterEveryEngineDisposes()
    {
        var resources = new List<TrackingDisposable>();
        var builder = new MarkdownEngineBuilder();
        builder.UseOwnedExtensionFactory(
            "tests.reusable-resource",
            () =>
            {
                var resource = new TrackingDisposable(static () => { });
                resources.Add(resource);
                return new MarkdownOwnedExtension(
                    new ConfigurableExtension("tests.reusable-resource", static _ => { }),
                    resource);
            });
        MarkdownEngine first = builder.Build();
        MarkdownExtensionSet extensions = first.Extensions;
        MarkdownEngine second = builder.Build();
        MarkdownEngine derived = new MarkdownEngineBuilder().UseExtensions(extensions).Build();
        MarkdownEngine copied = first.ToBuilder().Build();

        first.Dispose();
        second.Dispose();
        derived.Dispose();
        copied.Dispose();
        Assert.Equal(4, resources.Count);
        Assert.All(resources, static resource => Assert.True(resource.IsDisposed));

        MarkdownEngine rebuilt = builder.Build();
        Assert.Equal(5, resources.Count);
        Assert.False(resources[4].IsDisposed);
        rebuilt.Dispose();
        Assert.True(resources[4].IsDisposed);
    }

    [Fact]
    public void OwnedExtensionFactoriesIncludedByOwnedExtensionsBindInPlace()
    {
        var childResources = new List<TrackingDisposable>();
        MarkdownEngine childTemplateEngine = new MarkdownEngineBuilder()
            .UseOwnedExtensionFactory(
                "tests.nested-child",
                () =>
                {
                    var resource = new TrackingDisposable(static () => { });
                    childResources.Add(resource);
                    return new MarkdownOwnedExtension(
                        new ConfigurableExtension(
                            "tests.nested-child",
                            static builder => builder.AddFeature("tests.nested-feature")),
                        resource);
                })
            .Build();
        var parentResources = new List<TrackingDisposable>();
        MarkdownEngine parentEngine = new MarkdownEngineBuilder()
            .UseOwnedExtensionFactory(
                "tests.nested-parent",
                () =>
                {
                    var resource = new TrackingDisposable(static () => { });
                    parentResources.Add(resource);
                    return new MarkdownOwnedExtension(
                        new ConfigurableExtension(
                            "tests.nested-parent",
                            builder => builder.Include(childTemplateEngine.Extensions)),
                        resource);
                })
            .Build();

        try
        {
            Assert.Equal(2, childResources.Count);
            Assert.Single(parentResources);
            Assert.True(parentEngine.Extensions.ContainsExtension("tests.nested-parent"));
            Assert.True(parentEngine.Extensions.ContainsExtension("tests.nested-child"));
            Assert.True(parentEngine.Extensions.HasFeature("tests.nested-feature"));

            parentEngine.Dispose();
            Assert.True(parentResources[0].IsDisposed);
            Assert.True(childResources[1].IsDisposed);
            Assert.False(childResources[0].IsDisposed);
        }
        finally
        {
            parentEngine.Dispose();
            childTemplateEngine.Dispose();
        }

        Assert.True(childResources[0].IsDisposed);
    }

    [Fact]
    public void OwnedExtensionFactoryDoesNotAllocateUntilBuildAndCleansUpFailedBinding()
    {
        int created = 0;
        int disposed = 0;
        var builder = new MarkdownEngineBuilder();
        builder.UseOwnedExtensionFactory(
            "tests.binding-failure",
            () =>
            {
                Interlocked.Increment(ref created);
                var resource = new TrackingDisposable(() => Interlocked.Increment(ref disposed));
                return new MarkdownOwnedExtension(
                    new ConfigurableExtension("tests.binding-failure", static _ =>
                        throw new InvalidOperationException("synthetic binding failure")),
                    resource);
            });

        Assert.Equal(0, created);
        Assert.Throws<InvalidOperationException>(() => builder.Build());
        Assert.Equal(1, created);
        Assert.Equal(1, disposed);
        Assert.Throws<InvalidOperationException>(() => builder.Build());
        Assert.Equal(2, created);
        Assert.Equal(2, disposed);
    }

    [Fact]
    public void OwnedExtensionFactoryValidationIsNotMaskedByCleanupFailure()
    {
        var builder = new MarkdownEngineBuilder();
        builder.UseOwnedExtensionFactory(
            "tests.expected",
            static () => new MarkdownOwnedExtension(
                new ConfigurableExtension("tests.wrong", static _ => { }),
                new ThrowingDisposable()));

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => builder.Build());

        Assert.Contains("tests.expected", exception.Message, StringComparison.Ordinal);
        Assert.Contains("tests.wrong", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OwnedExtensionFactoryDisposesResourceWhenExtensionIdGetterThrows()
    {
        var expected = new InvalidOperationException("synthetic Id failure");
        var resource = new TrackingDisposable(static () => { });
        var builder = new MarkdownEngineBuilder();
        builder.UseOwnedExtensionFactory(
            "tests.throwing-id",
            () => new MarkdownOwnedExtension(
                new ThrowingIdExtension(expected),
                resource));

        InvalidOperationException actual = Assert.Throws<InvalidOperationException>(
            () => builder.Build());

        Assert.Same(expected, actual);
        Assert.True(resource.IsDisposed);
    }

    [Fact]
    public void OwnedExtensionFactoryReadsExtensionIdExactlyOnceDuringBinding()
    {
        int idReads = 0;
        int configureCalls = 0;
        var resource = new TrackingDisposable(static () => { });
        var builder = new MarkdownEngineBuilder();
        builder.UseOwnedExtensionFactory(
            "tests.one-shot-id",
            () => new MarkdownOwnedExtension(
                new OneShotIdExtension(
                    "tests.one-shot-id",
                    () => Interlocked.Increment(ref idReads),
                    () => Interlocked.Increment(ref configureCalls)),
                resource));

        using MarkdownEngine engine = builder.Build();

        Assert.Equal(1, idReads);
        Assert.Equal(1, configureCalls);
        Assert.True(engine.Extensions.ContainsExtension("tests.one-shot-id"));
    }

    [Fact]
    public async Task ParseAsync_ReusesCompletedDocumentWithinBudget()
    {
        var engine = new MarkdownEngineBuilder()
            .WithParseCacheBudgetBytes(1024 * 1024)
            .Build();

        var first = await engine.ParseAsync("# cached");
        var second = await engine.ParseAsync("# cached");

        Assert.Same(first, second);
    }

    [Fact]
    public async Task ParseAsync_CacheIsScopedToTheExactEngineConfiguration()
    {
        const string source = "| A |\n|---|\n| 1 |";
        var commonMark = new MarkdownEngineBuilder()
            .UseProfile(MarkdownProfiles.CommonMark)
            .WithParseCacheBudgetBytes(1024 * 1024)
            .Build();
        var gfm = new MarkdownEngineBuilder()
            .UseProfile(MarkdownProfiles.GfmStrict)
            .WithParseCacheBudgetBytes(1024 * 1024)
            .Build();

        var commonMarkDocument = await commonMark.ParseAsync(source);
        var gfmDocument = await gfm.ParseAsync(source);

        Assert.NotSame(commonMarkDocument, gfmDocument);
        Assert.Same(commonMarkDocument, await commonMark.ParseAsync(source));
        Assert.Same(gfmDocument, await gfm.ParseAsync(source));
    }

    [Fact]
    public async Task Gfm029_TableContinuationAndFollowingSourceRangesRemainExact()
    {
        const string source = """
            | abc | def |
            | --- | --- |
            | one | two |
            three

            # Heading

            [link](https://example.test)
            """;
        var engine = new MarkdownEngineBuilder()
            .UseProfile(MarkdownProfiles.GfmStrict)
            .WithParseCacheBudgetBytes(0)
            .Build();

        var document = await engine.ParseAsync(source);

        var table = Assert.IsType<Table>(document.ParsedDocument![0]);
        Assert.Equal(3, table.Count);
        var continuation = Assert.IsType<TableRow>(table[2]);
        var firstCell = Assert.IsType<TableCell>(continuation[0]);
        var paragraph = Assert.IsType<ParagraphBlock>(firstCell[0]);
        Assert.Equal("three", Assert.IsType<LiteralInline>(paragraph.Inline!.FirstChild).Content.ToString());
        var heading = Assert.Single(document.GetHeadings());
        var link = Assert.Single(document.GetLinks());
        Assert.Equal(source.IndexOf("# Heading", StringComparison.Ordinal), heading.SourceSpan.Start);
        Assert.Equal(source.IndexOf("[link]", StringComparison.Ordinal), link.SourceSpan.Start);
    }

    [Fact]
    public async Task Gfm029_MismatchedTableHeaderStaysLiteralWithoutMaskCharacters()
    {
        const string source = "| abc | def |\n| --- |\n| bar |";
        var document = await new MarkdownEngineBuilder()
            .UseProfile(MarkdownProfiles.GfmStrict)
            .WithParseCacheBudgetBytes(0)
            .Build()
            .ParseAsync(source);

        Assert.IsType<ParagraphBlock>(document.ParsedDocument![0]);
        Assert.DoesNotContain(document.ParsedDocument!, static block => block is Table);
        Assert.DoesNotContain('\uE000', CollectInlineText(
            Assert.IsType<ParagraphBlock>(document.ParsedDocument![0]).Inline!));
    }

    [Fact]
    public async Task Gfm029_AutolinkEmailRejectsTrailingDomainUnderscore()
    {
        const string source = "foo@bar.baz\n\nfoo@bar.baz_";
        var document = await new MarkdownEngineBuilder()
            .UseProfile(MarkdownProfiles.GfmStrict)
            .WithParseCacheBudgetBytes(0)
            .Build()
            .ParseAsync(source);

        var link = Assert.Single(document.GetLinks());
        Assert.Equal("foo@bar.baz", link.DisplayText);
        Assert.Equal("mailto:foo@bar.baz", link.Url);
        Assert.DoesNotContain(
            document.GetLinks(),
            value => value.SourceSpan.Start == source.LastIndexOf("foo", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ParseAsync_ZeroBudgetDeduplicatesOnlyConcurrentWork()
    {
        string source = CreateLargeMarkdown(5000);
        var engine = new MarkdownEngineBuilder()
            .WithParseCacheBudgetBytes(0)
            .Build();

        var firstTask = engine.ParseAsync(source);
        var secondTask = engine.ParseAsync(source);
        var first = await firstTask;
        var second = await secondTask;
        var later = await engine.ParseAsync(source);

        Assert.Same(first, second);
        Assert.NotSame(first, later);
    }

    [Fact]
    public async Task ParseAsync_DeduplicatesEqualSourcesWithDifferentStringInstances()
    {
        var entered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var engine = new MarkdownEngineBuilder()
            .UseExtension(new ConfigurableExtension("tests.equal-source-deduplication", builder =>
                builder.RegisterBlockAsync(MarkdownSyntaxKinds.Block.Paragraph, async (context, _) =>
                {
                    entered.TrySetResult();
                    await release.Task.WaitAsync(context.CancellationToken);
                })))
            .WithParseCacheBudgetBytes(0)
            .Build();
        string firstSource = new(['e', 'q', 'u', 'a', 'l']);
        string secondSource = new(firstSource.ToCharArray());
        Assert.NotSame(firstSource, secondSource);

        Task<MarkdownRenderer.Document.MarkdownDocument> first = engine.ParseAsync(firstSource);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task<MarkdownRenderer.Document.MarkdownDocument> second = engine.ParseAsync(secondSource);
        Assert.Equal(1, engine.OutstandingParseCount);

        release.TrySetResult();
        MarkdownRenderer.Document.MarkdownDocument[] documents = await Task.WhenAll(first, second)
            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Same(documents[0], documents[1]);
    }

    [Fact]
    public async Task ParseAsync_CancelingOneCallerDoesNotCancelSharedParse()
    {
        string source = CreateLargeMarkdown(5000);
        var engine = new MarkdownEngineBuilder()
            .WithParseCacheBudgetBytes(8 * 1024 * 1024)
            .Build();
        using var cancellation = new CancellationTokenSource();

        var canceledCaller = engine.ParseAsync(source, cancellation.Token);
        var remainingCaller = engine.ParseAsync(source);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceledCaller);
        var completed = await remainingCaller;
        var cached = await engine.ParseAsync(source);

        Assert.Same(completed, cached);
    }

    [Fact]
    public async Task ParseAsync_AbandonedWorkCannotPopulateCache()
    {
        string source = CreateLargeMarkdown(5000);
        var engine = new MarkdownEngineBuilder()
            .WithParseCacheBudgetBytes(8 * 1024 * 1024)
            .Build();
        using var cancellation = new CancellationTokenSource();

        var abandoned = engine.ParseAsync(source, cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => abandoned);

        Assert.True(SpinWait.SpinUntil(
            () => engine.ActiveParseCount == 0,
            TimeSpan.FromSeconds(5)));
        Assert.Equal(0, engine.CompletedParseCount);

        var replacement = await engine.ParseAsync(source);
        var cached = await engine.ParseAsync(source);

        Assert.Same(replacement, cached);
    }

    [Fact]
    public async Task ParseAsync_CancellationCallbacksCanReenterEngineWithoutDeadlock()
    {
        var callbackRegistered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var reentrantParseStarted = new TaskCompletionSource<Task<MarkdownRenderer.Document.MarkdownDocument>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var releasePrimaryParse = new ManualResetEventSlim();
        MarkdownEngine? engine = null;

        engine = new MarkdownEngineBuilder()
            .UseExtension(new ConfigurableExtension("tests.reentrant-cancellation", builder =>
                builder.RegisterBlock(MarkdownSyntaxKinds.Block.Paragraph, (context, _) =>
                {
                    if (!string.Equals(context.Node.Literal, "primary", StringComparison.Ordinal))
                        return;

                    context.CancellationToken.Register(() =>
                    {
                        try
                        {
                            // ParseAsync enters the engine gate before returning.
                            // This callback therefore deadlocks if cancellation is
                            // signaled while that gate is held.
                            reentrantParseStarted.TrySetResult(engine!.ParseAsync("secondary"));
                        }
                        finally
                        {
                            releasePrimaryParse.Set();
                        }

                        throw new InvalidOperationException("Cancellation callback failures are isolated.");
                    });
                    callbackRegistered.TrySetResult();
                    if (!releasePrimaryParse.Wait(TimeSpan.FromSeconds(5)))
                        throw new TimeoutException("Cancellation callback did not release the parser.");
                    context.CancellationToken.ThrowIfCancellationRequested();
                })))
            .WithParseCacheBudgetBytes(1024 * 1024)
            .Build();

        using var cancellation = new CancellationTokenSource();
        Task<MarkdownRenderer.Document.MarkdownDocument> primary =
            engine.ParseAsync("primary", cancellation.Token);
        await callbackRegistered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Task cancel = Task.Run(cancellation.Cancel);
        Task<MarkdownRenderer.Document.MarkdownDocument> reentrant =
            await reentrantParseStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => primary.WaitAsync(TimeSpan.FromSeconds(5)));
        await cancel.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("secondary", (await reentrant.WaitAsync(TimeSpan.FromSeconds(5))).Source);
        Assert.True(SpinWait.SpinUntil(
            () => engine.ActiveParseCount == 0,
            TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task ParseAsync_LastWaiterCancellationDoesNotWaitForExtensionCallbacks()
    {
        var callbackRegistered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var callbackRelease = new ManualResetEventSlim();
        using var parserRelease = new ManualResetEventSlim();

        var engine = new MarkdownEngineBuilder()
            .UseExtension(new ConfigurableExtension("tests.blocking-cancellation", builder =>
                builder.RegisterBlock(MarkdownSyntaxKinds.Block.Paragraph, (context, _) =>
                {
                    using var registration = context.CancellationToken.Register(() =>
                    {
                        callbackEntered.TrySetResult();
                        if (!callbackRelease.Wait(TimeSpan.FromSeconds(5)))
                            throw new TimeoutException("Cancellation callback was not released.");
                        parserRelease.Set();
                    });
                    callbackRegistered.TrySetResult();
                    if (!parserRelease.Wait(TimeSpan.FromSeconds(5)))
                        throw new TimeoutException("Parser was not released.");
                    context.CancellationToken.ThrowIfCancellationRequested();
                })))
            .WithParseCacheBudgetBytes(0)
            .Build();

        using var cancellation = new CancellationTokenSource();
        Task parse = engine.ParseAsync("active", cancellation.Token);
        await callbackRegistered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Task cancel = Task.Run(cancellation.Cancel);
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => parse.WaitAsync(TimeSpan.FromSeconds(1)));
            await cancel.WaitAsync(TimeSpan.FromSeconds(1));
            await callbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, engine.ActiveParseCount);
        }
        finally
        {
            callbackRelease.Set();
            parserRelease.Set();
        }

        Assert.True(SpinWait.SpinUntil(
            () => engine.ActiveParseCount == 0,
            TimeSpan.FromSeconds(5)));
        Assert.Equal(0, engine.CompletedParseCount);
    }

    [Fact]
    public async Task ParseCache_EvictsLeastRecentlyUsedDocumentsByWeight()
    {
        const string sourceA = "# " + "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        const string sourceB = "# " + "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        const string sourceC = "# " + "cccccccccccccccccccccccccccccccc";
        var sizingEngine = new MarkdownEngineBuilder()
            .WithParseCacheBudgetBytes(0)
            .Build();
        var sizedA = await sizingEngine.ParseAsync(sourceA);
        var sizedB = await sizingEngine.ParseAsync(sourceB);
        long twoDocumentBudget =
            MarkdownEngine.EstimateDocumentWeight(sizedA) +
            MarkdownEngine.EstimateDocumentWeight(sizedB);
        var engine = new MarkdownEngineBuilder()
            .WithParseCacheBudgetBytes(twoDocumentBudget)
            .Build();

        var firstA = await engine.ParseAsync(sourceA);
        var firstB = await engine.ParseAsync(sourceB);
        Assert.Same(firstA, await engine.ParseAsync(sourceA));

        _ = await engine.ParseAsync(sourceC);

        Assert.Same(firstA, await engine.ParseAsync(sourceA));
        Assert.NotSame(firstB, await engine.ParseAsync(sourceB));
    }

    [Fact]
    public async Task ParseCache_SameSourceReferenceSkipsRepeatedContentHashing()
    {
        string source = "# heading\n\n" + new string('x', 256 * 1024);
        using var engine = new MarkdownEngineBuilder()
            .WithParseCacheBudgetBytes(8 * 1024 * 1024)
            .Build();

        var first = await engine.ParseAsync(source);
        Assert.Equal(1, engine.SourceKeyHashCount);

        for (int iteration = 0; iteration < 20; iteration++)
            Assert.Same(first, await engine.ParseAsync(source));

        Assert.Equal(1, engine.SourceKeyHashCount);
    }

    [Fact]
    public async Task ParseCache_EqualDistinctSourcesRemainContentDeduplicated()
    {
        string firstSource = new(['#', ' ', 's', 'a', 'm', 'e']);
        string secondSource = new(firstSource.ToCharArray());
        Assert.NotSame(firstSource, secondSource);

        using var engine = new MarkdownEngineBuilder()
            .WithParseCacheBudgetBytes(1024 * 1024)
            .Build();

        var first = await engine.ParseAsync(firstSource);
        var equalContentHit = await engine.ParseAsync(secondSource);

        Assert.Same(first, equalContentHit);
        Assert.Equal(2, engine.SourceKeyHashCount);

        // The fast path tracks the caller's actual last string identity, not
        // merely the equivalent string retained as the dictionary key.
        Assert.Same(first, await engine.ParseAsync(secondSource));
        Assert.Equal(2, engine.SourceKeyHashCount);

        Assert.Same(first, await engine.ParseAsync(firstSource));
        Assert.Equal(3, engine.SourceKeyHashCount);
    }

    [Fact]
    public async Task ParseCache_IdentityFastPathIsSafeForConcurrentCallers()
    {
        string source = "# shared\n\n" + new string('x', 64 * 1024);
        using var engine = new MarkdownEngineBuilder()
            .WithParseCacheBudgetBytes(2 * 1024 * 1024)
            .Build();
        var expected = await engine.ParseAsync(source);
        long hashesBeforeHits = engine.SourceKeyHashCount;
        var callers = new Task<MarkdownRenderer.Document.MarkdownDocument>[32];

        for (int index = 0; index < callers.Length; index++)
            callers[index] = Task.Run(() => engine.ParseAsync(source));

        MarkdownRenderer.Document.MarkdownDocument[] documents = await Task.WhenAll(callers);
        Assert.All(documents, document => Assert.Same(expected, document));
        Assert.Equal(hashesBeforeHits, engine.SourceKeyHashCount);
    }

    [Fact]
    public async Task ParseCache_IdentityFastPathHonorsCancellationAndDisabledCache()
    {
        string source = new(['#', ' ', 'n', 'o', 't', ' ', 'c', 'a', 'c', 'h', 'e', 'd']);
        using var cachedEngine = new MarkdownEngineBuilder()
            .WithParseCacheBudgetBytes(1024 * 1024)
            .Build();
        var expected = await cachedEngine.ParseAsync(source);
        long hashesBeforeCancellation = cachedEngine.SourceKeyHashCount;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => cachedEngine.ParseAsync(source, cancellation.Token));
        Assert.Equal(hashesBeforeCancellation, cachedEngine.SourceKeyHashCount);
        Assert.Same(expected, await cachedEngine.ParseAsync(source));

        using var uncachedEngine = new MarkdownEngineBuilder()
            .WithParseCacheBudgetBytes(0)
            .Build();
        var first = await uncachedEngine.ParseAsync(source);
        var second = await uncachedEngine.ParseAsync(source);

        Assert.NotSame(first, second);
        Assert.Equal(2, uncachedEngine.SourceKeyHashCount);
    }

    [Fact]
    public async Task ParseCache_IdentityFastPathDoesNotOutliveEvictionOrDisposal()
    {
        const string firstSource = "# alpha\n\nalpha";
        const string secondSource = "# bravo\n\nbravo";
        using var sizingEngine = new MarkdownEngineBuilder()
            .WithParseCacheBudgetBytes(0)
            .Build();
        long oneDocumentBudget = MarkdownEngine.EstimateDocumentWeight(
            await sizingEngine.ParseAsync(firstSource));
        var engine = new MarkdownEngineBuilder()
            .WithParseCacheBudgetBytes(oneDocumentBudget)
            .Build();

        var first = await engine.ParseAsync(firstSource);
        _ = await engine.ParseAsync(secondSource);
        long hashesBeforeReparse = engine.SourceKeyHashCount;
        var reparsed = await engine.ParseAsync(firstSource);

        Assert.NotSame(first, reparsed);
        Assert.Equal(hashesBeforeReparse + 1, engine.SourceKeyHashCount);

        engine.Dispose();
        long hashesBeforeDisposedCall = engine.SourceKeyHashCount;
        await Assert.ThrowsAsync<ObjectDisposedException>(() => engine.ParseAsync(firstSource));
        Assert.Equal(hashesBeforeDisposedCall, engine.SourceKeyHashCount);
    }

    [Fact]
    public async Task ParseCache_AccountsForExtensionOwnedContent()
    {
        const long budget = 4096;
        const string source = "tiny";
        string extensionPayload = new('z', 16 * 1024);

        var plainEngine = new MarkdownEngineBuilder()
            .WithParseCacheBudgetBytes(budget)
            .Build();
        var plain = await plainEngine.ParseAsync(source);
        Assert.Same(plain, await plainEngine.ParseAsync(source));

        var extensionEngine = new MarkdownEngineBuilder()
            .UseExtension(new ConfigurableExtension("tests.large-content", builder =>
                builder.RegisterBlock(MarkdownSyntaxKinds.Block.Paragraph, (context, content) =>
                    content.AddText(
                        extensionPayload,
                        context.Node.SourceSpan,
                        MarkdownStyleRole.Body))))
            .WithParseCacheBudgetBytes(budget)
            .Build();

        var first = await extensionEngine.ParseAsync(source);
        var second = await extensionEngine.ParseAsync(source);

        Assert.True(MarkdownEngine.EstimateDocumentWeight(first) > budget);
        Assert.NotSame(first, second);
    }

    [Fact]
    public void ExtensionRegistry_IsImmutableAndUsesExactKindDispatch()
    {
        var engine = new MarkdownEngineBuilder()
            .UseExtension(new TestExtension())
            .Build();

        Assert.True(engine.Extensions.ContainsExtension("tests.extension"));
        Assert.True(engine.Extensions.TryGetBlockRenderer("tests.block", out var renderer));
        Assert.False(engine.Extensions.TryGetBlockRenderer("tests", out _));

        var content = new MarkdownContentBuilder();
        renderer!(
            new MarkdownExtensionContext(new MarkdownSyntaxNode("tests.block", new SourceSpan(0, 1), "x")),
            content);
        var fragment = content.Build();
        Assert.Single(fragment.Items);
        Assert.Equal(MarkdownContentKind.Text, fragment.Items[0].Kind);
    }

    [Fact]
    public void LegacySynchronousLookupNeverWrapsAsyncRegistrations()
    {
        var builder = new MarkdownExtensionBuilder();
        builder.RegisterBlockAsync("tests.async", static (_, _) =>
            new ValueTask(Task.Delay(Timeout.InfiniteTimeSpan)));
        builder.RegisterInline("tests.mixed", static (_, _) => { });
        builder.RegisterInlineAsync("tests.mixed", static (_, _) => ValueTask.CompletedTask);
        MarkdownExtensionSet extensions = builder.Build();

        Assert.False(extensions.TryGetBlockRenderer("tests.async", out _));
        Assert.False(extensions.TryGetInlineRenderer("tests.mixed", out _));
        Assert.True(extensions.TryGetAsyncBlockRenderer("tests.async", out _));
        Assert.True(extensions.TryGetAsyncInlineRenderer("tests.mixed", out _));
    }

    private sealed class TestExtension : IMarkdownExtension
    {
        public string Id => "tests.extension";

        public void Configure(MarkdownExtensionBuilder builder)
        {
            builder.RegisterBlock("tests.block", static (context, content) =>
                content.AddText(
                    context.Node.Literal ?? string.Empty,
                    context.Node.SourceSpan,
                    MarkdownStyleRole.Body));
        }
    }

    private sealed class ConfigurableExtension(
        string id,
        Action<MarkdownExtensionBuilder> configure) : IMarkdownExtension
    {
        public string Id => id;

        public void Configure(MarkdownExtensionBuilder builder) => configure(builder);
    }

    private sealed class ThrowingIdExtension(Exception failure) : IMarkdownExtension
    {
        public string Id => throw failure;

        public void Configure(MarkdownExtensionBuilder builder) =>
            throw new InvalidOperationException("Configure must not run when Id validation fails.");
    }

    private sealed class OneShotIdExtension(
        string id,
        Func<int> recordIdRead,
        Func<int> recordConfigureCall) : IMarkdownExtension
    {
        public string Id
        {
            get
            {
                int readCount = recordIdRead();
                if (readCount != 1)
                {
                    throw new InvalidOperationException(
                        "The owned extension Id getter was read more than once.");
                }

                return id;
            }
        }

        public void Configure(MarkdownExtensionBuilder builder) => recordConfigureCall();
    }

    private sealed class TrackingDisposable(Action dispose) : IDisposable
    {
        private int _disposed;

        internal bool IsDisposed => Volatile.Read(ref _disposed) != 0;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                dispose();
        }
    }

    private sealed class ThrowingDisposable : IDisposable
    {
        public void Dispose() => throw new InvalidOperationException("cleanup failure");
    }

    private static string CreateLargeMarkdown(int sectionCount)
    {
        var builder = new StringBuilder(sectionCount * 32);
        for (int index = 0; index < sectionCount; index++)
        {
            builder.Append("## Section ");
            builder.Append(index);
            builder.Append("\n\nParagraph ");
            builder.Append(index);
            builder.Append(".\n\n");
        }

        return builder.ToString();
    }

    private static string CollectInlineText(ContainerInline container)
    {
        var builder = new StringBuilder();
        for (Inline? inline = container.FirstChild; inline is not null; inline = inline.NextSibling)
        {
            if (inline is LiteralInline literal)
                builder.Append(literal.Content.ToString());
            if (inline is ContainerInline nested)
                builder.Append(CollectInlineText(nested));
        }
        return builder.ToString();
    }
}
