using System.Reflection;
using MarkdownRenderer.Document;
using MarkdownRenderer.Layout.Internal.Boxes;
using MarkdownRenderer.Layout.Internal.Context;
using MarkdownRenderer.Layout.Internal.Inlines;
using MarkdownRenderer.Layout.Internal.Inlines.Runs;
using MarkdownRenderer.Layout.Internal.Snapshots;
using MarkdownRenderer.Parsing.Internal;
using MarkdownRenderer.Theming;
using MarkdownRenderer.Theming.Internal;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml;
using Windows.UI;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests.Layout.Internal.Inlines.Styling;

public sealed class LazyInlineRunStyleCacheTests
{

    [Fact]
    public void OffscreenRunsRetainOrdinalsAndSourceMappingsWithoutStyleSlots()
    {
        InlineContainerBox box = CreateBox();
        Assert.Null(box.ResolvedRunStyles);
        Assert.Equal(new[] { 0, 1, 2 }, box.Runs.Select(run => run.InlineIndex));
        Assert.Equal("first", box.Context.SourceMap.Slice(new DocumentRange(
            new DocumentPosition(1, 0, 0), new DocumentPosition(1, 0, 5))));
        using LayoutSnapshot snapshot = new(new BlockBox[] { box }, box.Context.SourceMap, 320, 480);
        Assert.Contains("first", snapshot.SemanticDocument.Text, StringComparison.Ordinal);
        Assert.Null(box.ResolvedRunStyles);
    }

    [Fact]
    public void FirstResolutionCreatesExactOrdinalCacheAndLaterAddsPreserveExistingValues()
    {
        InlineContainerBox box = CreateBox();
        try
        {
            ElementStyle resolved = ResolveStyle(box, box.Runs[1]);
            List<ElementStyle?> cache = Assert.IsType<List<ElementStyle?>>(box.ResolvedRunStyles);
            Assert.Equal(3, cache.Count);
            Assert.Equal(3, cache.Capacity);
            Assert.Null(cache[0]);
            Assert.Same(resolved, cache[1]);
            Assert.Null(cache[2]);
            Assert.Same(resolved, ResolveStyle(box, box.Runs[1]));

            box.Add(new TextRun("fourth"));
            Assert.Same(cache, box.ResolvedRunStyles);
            Assert.Equal(4, cache.Count);
            Assert.Same(resolved, cache[1]);
            Assert.Null(cache[3]);
            Assert.NotNull(ResolveStyle(box, box.Runs[3]));
        }
        finally { box.Dispose(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PrivateClonePreservesCachedStyleIdentitiesWithoutSharingMutableCache(bool resolveSource)
    {
        InlineContainerBox source = CreateBox();
        InlineContainerBox? candidate = null;
        try
        {
            ElementStyle? resolved = resolveSource ? ResolveStyle(source, source.Runs[1]) : null;
            Assert.True(source.TryCreatePrivateInlinePlacementClone(
                source.Context, 320, out candidate, out _, allowUnmeasuredTextOnly: true));
            InlineContainerBox clone = Assert.IsType<InlineContainerBox>(candidate);
            if (resolveSource)
            {
                List<ElementStyle?> sourceCache = Assert.IsType<List<ElementStyle?>>(source.ResolvedRunStyles);
                List<ElementStyle?> cloneCache = Assert.IsType<List<ElementStyle?>>(clone.ResolvedRunStyles);
                Assert.NotSame(sourceCache, cloneCache);
                Assert.Equal(sourceCache.Count, cloneCache.Count);
                Assert.Same(resolved, cloneCache[1]);
                Assert.Same(resolved, ResolveStyle(clone, clone.Runs[1]));
            }
            else
            {
                Assert.Null(source.ResolvedRunStyles);
                Assert.Null(clone.ResolvedRunStyles);
                Assert.NotNull(ResolveStyle(clone, clone.Runs[1]));
                Assert.Null(source.ResolvedRunStyles);
            }
            Assert.NotSame(source.Runs[1], clone.Runs[1]);
            Assert.Equal(source.Runs.Select(run => run.InlineIndex), clone.Runs.Select(run => run.InlineIndex));
        }
        finally { candidate?.Dispose(); source.Dispose(); }
    }

    [Fact]
    public void PrivateCloneRejectsPartiallyInitializedStyleCache()
    {
        InlineContainerBox source = CreateBox();
        InlineContainerBox? candidate = null;
        try
        {
            _ = ResolveStyle(source, source.Runs[1]);
            List<ElementStyle?> sourceCache = Assert.IsType<List<ElementStyle?>>(source.ResolvedRunStyles);
            Assert.Equal(source.Runs.Count, sourceCache.Count);
            sourceCache.RemoveAt(sourceCache.Count - 1);

            Assert.False(source.TryCreatePrivateInlinePlacementClone(
                source.Context, 320, out candidate, out _, allowUnmeasuredTextOnly: true));
            Assert.Null(candidate);
            Assert.Same(sourceCache, source.ResolvedRunStyles);
            Assert.Equal(source.Runs.Count - 1, sourceCache.Count);
        }
        finally { candidate?.Dispose(); source.Dispose(); }
    }

    private static ElementStyle ResolveStyle(InlineContainerBox box, InlineRun run) =>
        Assert.IsType<ElementStyle>(box.GetRunStyle(run));

    private static InlineContainerBox CreateBox()
    {
        var box = new InlineContainerBox(CreateContext(), MarkdownElementKeys.Body) { BlockIndex = 1 };
        box.Add(new TextRun("first") { SourceSpan = new SourceSpan(0, 5) });
        box.Add(new StrongRun("second"));
        box.Add(new TextRun("third"));
        return box;
    }

    private static MarkdownLayoutContext CreateContext()
    {
        var style = new ElementStyle();
        Color black = Color.FromArgb(255, 0, 0, 0);
        var theme = new ThemeSnapshot(
            new Dictionary<string, ElementStyle>(StringComparer.Ordinal)
            {
                [MarkdownElementKeys.Body] = style,
                [MarkdownElementKeys.Strong] = style,
            }, new Dictionary<string, ElementStyleOverride>(), black, black, black, black,
            isDark: false, isHighContrast: false, textScaleFactor: 1);
        return new MarkdownLayoutContext(CanvasDevice.GetSharedDevice(), theme,
            new MarkdownSourceMap("first second third"), new MarkdownExtensionRegistry(), FlowDirection.LeftToRight);
    }
}
