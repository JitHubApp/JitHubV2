using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MarkdownRenderer.Document;
using Xunit;

namespace MarkdownRenderer.Tests;

public sealed class PagedEntryBufferTests
{
    [Fact]
    public void EmptyAndFirstPageUseStableFlattenedSnapshots()
    {
        var buffer = new PagedEntryBuffer<int>();
        Assert.Empty(buffer.ToArray());
        Assert.Equal(0, buffer.Count);

        buffer.Add(17);
        int[] oneEntry = buffer.ToArray();
        Assert.Equal(new[] { 17 }, oneEntry);

        buffer.Add(23);
        buffer.Add(31);
        buffer.Add(47);
        Assert.Equal(new[] { 17 }, oneEntry);
        Assert.Equal(new[] { 17, 23, 31, 47 }, buffer.ToArray());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(16)]
    [InlineData(17)]
    [InlineData(512)]
    [InlineData(513)]
    [InlineData(1024)]
    [InlineData(1025)]
    [InlineData(2048)]
    [InlineData(2049)]
    [InlineData(4095)]
    [InlineData(4096)]
    [InlineData(4097)]
    [InlineData(8191)]
    [InlineData(8192)]
    [InlineData(8193)]
    public void AppendIndexerAndFlattenPreserveEveryPageBoundary(int count)
    {
        var buffer = new PagedEntryBuffer<int>();
        for (int index = 0; index < count; index++)
            buffer.Add(index * 3);

        Assert.Equal(count, buffer.Count);
        for (int index = 0; index < count; index++)
            Assert.Equal(index * 3, buffer[index]);

        if (count > 0)
        {
            int editedIndex = count / 2;
            buffer[editedIndex] = -editedIndex;
        }

        int[] snapshot = buffer.ToArray();
        Assert.Equal(count, snapshot.Length);
        for (int index = 0; index < count; index++)
            Assert.Equal(index == count / 2 && count > 0 ? -index : index * 3, snapshot[index]);

        buffer.Add(-1);
        Assert.Equal(count + 1, buffer.Count);
        Assert.Equal(-1, buffer[count]);
        Assert.Equal(count, snapshot.Length);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    public void IndexerGetRejectsNegativeAndCountEvenWithFirstPageSlack(int index)
    {
        var buffer = new PagedEntryBuffer<int>();
        buffer.Add(42); // The first backing page has spare slots, but Count is one.

        Assert.Throws<ArgumentOutOfRangeException>(() => _ = buffer[index]);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    public void IndexerSetRejectsNegativeAndCountEvenWithFirstPageSlack(int index)
    {
        var buffer = new PagedEntryBuffer<int>();
        buffer.Add(42); // The first backing page has spare slots, but Count is one.

        Assert.Throws<ArgumentOutOfRangeException>(() => buffer[index] = 99);
        Assert.Equal(42, buffer[0]);
    }

    [Fact]
    public void SourceMapEntriesRemainFrozenAcrossAppendAndAffixMutation()
    {
        const string source = "##A!!";
        var map = new MarkdownSourceMap(source);
        map.Add(0, 0, 1, new SourceSpan(2, 1));

        IReadOnlyList<MarkdownSourceMap.Entry> beforeMutation = map.Entries;
        map.AddSourceAffixesToBlock(0, sourceStart: 0, sourceEnd: 3);
        Assert.Single(map.Entries);
        map.Add(1, 0, 1, new SourceSpan(4, 1));

        MarkdownSourceMap.Entry oldEntry = Assert.Single(beforeMutation);
        Assert.Equal(new SourceSpan(2, 1), oldEntry.Span);
        Assert.Equal(2, map.Entries.Count);
        Assert.Equal(new SourceSpan(0, 3), map.Entries[0].Span);
    }

    [Fact]
    public void OutOfOrderEqualDocumentKeysKeepInsertionOrderAfterSortedSnapshot()
    {
        const string source = "0123456789abcdef";
        var map = new MarkdownSourceMap(source);
        map.Add(5, 2, 1, new SourceSpan(8, 1));
        map.Add(2, 0, 1, new SourceSpan(0, 1));
        map.Add(5, 2, 1, new SourceSpan(9, 1));

        MarkdownSourceMap.Entry[] snapshot = map.Entries.ToArray();
        MarkdownSourceMap.Entry[] tied = snapshot
            .Where(entry => entry.BlockIndex == 5 && entry.InlineIndex == 2)
            .ToArray();

        Assert.Equal(2, tied.Length);
        Assert.Equal(new[] { 0, 2 }, tied.Select(entry => entry.InsertionOrder));
        Assert.Equal(new[] { 8, 9 }, tied.Select(entry => entry.Span.Start));
    }

    [Fact]
    public async Task FirstEntrySnapshotRemainsCoherentWhileAppendsRace()
    {
        const int appendCount = 4096;
        var map = new MarkdownSourceMap(new string('x', appendCount));
        using var start = new ManualResetEventSlim(initialState: false);
        var retainedViews = new ConcurrentBag<IReadOnlyList<MarkdownSourceMap.Entry>>();

        // No Entries access occurs before these workers start, so their first
        // calls race the first append and the initial cached-index creation.
        Task writer = Task.Factory.StartNew(
            () =>
            {
                start.Wait();
                for (int index = 0; index < appendCount; index++)
                {
                    map.Add(7, 3, renderedLength: 1, span: new SourceSpan(index, 1));
                    if ((index & 31) == 0)
                        Thread.Yield();
                }
            },
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

        Task[] readers = Enumerable.Range(0, 3)
            .Select(_ => Task.Factory.StartNew(
                () =>
                {
                    start.Wait();
                    for (int observation = 0; observation < 64; observation++)
                    {
                        IReadOnlyList<MarkdownSourceMap.Entry> view = map.Entries;
                        AssertInsertionOrderPrefix(view);
                        retainedViews.Add(view);
                        Thread.Yield();
                    }
                },
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default))
            .ToArray();

        start.Set();
        await Task.WhenAll(readers.Append(writer));

        IReadOnlyList<MarkdownSourceMap.Entry> finalView = map.Entries;
        Assert.Equal(appendCount, finalView.Count);
        AssertInsertionOrderPrefix(finalView);

        // A view acquired during mutation wraps its own immutable Entry[] and
        // must not change when later appends invalidate the source-map cache.
        foreach (IReadOnlyList<MarkdownSourceMap.Entry> retainedView in retainedViews)
            AssertInsertionOrderPrefix(retainedView);
    }

    private static void AssertInsertionOrderPrefix(IReadOnlyList<MarkdownSourceMap.Entry> entries)
    {
        for (int index = 0; index < entries.Count; index++)
        {
            MarkdownSourceMap.Entry entry = entries[index];
            Assert.Equal(index, entry.InsertionOrder);
            Assert.Equal(7, entry.BlockIndex);
            Assert.Equal(3, entry.InlineIndex);
            Assert.Equal(index, entry.Span.Start);
            Assert.Equal(1, entry.Span.Length);
        }
    }
}
