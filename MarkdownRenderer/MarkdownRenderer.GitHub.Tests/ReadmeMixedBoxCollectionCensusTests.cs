using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Markdig.Syntax;
using MarkdownRenderer.Controls;
using MarkdownRenderer.Document;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Parsing;
using MarkdownRenderer.PerformanceHarness;
using MarkdownRenderer.Theming;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml;
using Windows.UI;
using Xunit;
using Xunit.Abstractions;

namespace MarkdownRenderer.GitHub.Tests;

[CollectionDefinition(ReadmeMixedLayoutFixture.CollectionName, DisableParallelization = true)]
public sealed class ReadmeMixedLayoutFixtureCollection
{
}

[Collection(ReadmeMixedLayoutFixture.CollectionName)]
public sealed class ReadmeMixedBoxCollectionCensusTests
{
    private readonly ITestOutputHelper _output;

    public ReadmeMixedBoxCollectionCensusTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task ReadmeMixedOneMiBLazyGraphReportsExactInlineRunAndStackChildHistograms()
    {
        await using var fixture = await ReadmeMixedLayoutFixture.CreateAsync();
        Census census = CountGraph(fixture.Snapshot, fixture.CancellationToken);
        Assert.Equal(fixture.Snapshot.Blocks.Count, census.RootsVisited);
        Assert.Equal(census.UniqueBoxesVisited, census.BoxKinds.Values.Sum());
        Assert.Equal(census.InlineContainerCount, census.InlineRunCounts.Total);
        Assert.Equal(census.StackBoxCount, census.StackChildCounts.Total);
        Assert.True(fixture.Snapshot.Blocks.Count > 1);
        Assert.True(census.InlineContainerCount > 0);
        Assert.True(census.StackBoxCount > 0);

        _output.WriteLine(
            $"diagnostic=structural census only; theme=neutral test snapshot; " +
            $"not WinUI first-presentation or release evidence; " +
            $"corpus={PerformanceDocumentFactory.ReadmeMixedCorpus}; " +
            $"sourceUtf16Bytes={ReadmeMixedLayoutFixture.CorpusUtf16Bytes}; " +
            $"parsedRoots={fixture.ParsedDocument.Count}; " +
            $"layoutRoots={fixture.Snapshot.Blocks.Count}; uniqueBoxes={census.UniqueBoxesVisited}; " +
            $"inlineContainers={census.InlineContainerCount}; " +
            $"inlineRuns(0/1/2/3+)={census.InlineRunCounts}; " +
            $"stackBoxes={census.StackBoxCount}; " +
            $"stackChildren(0/1/2/3+)={census.StackChildCounts}; " +
            $"boxKinds={string.Join(",", census.BoxKinds.OrderBy(static pair => pair.Key)
                .Select(static pair => $"{pair.Key}:{pair.Value}"))}");

