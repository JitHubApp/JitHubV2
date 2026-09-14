using MarkdownRenderer.Layout;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

public sealed class SharedLayoutMetricsCacheTests
{
    [Fact]
    public void EqualConfiguration_ReusesOneConcurrentMetricsBuffer()
    {
        var cache = new SharedLayoutMetricsCache(16 * 1024);
        var styleSheet = new object();
        var registry = new object();
        SharedLayoutMetricsKey firstKey = CreateKey("# shared", styleSheet, registry);
        SharedLayoutMetricsKey equivalentKey = CreateKey("# shared", styleSheet, registry);

        SharedLayoutMetrics first = cache.GetOrCreate(firstKey, topLevelBlockCount: 3);
        first.RecordHeight(1, 72.5f);
        SharedLayoutMetrics second = cache.GetOrCreate(equivalentKey, topLevelBlockCount: 3);

        Assert.Same(first, second);
        Assert.True(second.TryGetHeight(1, out float height));
        Assert.Equal(72.5f, height);
        Assert.Equal(1, cache.Statistics.Count);
    }

    [Fact]
    public void HostServiceIdentityAndLayoutConfiguration_PartitionMetrics()
    {
        var cache = new SharedLayoutMetricsCache(64 * 1024);
        var styleSheet = new object();
        var registry = new object();
        var resolverA = new object();
        var resolverB = new object();

        SharedLayoutMetricsKey firstKey = CreateKey(
            "![image](asset)",
            styleSheet,
            registry,
            imageResolver: resolverA);
        SharedLayoutMetricsKey secondKey = CreateKey(
            "![image](asset)",
            styleSheet,
            registry,
            imageResolver: resolverB);

        SharedLayoutMetrics first = cache.GetOrCreate(firstKey, 1);
        SharedLayoutMetrics second = cache.GetOrCreate(secondKey, 1);

        Assert.NotSame(first, second);
        Assert.Equal(2, cache.Statistics.Count);
    }

    [Fact]
    public void WeightedBudget_EvictsLeastRecentlyUsedMetricSet()
    {
        var cache = new SharedLayoutMetricsCache(900);
        var styleSheet = new object();
        var registry = new object();
        SharedLayoutMetricsKey firstKey = CreateKey(
            new string('a', 100), styleSheet, registry, documentIdentity: new object());
        SharedLayoutMetricsKey secondKey = CreateKey(
            new string('b', 100), styleSheet, registry, documentIdentity: new object());
        SharedLayoutMetricsKey thirdKey = CreateKey(
            new string('c', 100), styleSheet, registry, documentIdentity: new object());

        SharedLayoutMetrics first = cache.GetOrCreate(firstKey, 4);
        SharedLayoutMetrics second = cache.GetOrCreate(secondKey, 4);
        Assert.Same(first, cache.GetOrCreate(firstKey, 4)); // first is MRU
        _ = cache.GetOrCreate(thirdKey, 4);

        SharedLayoutMetrics secondAgain = cache.GetOrCreate(secondKey, 4);
        Assert.NotSame(second, secondAgain);
        Assert.InRange(cache.Statistics.RetainedBytes, 1, 900);
    }

    [Fact]
    public void InvalidHeight_IsNeverPublished()
    {
        var metrics = new SharedLayoutMetrics(2, 1);

        metrics.RecordHeight(0, float.NaN);
        metrics.RecordHeight(0, -1);
        metrics.RecordHeight(5, 42);

        Assert.False(metrics.TryGetHeight(0, out _));
        Assert.False(metrics.TryGetHeight(5, out _));
    }

    [Fact]
    public void ImmutableDocumentIdentityMakesSourceHashingAndRetentionUnnecessary()
    {
        var document = new object();
        var styleSheet = new object();
        var registry = new object();
        SharedLayoutMetricsKey shortSource = CreateKey(
            "a", styleSheet, registry, documentIdentity: document);
        SharedLayoutMetricsKey enormousSource = CreateKey(
            new string('z', 1_000_000), styleSheet, registry, documentIdentity: document);

        Assert.Equal(shortSource, enormousSource);
        Assert.Equal(256, shortSource.EstimatedKeyWeightBytes);
        Assert.Equal(256, enormousSource.EstimatedKeyWeightBytes);
    }

    private static SharedLayoutMetricsKey CreateKey(
        string source,
        object styleSheet,
        object registry,
        object? imageResolver = null,
        object? documentIdentity = null) =>
        new(
            source,
            documentIdentity: documentIdentity ?? registry,
            widthInSixtyFourthDips: 50_000,
            typographyFingerprint: 1234,
            flowDirection: 0,
            codeWrappingMode: 0,
            lineNumberMode: 0,
            codeCopyEnabled: true,
            taskEditingEnabled: false,
            rasterScaleInThousandths: 1_000,
            language: "en-US",
            registryRevision: 1,
            disclosureFingerprint: 0,
            imageContext: default,
            styleSheet,
            registry,
            embedFactory: null,
            hostedElementFactory: null,
            imageResolver,
            commandProvider: null,
            stringProvider: null);
}
