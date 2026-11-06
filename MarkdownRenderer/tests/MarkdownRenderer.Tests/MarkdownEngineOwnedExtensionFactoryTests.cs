using MarkdownRenderer.Extensions;
using Xunit;

namespace MarkdownRenderer.Tests;

/// <summary>
/// Checks owned extension builders and factories: reuse after engine
/// disposal, in-place binding of included factories, lazy allocation, and
/// cleanup when validation or the extension id getter fails.
/// </summary>
public sealed class MarkdownEngineOwnedExtensionFactoryTests
{
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

    private sealed class ThrowingDisposable : IDisposable
    {
        public void Dispose() => throw new InvalidOperationException("cleanup failure");
    }
}
