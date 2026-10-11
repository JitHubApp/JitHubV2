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

    [Fact]
    public void WorkerCopiesProviderResultBeforeDispatcherPublicationAndCacheAccess()
    {
        string control = ReadRepositorySource(
            "MarkdownRenderer", "MarkdownRenderer", "Controls", "MarkdownRendererControl.cs");
        string scheduler = GetMethodBody(control, "private void ScheduleVisibleCodeBlockHighlighting()");
        string worker = GetMethodBody(control, "private async Task HighlightCodeBlockAsync(");

        int workerMode = scheduler.IndexOf(
            "bool runOnWorker = performanceSession is not null;",
            StringComparison.Ordinal);
        int workerDispatch = scheduler.IndexOf("() => runOnWorker", StringComparison.Ordinal);
        int workerTaskRun = scheduler.IndexOf("? Task.Run(", workerDispatch, StringComparison.Ordinal);
        int sessionlessCall = scheduler.IndexOf(": HighlightCodeBlockAsync(", workerTaskRun, StringComparison.Ordinal);
        int providerCall = worker.IndexOf("await highlighter.HighlightAsync(", StringComparison.Ordinal);
        int uiThreadGuard = worker.IndexOf("dispatcher.HasThreadAccess", providerCall, StringComparison.Ordinal);
        int uiThreadTaskRun = worker.IndexOf("? await Task.Run(", uiThreadGuard, StringComparison.Ordinal);
        int copy = worker.IndexOf(
            "CodeBlockHighlightResultSnapshot.CopyProviderResult(",
            uiThreadTaskRun,
            StringComparison.Ordinal);
        int workerDirectCopy = worker.IndexOf(
            ": CodeBlockHighlightResultSnapshot.CopyProviderResult(",
            copy,
            StringComparison.Ordinal);
        int canceledAfterProvider = worker.IndexOf(
            "if (workToken.IsCancellationRequested)",
            providerCall,
            StringComparison.Ordinal);
        int canceledAfterCopy = worker.IndexOf(
            "if (workToken.IsCancellationRequested)",
            workerDirectCopy,
            StringComparison.Ordinal);
        int enqueue = worker.IndexOf("publicationQueued = dispatcher.TryEnqueue(", StringComparison.Ordinal);
        int cache = worker.IndexOf("_codeBlockHighlightCache.Set(key, result);", StringComparison.Ordinal);

        Assert.True(workerMode >= 0 && workerDispatch > workerMode);
        Assert.True(workerTaskRun > workerDispatch && sessionlessCall > workerTaskRun);
        Assert.True(providerCall >= 0);
        Assert.True(canceledAfterProvider > providerCall && uiThreadGuard > canceledAfterProvider);
        Assert.True(uiThreadTaskRun > uiThreadGuard && copy > uiThreadTaskRun);
        Assert.True(workerDirectCopy > copy);
        Assert.True(canceledAfterCopy > copy && enqueue > canceledAfterCopy);
        Assert.True(copy > providerCall && enqueue > copy && cache > enqueue);
        Assert.Contains("catch (Exception ex)", worker, StringComparison.Ordinal);
        Assert.Contains("RemoveCodeBlockHighlightInFlight(key, generation)", worker, StringComparison.Ordinal);
    }

    private static CodeBlockHighlightSpan Span(int start, int length, byte blue)
        => new(start, length, Color.FromArgb(255, 1, 2, blue));

    private static string GetMethodBody(string source, string signature)
    {
        int member = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(member >= 0, $"Could not find method '{signature}'.");
        int bodyStart = source.IndexOf('{', member + signature.Length);
        Assert.True(bodyStart >= 0, $"Could not find a body for '{signature}'.");

        int depth = 0;
        for (int index = bodyStart; index < source.Length; index++)
        {
            if (source[index] == '{')
                depth++;
            else if (source[index] == '}' && --depth == 0)
                return source[bodyStart..(index + 1)];
        }

        throw new InvalidOperationException($"Could not find the end of '{signature}'.");
    }

    private static string ReadRepositorySource(params string[] pathParts)
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            string path = directory.FullName;
            foreach (string part in pathParts)
                path = Path.Combine(path, part);
            if (File.Exists(path))
                return File.ReadAllText(path);
        }

        throw new FileNotFoundException(
            $"Could not find repository source {Path.Combine(pathParts)}.");
    }

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