        using var cancelDuringTraversal = new CancellationTokenSource();
        Assert.Throws<OperationCanceledException>(() => CountGraph(
            fixture.Snapshot,
            cancelDuringTraversal.Token,
            cancelAfterFirstBox: cancelDuringTraversal.Cancel));
    }

    private static Census CountGraph(
        LayoutSnapshot snapshot,
        CancellationToken cancellationToken,
        Action? cancelAfterFirstBox = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var pending = new Stack<(BlockBox Box, bool IsRoot)>(snapshot.Blocks.Count);
        for (int index = snapshot.Blocks.Count - 1; index >= 0; index--)
            pending.Push((snapshot.Blocks[index], true));

        var visited = new HashSet<BlockBox>(ReferenceEqualityComparer.Instance);
        var kinds = new Dictionary<string, int>(StringComparer.Ordinal);
        var inlineRunCounts = new Histogram();
        var stackChildCounts = new Histogram();
        int rootsVisited = 0;
        int inlineContainerCount = 0;
        int stackBoxCount = 0;

        while (pending.Count != 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            (BlockBox current, bool isRoot) = pending.Pop();
            if (!visited.Add(current))
            {
                throw new InvalidOperationException(
                    "The lazy layout graph exposed one BlockBox through more than one owner path.");
            }

            if (isRoot)
                rootsVisited++;
            string kind = current.GetType().Name;
            kinds.TryGetValue(kind, out int count);
            kinds[kind] = count + 1;

            switch (current)
            {
                case InlineContainerBox inline:
                    inlineContainerCount++;
                    inlineRunCounts.Add(inline.Runs.Count);
                    foreach (InlineRun run in inline.Runs)
                    {
                        if (run is InlineImageRun image)
                            pending.Push((image.Image, false));
                    }
                    break;

                case StackBox stack:
                    stackBoxCount++;
                    stackChildCounts.Add(stack.Children.Count);
                    for (int index = stack.Children.Count - 1; index >= 0; index--)
                        pending.Push((stack.Children[index], false));
                    break;

                case ListItemBox listItem:
                    pending.Push((listItem.Content, false));
                    pending.Push((listItem.Marker, false));
                    break;

                case TableBox table:
                    foreach (InlineContainerBox cell in table.GetCellBoxes())
                        pending.Push((cell, false));
                    break;

                case CodeBlockBox code:
                    for (int index = code.Chunks.Count - 1; index >= 0; index--)
                        pending.Push((code.Chunks[index], false));
                    break;
            }

            if (visited.Count == 1)
                cancelAfterFirstBox?.Invoke();
        }

        return new Census(
            rootsVisited,
            visited.Count,
            inlineContainerCount,
            stackBoxCount,
            kinds,
            inlineRunCounts,
            stackChildCounts);
    }

    private sealed record Census(
        int RootsVisited,
        int UniqueBoxesVisited,
        int InlineContainerCount,
        int StackBoxCount,
        IReadOnlyDictionary<string, int> BoxKinds,
        Histogram InlineRunCounts,
        Histogram StackChildCounts);

    private sealed class Histogram
    {
        internal int Zero { get; private set; }
        internal int One { get; private set; }
        internal int Two { get; private set; }
        internal int ThreeOrMore { get; private set; }

        internal int Total => Zero + One + Two + ThreeOrMore;

        internal void Add(int value)
        {
            switch (value)
            {
                case 0:
                    Zero++;
                    break;
                case 1:
                    One++;
                    break;
                case 2:
                    Two++;
                    break;
                default:
                    ThreeOrMore++;
                    break;
            }
        }

        public override string ToString() => $"{Zero}/{One}/{Two}/{ThreeOrMore}";
    }
}

internal sealed class ReadmeMixedLayoutFixture : IAsyncDisposable
{
    internal const string CollectionName = "Readme mixed layout diagnostic";
    internal const int CorpusUtf16Bytes = 1024 * 1024;
    private const float LayoutWidth = 1100;
    private const double ViewportHeight = 760;
    private const double LayoutOverscan = 2400;

    private readonly CanvasDevice _device;
    private readonly global::MarkdownRenderer.MarkdownEngine _engine;
    private readonly CancellationTokenSource _cancellation;
    private bool _disposed;

    private ReadmeMixedLayoutFixture(
        CanvasDevice device,
        global::MarkdownRenderer.MarkdownEngine engine,
        CancellationTokenSource cancellation,
        string source,
        MarkdownRenderer.Document.MarkdownDocument semanticDocument,
        Markdig.Syntax.MarkdownDocument parsedDocument,
        MarkdownLayoutContext context,
        LayoutSnapshot snapshot)
    {
        _device = device;
        _engine = engine;
        _cancellation = cancellation;
        Source = source;
        SemanticDocument = semanticDocument;
        ParsedDocument = parsedDocument;
        Context = context;
        Snapshot = snapshot;
    }

    internal string Source { get; }
    internal MarkdownRenderer.Document.MarkdownDocument SemanticDocument { get; }
    internal Markdig.Syntax.MarkdownDocument ParsedDocument { get; }
    internal MarkdownLayoutContext Context { get; }
    internal LayoutSnapshot Snapshot { get; }
    internal CancellationToken CancellationToken => _cancellation.Token;

