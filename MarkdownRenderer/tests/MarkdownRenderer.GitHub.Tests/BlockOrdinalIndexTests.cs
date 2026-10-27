using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using MarkdownRenderer.Accessibility;
using MarkdownRenderer.Document;
using MarkdownRenderer.Extensions;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Parsing;
using MarkdownRenderer.Theming;
using Markdig;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml;
using Windows.UI;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

public sealed class BlockOrdinalIndexTests
{
    [Fact]
    public void PagedGeneratedDomainPreservesHolesBoundariesAndFirstWins()
    {
        var index = new BlockOrdinalIndex(generatedUpperBound: 513);
        int[]?[] pages = GetField<int[]?[]?>(index, "_pages")
            ?? throw new InvalidOperationException("A valid generated domain needs a page directory.");
        Assert.Equal(2, pages.Length);
        Assert.All(pages, static page => Assert.Null(page));

        Assert.True(index.TryAdd(1, 0));
        Assert.True(index.TryAdd(512, 11));
        Assert.True(index.TryAdd(513, 12));
        Assert.False(index.TryAdd(512, 99));

        Assert.Equal(512, pages[0]!.Length);
        Assert.Single(pages[1]!);
        Assert.Equal(1, pages[0]![0]); // Zero is absent; ordinal zero is stored as one.
        Assert.Equal(12, pages[0]![511]);
        Assert.Equal(13, pages[1]![0]);
        Assert.False(index.TryGetValue(2, out _));
        Assert.False(index.ContainsKey(0));
        Assert.Equal(3, index.Count);

        AssertMapEquals(
            new Dictionary<int, int> { [1] = 0, [512] = 11, [513] = 12 },
            index);
    }

    [Fact]
    public void CustomAndOutOfDomainKeysUseSpillWithoutLosingDenseEntries()
    {
        var index = new BlockOrdinalIndex(generatedUpperBound: 513);
        Assert.True(index.TryAdd(513, 7));
        Assert.True(index.TryAdd(0, -1));
        Assert.True(index.TryAdd(-1, int.MaxValue));
        Assert.True(index.TryAdd(514, int.MinValue));
        Assert.True(index.TryAdd(int.MinValue, 42));
        Assert.True(index.TryAdd(int.MaxValue, -42));

        Assert.False(index.TryAdd(0, 123));
        Assert.False(index.TryAdd(514, 123));
        Assert.Equal(-1, index[0]);
        Assert.Equal(int.MinValue, index[514]);
        Assert.Equal(6, index.Count);

        AssertMapEquals(
            new Dictionary<int, int>
            {
                [513] = 7,
                [0] = -1,
                [-1] = int.MaxValue,
                [514] = int.MinValue,
                [int.MinValue] = 42,
                [int.MaxValue] = -42,
            },
            index);
    }

