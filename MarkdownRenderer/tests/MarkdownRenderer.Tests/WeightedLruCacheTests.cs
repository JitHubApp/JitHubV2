using System.Threading.Tasks;
using MarkdownRenderer.Utilities;
using Xunit;

namespace MarkdownRenderer.Tests;

public sealed class WeightedLruCacheTests
{
    [Fact]
    public void Set_EvictsLeastRecentlyUsedEntriesByByteWeight()
    {
        var cache = new WeightedLruCache<string, byte[]>(10, static value => value.Length);

        cache.Set("first", new byte[4]);
        cache.Set("second", new byte[4]);
        Assert.True(cache.TryGetValue("first", out _));

        cache.Set("third", new byte[4]);

        Assert.True(cache.TryGetValue("first", out _));
        Assert.False(cache.TryGetValue("second", out _));
        Assert.True(cache.TryGetValue("third", out _));
        Assert.Equal(8, cache.RetainedBytes);
    }

    [Fact]
    public void Set_RejectsSingleEntryLargerThanBudget()
    {
        var evicted = new List<byte[]>();
        var cache = new WeightedLruCache<string, byte[]>(
            4,
            static value => value.Length,
            evicted.Add);
        byte[] value = new byte[5];

        cache.Set("too-large", value);

        Assert.False(cache.TryGetValue("too-large", out _));
        Assert.Same(value, Assert.Single(evicted));
        Assert.Equal(0, cache.RetainedBytes);
    }

    [Fact]
    public void Set_ChargesKeyWeightAndBoundsAliasesUnderManyKeys()
    {
        byte[] aliasedValue = [42];
        var cache = new WeightedLruCache<string, byte[]>(
            6,
            static value => value.Length,
            keyWeightSelector: static key => key.Length);

        cache.Set("aa", aliasedValue);
        cache.Set("bb", aliasedValue);
        cache.Set("cc", aliasedValue);

        Assert.False(cache.TryGetValue("aa", out _));
        Assert.True(cache.TryGetValue("bb", out byte[] second));
        Assert.True(cache.TryGetValue("cc", out byte[] third));
        Assert.Same(aliasedValue, second);
        Assert.Same(aliasedValue, third);
        Assert.Equal(2, cache.Count);
        Assert.Equal(6, cache.RetainedBytes);
    }

