using MarkdownRenderer.Extensions;
using MarkdownRenderer.Theming;
using Xunit;

namespace MarkdownRenderer.Tests;

/// <summary>
/// Checks the engine parse cache: its budget, reuse scoped to the exact
/// configuration, weighted LRU eviction, the source identity fast path
/// and extension-owned content accounting.
/// </summary>
public sealed class MarkdownEngineParseCacheTests
{
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
}