    internal static async Task<ReadmeMixedLayoutFixture> CreateAsync()
    {
        CanvasDevice? device = null;
        global::MarkdownRenderer.MarkdownEngine? engine = null;
        var cancellation = new CancellationTokenSource();
        MarkdownLayoutContext? context = null;
        LayoutSnapshot? snapshot = null;
        try
        {
            device = new CanvasDevice(forceSoftwareRenderer: true);
            engine = new MarkdownEngineBuilder()
                .UseProfile(MarkdownProfiles.GfmStrict)
                .Build();

            string source = PerformanceDocumentFactory.Create(CorpusUtf16Bytes, CorpusUtf16Bytes);
            if (checked(source.Length * sizeof(char)) != CorpusUtf16Bytes ||
                PerformanceDocumentFactory.GetCorpusName(CorpusUtf16Bytes) != PerformanceDocumentFactory.ReadmeMixedCorpus)
            {
                throw new InvalidOperationException("The linked performance fixture did not produce the expected 1 MiB corpus.");
            }

            MarkdownRenderer.Document.MarkdownDocument semanticDocument = await engine.ParseAsync(
                source,
                cancellation.Token);
            Markdig.Syntax.MarkdownDocument parsed = semanticDocument.ParsedDocument ??
                throw new InvalidOperationException("The performance engine did not retain its parsed document.");
            context = new MarkdownLayoutContext(
                device,
                CreateNeutralThemeSnapshot(),
                new MarkdownSourceMap(source),
                new MarkdownExtensionRegistry(),
                FlowDirection.LeftToRight,
                language: "en-US")
            {
                CancellationToken = cancellation.Token,
            };

            snapshot = new LayoutBuilder(context, semanticDocument: semanticDocument)
                .BuildLazyForViewport(
                    parsed,
                    LayoutWidth,
                    viewportTop: 0,
                    viewportHeight: ViewportHeight,
                    overscan: LayoutOverscan,
                    cancellation.Token);

            var fixture = new ReadmeMixedLayoutFixture(
                device,
                engine,
                cancellation,
                source,
                semanticDocument,
                parsed,
                context,
                snapshot);
            device = null;
            engine = null;
            snapshot = null;
            context = null;
            return fixture;
        }
        catch
        {
            try
            {
                if (snapshot is not null)
                    await snapshot.Retire().WaitAsync(TimeSpan.FromSeconds(30));
            }
            finally
            {
                try
                {
                    if (context is not null)
                        await context.DrainOwnedRetirementsAsync().WaitAsync(TimeSpan.FromSeconds(30));
                }
                finally
                {
                    cancellation.Dispose();
                    engine?.Dispose();
                    device?.Dispose();
                }
            }

            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;
        try
        {
            await Snapshot.Retire().WaitAsync(TimeSpan.FromSeconds(30));
        }
        finally
        {
            try
            {
                await Context.DrainOwnedRetirementsAsync().WaitAsync(TimeSpan.FromSeconds(30));
            }
            finally
            {
                _cancellation.Dispose();
                _engine.Dispose();
                _device.Dispose();
            }
        }
    }

    private static ThemeSnapshot CreateNeutralThemeSnapshot()
    {
        var body = new ElementStyle { FontFamily = "Segoe UI", FontSize = 16 };
        var styles = new Dictionary<string, ElementStyle>(StringComparer.Ordinal)
        {
            [MarkdownElementKeys.Body] = body,
            [MarkdownElementKeys.Heading1] = body,
            [MarkdownElementKeys.Heading2] = body,
            [MarkdownElementKeys.Heading3] = body,
            [MarkdownElementKeys.Heading4] = body,
            [MarkdownElementKeys.Heading5] = body,
            [MarkdownElementKeys.Heading6] = body,
            [MarkdownElementKeys.CodeInline] = body,
            [MarkdownElementKeys.CodeBlock] = body,
            [MarkdownElementKeys.CodeBlockHeader] = body,
            [MarkdownElementKeys.CodeBlockLanguage] = body,
            [MarkdownElementKeys.CodeBlockGutter] = body,
            [MarkdownElementKeys.CodeBlockLineNumber] = body,
            [MarkdownElementKeys.Quote] = body,
            [MarkdownElementKeys.Link] = body,
            [MarkdownElementKeys.Strong] = body,
            [MarkdownElementKeys.Emphasis] = body,
            [MarkdownElementKeys.Strikethrough] = body,
            [MarkdownElementKeys.List] = body,
            [MarkdownElementKeys.ListMarker] = body,
            [MarkdownElementKeys.Table] = body,
            [MarkdownElementKeys.TableHeader] = body,
            [MarkdownElementKeys.TableCell] = body,
        };
        Color transparent = Color.FromArgb(0, 0, 0, 0);
        return new ThemeSnapshot(
            styles,
            new Dictionary<string, ElementStyleOverride>(),
            transparent,
            transparent,
            transparent,
            transparent,
            isDark: false,
            isHighContrast: false,
            textScaleFactor: 1);
    }
}