    [Fact]
    public void MaximumEncodableOrdinalUsesDenseSentinelAndUnencodableOrdinalSpillsFirstWins()
    {
        var index = new BlockOrdinalIndex(generatedUpperBound: 2);
        Assert.True(index.TryAdd(1, int.MaxValue));
        Assert.True(index.TryAdd(2, int.MaxValue - 1));
        Assert.False(index.TryAdd(1, 0));
        Assert.False(index.TryAdd(2, int.MaxValue));

        Assert.Equal(int.MaxValue, index[1]);
        Assert.Equal(int.MaxValue - 1, index[2]);
        Assert.Equal(2, index.Count);
        Assert.Equal(int.MaxValue, GetField<Dictionary<int, int>>(index, "_spill")[1]);
        int[]?[] pages = GetField<int[]?[]?>(index, "_pages")
            ?? throw new InvalidOperationException("A valid generated domain needs a page directory.");
        Assert.Equal(int.MaxValue, pages[0]![1]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(BlockOrdinalIndex.MaximumDenseGeneratedUpperBound + 1)]
    public void MissingOrOverCapGeneratedBoundUsesDictionaryFallback(int upperBound)
    {
        var index = new BlockOrdinalIndex(upperBound);
        Assert.Null(GetField<int[]?[]?>(index, "_pages"));
        Assert.True(index.TryAdd(1, 0));
        Assert.True(index.TryAdd(int.MinValue, int.MaxValue));
        Assert.False(index.TryAdd(1, 1));
        Assert.Equal(2, index.Count);
        AssertMapEquals(new Dictionary<int, int> { [1] = 0, [int.MinValue] = int.MaxValue }, index);
    }

    [Fact]
    public void MaximumGeneratedBoundUsesSparseDirectoryWithExactFinalPage()
    {
        int upperBound = BlockOrdinalIndex.MaximumDenseGeneratedUpperBound;
        var index = new BlockOrdinalIndex(upperBound);
        int[]?[] pages = GetField<int[]?[]?>(index, "_pages")
            ?? throw new InvalidOperationException("The capped generated domain needs a page directory.");
        Assert.Equal(upperBound / 512, pages.Length);
        Assert.All(pages, static page => Assert.Null(page));

        Assert.True(index.TryAdd(upperBound, 0));
        Assert.Equal(512, pages[^1]!.Length);
        Assert.Equal(1, pages[^1]![^1]);
        Assert.True(index.TryGetValue(upperBound, out int ordinal));
        Assert.Equal(0, ordinal);
        Assert.False(index.TryGetValue(upperBound - 1, out _));
    }

    [Fact]
    public void ReadersCanConcurrentlyQueryFrozenDenseAndSpillEntries()
    {
        var index = new BlockOrdinalIndex(generatedUpperBound: 2048);
        var expected = new Dictionary<int, int>();
        for (int key = 1; key <= 2048; key += 3)
        {
            int ordinal = key % 7 == 0 ? int.MaxValue : key - 1;
            Assert.True(index.TryAdd(key, ordinal));
            expected.Add(key, ordinal);
        }
        int[] spillKeys = [int.MinValue, int.MaxValue, 0, -7, 4096];
        for (int spillIndex = 0; spillIndex < spillKeys.Length; spillIndex++)
        {
            int key = spillKeys[spillIndex];
            int value = -5 - spillIndex;
            Assert.True(index.TryAdd(key, value));
            expected.Add(key, value);
        }
        IReadOnlyDictionary<int, int> frozenView = index.Freeze();
        Assert.Same(index, frozenView);

        int mismatches = 0;
        Parallel.For(0, 8, worker =>
        {
            for (int iteration = 0; iteration < 64; iteration++)
            {
                foreach (KeyValuePair<int, int> entry in expected)
                {
                    if (!index.TryGetValue(entry.Key, out int value) || value != entry.Value)
                        Interlocked.Increment(ref mismatches);
                }

                if (index.TryGetValue(2049, out _))
                    Interlocked.Increment(ref mismatches);

                int enumerated = 0;
                foreach (KeyValuePair<int, int> entry in index)
                {
                    if (!expected.TryGetValue(entry.Key, out int value) || value != entry.Value)
                        Interlocked.Increment(ref mismatches);
                    enumerated++;
                }
                if (enumerated != expected.Count || index.Count != expected.Count)
                    Interlocked.Increment(ref mismatches);
            }
        });

        Assert.Equal(0, mismatches);
    }

    [Fact]
    public void FreezeIsIdempotentAndRejectsAllLaterMutationsWithoutChangingContents()
    {
        var index = new BlockOrdinalIndex(generatedUpperBound: 4);
        Assert.True(index.TryAdd(1, 0));
        Assert.True(index.TryAdd(-3, int.MaxValue));
        IReadOnlyDictionary<int, int> frozen = index.Freeze();

        Assert.Same(frozen, index.Freeze());
        var expected = new Dictionary<int, int>(frozen);
        Assert.Throws<InvalidOperationException>(() => index.TryAdd(1, 99));
        Assert.Throws<InvalidOperationException>(() => index.TryAdd(2, 1));
        Assert.Throws<InvalidOperationException>(() => index.TryAdd(int.MinValue, -1));
        AssertMapEquals(expected, frozen);
    }

    [Fact]
    public void EagerAndLazyBuildersCaptureTheContextUpperBoundForTheirSnapshots()
    {
        const string source = "first paragraph\n\nsecond paragraph";
        MarkdownLayoutContext context = CreateContext(source);
        var builder = new LayoutBuilder(context);

        using LayoutSnapshot eager = builder.Build(Markdown.Parse(source), availableWidth: 360);
        int eagerUpperBound = context.GeneratedBlockIndexUpperBound;
        Assert.True(eagerUpperBound > 0);
        Assert.Equal(eagerUpperBound, GetField<int>(eager, "_generatedBlockIndexUpperBound"));
        AssertDenseIndexBound(eager, eagerUpperBound);

        using LayoutSnapshot lazy = builder.BuildLazy(
            Markdown.Parse(source),
            availableWidth: 360,
            viewportTop: 0,
            viewportHeight: 120,
            overscan: 0,
            cancellationToken: CancellationToken.None);
        int lazyUpperBound = context.GeneratedBlockIndexUpperBound;
        Assert.True(lazyUpperBound > eagerUpperBound);
        Assert.Equal(lazyUpperBound, GetField<int>(lazy, "_generatedBlockIndexUpperBound"));
        AssertDenseIndexBound(lazy, lazyUpperBound);
    }

    private static void AssertDenseIndexBound(LayoutSnapshot snapshot, int upperBound)
    {
        IReadOnlyDictionary<int, int> map = ReadMap(snapshot);
        BlockOrdinalIndex index = Assert.IsType<BlockOrdinalIndex>(map);
        Assert.Equal(
            upperBound is > 0 and <= BlockOrdinalIndex.MaximumDenseGeneratedUpperBound ? upperBound : 0,
            GetField<int>(index, "_denseGeneratedUpperBound"));
        Assert.Throws<InvalidOperationException>(() => index.TryAdd(int.MinValue, -1));
        foreach (BlockBox block in snapshot.Blocks)
        {
            Assert.True(map.TryGetValue(block.BlockIndex, out int ordinal));
            Assert.True(ordinal >= 0 && ordinal < snapshot.Blocks.Count);
        }
    }

    private static MarkdownLayoutContext CreateContext(string source)
    {
        var style = new ElementStyle { FontFamily = "Segoe UI", FontSize = 16 };
        var theme = new ThemeSnapshot(
            new Dictionary<string, ElementStyle>(StringComparer.Ordinal)
            {
                [MarkdownElementKeys.Body] = style,
                [MarkdownElementKeys.CodeBlock] = style,
                [MarkdownElementKeys.Link] = style,
            },
            new Dictionary<string, ElementStyleOverride>(),
            Color.FromArgb(255, 0, 0, 0),
            Color.FromArgb(255, 0, 0, 0),
            Color.FromArgb(255, 0, 0, 0),
            Color.FromArgb(255, 0, 0, 0),
            isDark: false,
            isHighContrast: false,
            textScaleFactor: 1);
        var sourceMap = new MarkdownSourceMap(source);
        return new MarkdownLayoutContext(
            CanvasDevice.GetSharedDevice(),
            theme,
            sourceMap,
            new MarkdownExtensionRegistry(),
            FlowDirection.LeftToRight);
    }

    private static IReadOnlyDictionary<int, int> ReadMap(LayoutSnapshot snapshot)
        => Assert.IsAssignableFrom<IReadOnlyDictionary<int, int>>(
            typeof(LayoutSnapshot).GetField("_topLevelOrdinalByBlockIndex", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(snapshot));

    private static void AssertMapEquals(
        IReadOnlyDictionary<int, int> expected,
        IReadOnlyDictionary<int, int> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        Assert.Equal(expected.Keys.Order(), actual.Keys.Order());
        Assert.Equal(expected.Values.Order(), actual.Values.Order());
        foreach (KeyValuePair<int, int> entry in expected)
        {
            Assert.True(actual.TryGetValue(entry.Key, out int value));
            Assert.Equal(entry.Value, value);
        }

        var actualKeys = new HashSet<int>();
        foreach (KeyValuePair<int, int> entry in actual)
        {
            Assert.True(actualKeys.Add(entry.Key));
            Assert.True(expected.TryGetValue(entry.Key, out int value));
            Assert.Equal(value, entry.Value);
        }
        Assert.Equal(expected.Count, actualKeys.Count);
        Assert.Throws<KeyNotFoundException>(() => _ = actual[int.MinValue + 1]);
    }

    private static T GetField<T>(object instance, string name)
    {
        FieldInfo field = instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Field '{name}' was not found.");
        object? value = field.GetValue(instance);
        if (value is null)
        {
            if (default(T) is null)
                return default!;
            throw new InvalidOperationException($"Field '{name}' unexpectedly contained null.");
        }
        return Assert.IsType<T>(value);
    }
}
