using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using MarkdownRenderer.CodeBlocks;
using Windows.UI;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

public sealed class CodeBlockHighlightResultSnapshotTests
{
    [Fact]
    public void CopyDetachesProviderCallbacksAndLaterMutations()
    {
        CodeBlockHighlightSpan original = Span(0, 2, 10);
        CodeBlockHighlightSpan second = Span(3, 4, 20);
        var providerSpans = new CallbackSpanList([original, second]);
        var providerResult = new CodeBlockHighlightResult(providerSpans);

        CodeBlockHighlightResult snapshot = CodeBlockHighlightResultSnapshot.CopyProviderResult(
            providerResult,
            CancellationToken.None);

        Assert.Equal(1, providerSpans.CountReadCount);
        Assert.Equal(2, providerSpans.IndexReadCount);
        Assert.Equal(0, providerSpans.EnumeratorReadCount);
        Assert.Equal(new[] { original, second }, snapshot.Spans);

        providerSpans.ReplaceAll([Span(50, 60, 30)]);
        Assert.Equal(new[] { original, second }, snapshot.Spans);
        Assert.Equal(1, providerSpans.CountReadCount);
        Assert.Equal(2, providerSpans.IndexReadCount);

        IList<CodeBlockHighlightSpan> ownedSpans = Assert.IsAssignableFrom<IList<CodeBlockHighlightSpan>>(
            snapshot.Spans);
        Assert.True(ownedSpans.IsReadOnly);
        Assert.Throws<NotSupportedException>(() => ownedSpans[0] = Span(70, 80, 40));
    }

    [Fact]
    public void CopyPropagatesProviderCollectionFailuresForTheWorkerErrorPath()
    {
        var failure = new InvalidOperationException("provider span accessor failed");
        var providerSpans = new CallbackSpanList([Span(0, 1, 1), Span(1, 1, 2)])
        {
            ThrowOnIndex = 1,
            IndexFailure = failure,
        };

        InvalidOperationException actual = Assert.Throws<InvalidOperationException>(() =>
            CodeBlockHighlightResultSnapshot.CopyProviderResult(
                new CodeBlockHighlightResult(providerSpans),
                CancellationToken.None));

        Assert.Same(failure, actual);
        Assert.Equal(2, providerSpans.IndexReadCount);
        Assert.Equal(0, providerSpans.EnumeratorReadCount);
    }

    [Fact]
    public void CopyObservesCancellationDuringProviderCollectionAccess()
    {
        using var cancellation = new CancellationTokenSource();
        var providerSpans = new CallbackSpanList(Enumerable.Repeat(Span(0, 1, 1), 512).ToArray())
        {
            AfterIndexRead = index =>
            {
                if (index == 1)
                    cancellation.Cancel();
            },
        };

        OperationCanceledException actual = Assert.Throws<OperationCanceledException>(() =>
            CodeBlockHighlightResultSnapshot.CopyProviderResult(
                new CodeBlockHighlightResult(providerSpans),
                cancellation.Token));

        Assert.Equal(cancellation.Token, actual.CancellationToken);
        Assert.Equal(2, providerSpans.IndexReadCount);
        Assert.Equal(0, providerSpans.EnumeratorReadCount);
    }

    private static CodeBlockHighlightSpan Span(int start, int length, byte blue)
        => new(start, length, Color.FromArgb(255, 1, 2, blue));

    private sealed class CallbackSpanList : IReadOnlyList<CodeBlockHighlightSpan>
    {
        private readonly List<CodeBlockHighlightSpan> _spans;

        internal CallbackSpanList(IEnumerable<CodeBlockHighlightSpan> spans)
            => _spans = new List<CodeBlockHighlightSpan>(spans);

        internal int CountReadCount { get; private set; }
        internal int IndexReadCount { get; private set; }
        internal int EnumeratorReadCount { get; private set; }
        internal int ThrowOnIndex { get; init; } = -1;
        internal Exception? IndexFailure { get; init; }
        internal Action<int>? AfterIndexRead { get; init; }

        public int Count
        {
            get
            {
                CountReadCount++;
                return _spans.Count;
            }
        }

        public CodeBlockHighlightSpan this[int index]
        {
            get
            {
                IndexReadCount++;
                if (index == ThrowOnIndex)
                    throw IndexFailure ?? new InvalidOperationException("index accessor failed");

                CodeBlockHighlightSpan span = _spans[index];
                AfterIndexRead?.Invoke(index);
                return span;
            }
        }

        internal void ReplaceAll(IEnumerable<CodeBlockHighlightSpan> spans)
        {
            _spans.Clear();
            _spans.AddRange(spans);
        }

        public IEnumerator<CodeBlockHighlightSpan> GetEnumerator()
        {
            EnumeratorReadCount++;
            throw new InvalidOperationException("Provider enumerators must not escape the copy boundary.");
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