    [Fact]
    public void Set_RejectsKeyOverBudgetWithoutChangingValuePolicy()
    {
        byte[] value = [1];
        var evicted = new List<byte[]>();
        var cache = new WeightedLruCache<string, byte[]>(
            4,
            static payload => payload.Length,
            evicted.Add,
            keyWeightSelector: static key => key.Length);

        cache.Set("oversized-key", value);

        Assert.False(cache.TryGetValue("oversized-key", out _));
        Assert.Same(value, Assert.Single(evicted));
        Assert.Equal(0, cache.RetainedBytes);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-12)]
    public void Set_NormalizesNonPositiveKeyWeightsToZero(long keyWeight)
    {
        var cache = new WeightedLruCache<string, byte[]>(
            10,
            static value => value.Length,
            keyWeightSelector: _ => keyWeight);

        cache.Set("key", new byte[3]);

        Assert.Equal(3, cache.RetainedBytes);
        Assert.Equal(1, cache.Count);
    }

    [Fact]
    public void Set_SaturatesWeightAndMakesRoomBeforeLongMaxAddition()
    {
        var cache = new WeightedLruCache<string, byte[]>(
            long.MaxValue,
            static value => value.Length,
            keyWeightSelector: static key => key == "huge" ? long.MaxValue : 0);

        cache.Set("huge", new byte[2]);
        Assert.Equal(long.MaxValue, cache.RetainedBytes);

        cache.Set("small", new byte[1]);

        Assert.False(cache.TryGetValue("huge", out _));
        Assert.True(cache.TryGetValue("small", out _));
        Assert.Equal(1, cache.Count);
        Assert.Equal(1, cache.RetainedBytes);
    }

    [Fact]
    public void ReplacementAndBudgetDrainInvokeEvictionCallbacksOutsideCacheLock()
    {
        WeightedLruCache<string, string>? cache = null;
        var observations = new List<(string Value, int Count)>();
        cache = new WeightedLruCache<string, string>(
            32,
            static value => value.Length,
            value =>
            {
                // A second thread must be able to acquire the cache lock while
                // the callback is running; same-thread Monitor reentrancy would
                // not detect an accidental callback-under-lock regression.
                Task<int> countRead = Task.Run(() => cache!.Count);
                Assert.True(
                    countRead.Wait(TimeSpan.FromSeconds(5)),
                    "Eviction callback ran while holding the cache lock.");
                observations.Add((value, countRead.GetAwaiter().GetResult()));
            },
            keyWeightSelector: static key => key.Length);

        cache.Set("entry", "old");
        cache.Set("entry", "new");
        cache.SetBudget(0);

        Assert.Collection(
            observations,
            observation => Assert.Equal(("old", 1), observation),
            observation => Assert.Equal(("new", 0), observation));
    }

    [Fact]
    public void SetBudgetAndRemoveWhere_ReleaseEntriesOutsideLock()
    {
        var evicted = new List<string>();
        WeightedLruCache<string, string>? cache = null;
        cache = new WeightedLruCache<string, string>(
            20,
            static value => value.Length,
            value =>
            {
                evicted.Add(value);
                _ = cache!.Count;
            });
        cache.Set("a", "aaaa");
        cache.Set("b", "bbbb");
        cache.Set("c", "cccc");

        Assert.Equal(2, cache.RemoveWhere(static key => key is "a" or "c"));
        cache.SetBudget(0);

        Assert.Equal(0, cache.Count);
        Assert.Equal(3, evicted.Count);
        Assert.Contains("aaaa", evicted);
        Assert.Contains("bbbb", evicted);
        Assert.Contains("cccc", evicted);
    }

    [Fact]
    public void BestMatch_SelectsLargestAdmittedVariantAndRefreshesItsRecency()
    {
        var cache = new WeightedLruCache<string, byte[]>(12, static value => value.Length);
        cache.Set("image/2", new byte[2]);
        cache.Set("other/4", new byte[4]);
        cache.Set("image/4", new byte[4]);

        Assert.True(cache.TryGetBestMatch(
            static (key, value) => key.StartsWith("image/", StringComparison.Ordinal) &&
                value.Length <= 4,
            static value => value.Length,
            out byte[] best));
        Assert.Equal(4, best.Length);

        cache.Set("new/4", new byte[4]);
        Assert.True(cache.TryGetValue("image/4", out _));
        Assert.False(cache.TryGetValue("image/2", out _));
        Assert.False(cache.TryGetBestMatch(
            static (key, _) => key.StartsWith("missing/", StringComparison.Ordinal),
            static value => value.Length,
            out _));
    }

    [Fact]
    public void TryReplaceIfReferenceMatches_MissesDoNotInsertOrRefreshRecency()
    {
        var cache = new WeightedLruCache<string, byte[]>(8, static value => value.Length);
        byte[] first = new byte[4];
        byte[] second = new byte[4];

        cache.Set("first", first);
        cache.Set("second", second);

        Assert.False(cache.TryReplaceIfReferenceMatches("first", new byte[4], new byte[1]));
        Assert.False(cache.TryReplaceIfReferenceMatches("missing", first, new byte[1]));
        Assert.False(cache.TryGetValue("missing", out _));

        cache.Set("third", new byte[4]);

        Assert.False(cache.TryGetValue("first", out _));
        Assert.True(cache.TryGetValue("second", out byte[] retainedSecond));
        Assert.True(cache.TryGetValue("third", out _));
        Assert.Same(second, retainedSecond);
        Assert.Equal(8, cache.RetainedBytes);
    }

    [Fact]
    public void TryReplaceIfReferenceMatches_BudgetDenialPreservesEntriesAndOwnership()
    {
        var evicted = new List<byte[]>();
        var cache = new WeightedLruCache<string, byte[]>(
            10,
            static value => value.Length,
            evicted.Add,
            keyWeightSelector: static key => key == "owner" ? 2 : 0);
        byte[] owner = new byte[4];
        byte[] other = new byte[4];
        byte[] replacement = new byte[5];
        cache.Set("owner", owner);
        cache.Set("other", other);

        Assert.Equal(10, cache.RetainedBytes);
        Assert.False(cache.TryReplaceIfReferenceMatches("owner", owner, replacement));

        Assert.True(cache.TryGetValue("owner", out byte[] retainedOwner));
        Assert.True(cache.TryGetValue("other", out byte[] retainedOther));
        Assert.Same(owner, retainedOwner);
        Assert.Same(other, retainedOther);
        Assert.Equal(10, cache.RetainedBytes);
        Assert.Equal(2, cache.Count);
        Assert.Empty(evicted);
    }

    [Fact]
    public void TryReplaceIfReferenceMatches_SuccessUpdatesWeightAndRecency()
    {
        var cache = new WeightedLruCache<string, byte[]>(8, static value => value.Length);
        byte[] first = new byte[2];
        byte[] second = new byte[4];
        byte[] replacement = new byte[3];
        cache.Set("first", first);
        cache.Set("second", second);

        Assert.True(cache.TryReplaceIfReferenceMatches("first", first, replacement));
        Assert.Equal(7, cache.RetainedBytes);
        cache.Set("third", new byte[2]);

        Assert.True(cache.TryGetValue("first", out byte[] retainedFirst));
        Assert.Same(replacement, retainedFirst);
        Assert.False(cache.TryGetValue("second", out _));
        Assert.True(cache.TryGetValue("third", out _));
        Assert.Equal(5, cache.RetainedBytes);
    }

    [Fact]
    public void TryReplaceIfReferenceMatches_SameReferenceDoesNotRefreshOrCleanUp()
    {
        var evicted = new List<string>();
        var cache = new WeightedLruCache<string, string>(
            2,
            static value => value.Length,
            evicted.Add);
        cache.Set("first", "a");
        cache.Set("second", "b");

        Assert.True(cache.TryReplaceIfReferenceMatches("first", "a", "a"));
        Assert.Empty(evicted);
        cache.Set("third", "c");

        Assert.False(cache.TryGetValue("first", out _));
        Assert.True(cache.TryGetValue("second", out _));
        Assert.True(cache.TryGetValue("third", out _));
        Assert.Equal("a", Assert.Single(evicted));
    }

    [Fact]
    public void TryReplaceIfReferenceMatches_UsesReferenceIdentityNotValueEquality()
    {
        var cache = new WeightedLruCache<string, EqualityBomb>(8, static _ => 1);
        var original = new EqualityBomb();
        var equalButDistinct = new EqualityBomb();
        cache.Set("owner", original);

        Assert.False(cache.TryReplaceIfReferenceMatches(
            "owner",
            equalButDistinct,
            new EqualityBomb()));
        var replacement = new EqualityBomb();
        Assert.True(cache.TryReplaceIfReferenceMatches("owner", original, replacement));
        Assert.True(cache.TryGetValue("owner", out EqualityBomb retained));
        Assert.Same(replacement, retained);
    }

    [Fact]
    public void TryReplaceIfReferenceMatches_SaturatesCombinedWeightAndCallsBackOutsideLock()
    {
        var evicted = new List<string>();
        WeightedLruCache<string, string>? cache = null;
        cache = new WeightedLruCache<string, string>(
            long.MaxValue,
            static value => value.Length,
            value =>
            {
                Task<int> countRead = Task.Run(() => cache!.Count);
                Assert.True(
                    countRead.Wait(TimeSpan.FromSeconds(5)),
                    "Replacement callback ran while holding the cache lock.");
                evicted.Add(value);
            },
            keyWeightSelector: static key => key == "owner" ? long.MaxValue - 1 : 0);
        string original = new('a', 1);
        string replacement = new('b', 2);
        cache.Set("owner", original);

        Assert.Equal(long.MaxValue, cache.RetainedBytes);
        Assert.True(cache.TryReplaceIfReferenceMatches("owner", original, original));
        Assert.Empty(evicted);
        Assert.True(cache.TryReplaceIfReferenceMatches("owner", original, replacement));

        Assert.Equal(long.MaxValue, cache.RetainedBytes);
        Assert.Equal(1, cache.Count);
        Assert.Equal("a", Assert.Single(evicted));
        Assert.True(cache.TryGetValue("owner", out string retained));
        Assert.Same(replacement, retained);
    }

    private sealed class EqualityBomb
    {
        public override bool Equals(object? obj) =>
            throw new InvalidOperationException("Cache replacement must use reference identity.");

        public override int GetHashCode() =>
            System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this);
    }

    [Fact]
    public void TryReplaceIfReferenceMatches_RemovedEntryIsNotRecreated()
    {
        var cache = new WeightedLruCache<string, byte[]>(8, static value => value.Length);
        byte[] original = [1, 2];
        cache.Set("owner", original);
        Assert.True(cache.Remove("owner"));

        Assert.False(cache.TryReplaceIfReferenceMatches("owner", original, [3, 4]));
        Assert.False(cache.TryGetValue("owner", out _));
        Assert.Equal(0, cache.Count);
        Assert.Equal(0, cache.RetainedBytes);
    }

    [Fact]
    public async Task TryReplaceIfReferenceMatches_ChangedEntryDuringWeightCalculationIsNotOverwritten()
    {
        byte[] original = [1, 2];
        byte[] newer = [3, 4];
        byte[] replacement = [5, 6];
        var weightStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseWeight = new System.Threading.ManualResetEventSlim(initialState: false);
        var cache = new WeightedLruCache<string, byte[]>(8, value =>
        {
            if (ReferenceEquals(value, replacement))
            {
                weightStarted.TrySetResult();
                Assert.True(releaseWeight.Wait(TimeSpan.FromSeconds(5)));
            }
            return value.Length;
        });
        cache.Set("owner", original);
        Task<bool> pending = Task.Run(() => cache.TryReplaceIfReferenceMatches("owner", original, replacement));
        try
        {
            await weightStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cache.Set("owner", newer);
            releaseWeight.Set();
            Assert.False(await pending.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.True(cache.TryGetValue("owner", out byte[] retained));
            Assert.Same(newer, retained);
            Assert.Equal(2, cache.RetainedBytes);
        }
        finally
        {
            releaseWeight.Set();
            await pending.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public void TryReplaceWithAliasIfReferenceMatches_UpdatesPairWeightsAndCleansUpOutsideLock()
    {
        var retired = new List<byte[]>();
        WeightedLruCache<string, byte[]>? cache = null;
        cache = new WeightedLruCache<string, byte[]>(20, static value => value.Length,
            value =>
            {
                Task<int> probe = Task.Run(() => cache!.Count);
                Assert.True(probe.Wait(TimeSpan.FromSeconds(5)));
                Assert.Equal(2, probe.Result);
                retired.Add(value);
            }, keyWeightSelector: static key => key.Length);
        byte[] original = [1, 2];
        byte[] replacement = [3, 4, 5];
        cache.Set("key", original);
        cache.Set("alias", original);

        Assert.True(cache.TryReplaceWithAliasIfReferenceMatches("key", "alias", original, replacement));

        Assert.Equal(14, cache.RetainedBytes);
        Assert.Equal(2, cache.Count);
        Assert.True(cache.TryGetValue("key", out byte[] primary));
        Assert.True(cache.TryGetValue("alias", out byte[] alias));
        Assert.Same(replacement, primary);
        Assert.Same(replacement, alias);
        Assert.Equal(2, retired.Count);
        Assert.All(retired, value => Assert.Same(original, value));
    }

    [Fact]
    public void TryReplaceWithAliasIfReferenceMatches_PairBudgetDenialLeavesBothUntouched()
    {
        var retired = new List<byte[]>();
        var cache = new WeightedLruCache<string, byte[]>(6, static value => value.Length, retired.Add);
        byte[] original = [1, 2];
        cache.Set("key", original);
        cache.Set("alias", original);

        Assert.False(cache.TryReplaceWithAliasIfReferenceMatches("key", "alias", original, new byte[4]));
        Assert.True(cache.TryGetValue("key", out byte[] primary));
        Assert.True(cache.TryGetValue("alias", out byte[] alias));
        Assert.Same(original, primary);
        Assert.Same(original, alias);
        Assert.Equal(4, cache.RetainedBytes);
        Assert.Empty(retired);
    }

    [Fact]
    public void TryReplaceWithAliasIfReferenceMatches_ChangedAliasIsPreserved()
    {
        var cache = new WeightedLruCache<string, EqualityBomb>(4, static _ => 1);
        var original = new EqualityBomb();
        var newerAlias = new EqualityBomb();
        var replacement = new EqualityBomb();
        cache.Set("key", original);
        cache.Set("alias", newerAlias);

        Assert.True(cache.TryReplaceWithAliasIfReferenceMatches("key", "alias", original, replacement));
        Assert.True(cache.TryGetValue("key", out EqualityBomb primary));
        Assert.True(cache.TryGetValue("alias", out EqualityBomb alias));
        Assert.Same(replacement, primary);
        Assert.Same(newerAlias, alias);
        Assert.Equal(2, cache.RetainedBytes);
    }

    [Fact]
    public void TryReplaceWithAliasIfReferenceMatches_MissingAliasIsNotRecreated()
    {
        var cache = new WeightedLruCache<string, byte[]>(4, static value => value.Length);
        byte[] original = [1];
        byte[] replacement = [2];
        cache.Set("key", original);
        cache.Set("alias", original);
        Assert.True(cache.Remove("alias"));

        Assert.True(cache.TryReplaceWithAliasIfReferenceMatches("key", "alias", original, replacement));
        Assert.True(cache.TryGetValue("key", out byte[] primary));
        Assert.Same(replacement, primary);
        Assert.False(cache.TryGetValue("alias", out _));
        Assert.Equal(1, cache.RetainedBytes);
        Assert.Equal(1, cache.Count);
    }

    [Fact]
    public void TryReplaceWithAliasIfReferenceMatches_SameKeyReplacesAndChargesOnlyOnce()
    {
        var retired = new List<byte[]>();
        var cache = new WeightedLruCache<string, byte[]>(2, static value => value.Length,
            retired.Add, StringComparer.OrdinalIgnoreCase);
        byte[] original = [1];
        byte[] replacement = [2, 3];
        cache.Set("key", original);

        Assert.True(cache.TryReplaceWithAliasIfReferenceMatches("key", "KEY", original, replacement));
        Assert.True(cache.TryGetValue("key", out byte[] primary));
        Assert.Same(replacement, primary);
        Assert.Same(original, Assert.Single(retired));
        Assert.Equal(2, cache.RetainedBytes);
        Assert.Equal(1, cache.Count);
    }

    [Fact]
    public void TryReplaceWithAliasIfReferenceMatches_PrimaryMissDoesNotChangeAliasOrRecency()
    {
        var cache = new WeightedLruCache<string, byte[]>(2, static value => value.Length);
        byte[] original = [1];
        cache.Set("alias", original);
        cache.Set("other", new byte[1]);

        Assert.False(cache.TryReplaceWithAliasIfReferenceMatches("missing", "alias", original, [2]));
        cache.Set("third", new byte[1]);

        Assert.False(cache.TryGetValue("missing", out _));
        Assert.False(cache.TryGetValue("alias", out _));
        Assert.True(cache.TryGetValue("other", out _));
        Assert.True(cache.TryGetValue("third", out _));
        Assert.Equal(2, cache.RetainedBytes);
    }

    [Fact]
    public async Task TryReplaceWithAliasIfReferenceMatches_CompetingCompletionCannotSplitPair()
    {
        byte[] original = [1];
        byte[] first = [2];
        byte[] second = [3];
        var weightStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseWeight = new System.Threading.ManualResetEventSlim(initialState: false);
        var cache = new WeightedLruCache<string, byte[]>(4, value =>
        {
            if (ReferenceEquals(value, first))
            {
                weightStarted.TrySetResult();
                Assert.True(releaseWeight.Wait(TimeSpan.FromSeconds(5)));
            }
            return value.Length;
        });
        cache.Set("key", original);
        cache.Set("alias", original);
        Task<bool> pending = Task.Run(() =>
            cache.TryReplaceWithAliasIfReferenceMatches("key", "alias", original, first));
        try
        {
            await weightStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(cache.TryReplaceWithAliasIfReferenceMatches("key", "alias", original, second));
            releaseWeight.Set();
            Assert.False(await pending.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.True(cache.TryGetValue("key", out byte[] primary));
            Assert.True(cache.TryGetValue("alias", out byte[] alias));
            Assert.Same(second, primary);
            Assert.Same(second, alias);
            Assert.Equal(2, cache.RetainedBytes);
        }
        finally
        {
            releaseWeight.Set();
            await pending.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public void TryReplaceWithAliasIfReferenceMatches_CombinedOverflowIsNotAdmitted()
    {
        var original = new EqualityBomb();
        var replacement = new EqualityBomb();
        var cache = new WeightedLruCache<string, EqualityBomb>(long.MaxValue,
            value => ReferenceEquals(value, replacement) ? long.MaxValue / 2 + 1 : 1);
        cache.Set("key", original);
        cache.Set("alias", original);

        Assert.False(cache.TryReplaceWithAliasIfReferenceMatches("key", "alias", original, replacement));
        Assert.True(cache.TryGetValue("key", out EqualityBomb primary));
        Assert.True(cache.TryGetValue("alias", out EqualityBomb alias));
        Assert.Same(original, primary);
        Assert.Same(original, alias);
        Assert.Equal(2, cache.RetainedBytes);
        Assert.Equal(2, cache.Count);
    }

    [Fact]
    public void ConcurrentReadersAndWriters_NeverExceedByteBudget()
    {
        const int budget = 4 * 1024;
        var cache = new WeightedLruCache<int, byte[]>(budget, static value => value.Length);

        Parallel.For(0, 20_000, index =>
        {
            int key = index % 257;
            cache.Set(key, new byte[16 + (index % 113)]);
            _ = cache.TryGetValue((index * 17) % 257, out _);
        });

        Assert.InRange(cache.RetainedBytes, 0, budget);
        Assert.InRange(cache.Count, 0, 257);
    }
}
