using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using MarkdownRenderer.Document;
using Xunit;

namespace MarkdownRenderer.Tests;

public sealed class PagedEntryBufferFreezeTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(511)]
    [InlineData(512)]
    [InlineData(1023)]
    [InlineData(1024)]
    [InlineData(1025)]
    [InlineData(65537)]
    public void FreezePreservesIndexedAndEnumeratedOrderAtEveryPageBoundary(int count)
    {
        var buffer = new PagedEntryBuffer<int>();
        var expected = new int[count];
        for (int index = 0; index < count; index++)
        {
            expected[index] = index * 31;
            buffer.Add(expected[index]);
        }

        if (count > 0)
        {
            int changedIndex = count / 2;
            expected[changedIndex] = int.MinValue;
            buffer[changedIndex] = int.MinValue;
        }

        IReadOnlyList<int> frozen = buffer.Freeze();
        Assert.Equal(count, frozen.Count);
        Assert.Equal(count, buffer.Count);

        var enumerated = new List<int>(count);
        foreach (int value in frozen)
            enumerated.Add(value);

        Assert.Equal(expected, enumerated);
        for (int index = 0; index < count; index++)
            Assert.Equal(expected[index], frozen[index]);

        Assert.Throws<ArgumentOutOfRangeException>(() => _ = frozen[-1]);
        Assert.Throws<ArgumentOutOfRangeException>(() => _ = frozen[count]);
        Assert.Throws<ArgumentOutOfRangeException>(() => _ = buffer[-1]);
        Assert.Throws<ArgumentOutOfRangeException>(() => _ = buffer[count]);
    }

    [Fact]
    public void FreezeIsIdempotentAndRejectsMutationWithoutExposingPages()
    {
        var buffer = new PagedEntryBuffer<int>();
        buffer.Add(17);
        buffer.Add(23);

        IReadOnlyList<int> frozen = buffer.Freeze();
        Assert.Same(frozen, buffer.Freeze());
        Assert.False(frozen is int[]);
        Assert.False(frozen is IList<int>);
        Assert.False(frozen is ICollection<int>);

        Assert.Throws<InvalidOperationException>(() => buffer.Add(31));
        Assert.Throws<InvalidOperationException>(() => buffer[0] = 99);
        Assert.Equal(2, buffer.Count);
        Assert.Equal(17, buffer[0]);
        Assert.Equal(23, buffer[1]);

        int[] copy = buffer.ToArray();
        copy[0] = 101;
        Assert.Equal(17, frozen[0]);
        Assert.Equal(23, frozen[1]);
    }

    [Fact]
    public void FrozenViewSupportsConcurrentIndependentReaders()
    {
        const int count = 8193;
        var buffer = new PagedEntryBuffer<int>();
        for (int index = 0; index < count; index++)
            buffer.Add(index ^ 0x5A5A);

        IReadOnlyList<int> frozen = buffer.Freeze();
        Parallel.For(0, 4, _ =>
        {
            for (int pass = 0; pass < 2; pass++)
            {
                for (int index = 0; index < count; index++)
                    Assert.Equal(index ^ 0x5A5A, frozen[index]);

                int enumeratedCount = 0;
                foreach (int value in frozen)
                {
                    Assert.Equal(enumeratedCount ^ 0x5A5A, value);
                    enumeratedCount++;
                }
                Assert.Equal(count, enumeratedCount);
            }
        });
    }

    [Fact]
    public void GenericAndNongenericEnumeratorsMatchListPositionRules()
    {
        var buffer = new PagedEntryBuffer<int>();
        buffer.Add(17);
        buffer.Add(23);
        IReadOnlyList<int> frozen = buffer.Freeze();

        IEnumerator<int> generic = frozen.GetEnumerator();
        Assert.Equal(0, generic.Current);
        Assert.True(generic.MoveNext());
        Assert.Equal(17, generic.Current);
        Assert.True(generic.MoveNext());
        Assert.Equal(23, generic.Current);
        Assert.False(generic.MoveNext());
        Assert.Equal(0, generic.Current);
        generic.Reset();
        Assert.Equal(0, generic.Current);
        Assert.True(generic.MoveNext());
        Assert.Equal(17, generic.Current);

        IEnumerator nongeneric = ((IEnumerable)frozen).GetEnumerator();
        Assert.Throws<InvalidOperationException>(() => _ = nongeneric.Current);
        Assert.True(nongeneric.MoveNext());
        Assert.Equal(17, nongeneric.Current);
        Assert.True(nongeneric.MoveNext());
        Assert.Equal(23, nongeneric.Current);
        Assert.False(nongeneric.MoveNext());
        Assert.Throws<InvalidOperationException>(() => _ = nongeneric.Current);
        nongeneric.Reset();
        Assert.Throws<InvalidOperationException>(() => _ = nongeneric.Current);
        Assert.True(nongeneric.MoveNext());
        Assert.Equal(17, nongeneric.Current);

        var emptyBuffer = new PagedEntryBuffer<int>();
        IReadOnlyList<int> empty = emptyBuffer.Freeze();
        IEnumerator emptyEnumerator = ((IEnumerable)empty).GetEnumerator();
        Assert.Throws<InvalidOperationException>(() => _ = emptyEnumerator.Current);
        Assert.False(emptyEnumerator.MoveNext());
        Assert.Throws<InvalidOperationException>(() => _ = emptyEnumerator.Current);
        emptyEnumerator.Reset();
        Assert.Throws<InvalidOperationException>(() => _ = emptyEnumerator.Current);

        Assert.Throws<InvalidOperationException>(() => emptyBuffer.Add(1));
    }
}
