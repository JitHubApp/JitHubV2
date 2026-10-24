using Markdig.Syntax;
using MarkdownRenderer.Document;
using MarkdownRenderer.Gfm;
using MarkdownRenderer.Hosting;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Parsing;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml;
using Windows.Graphics.DirectX;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

[Collection(SharedCanvasBitmapCacheTestCollection.Name)]
public sealed class ChildBlockConstructionScopeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EagerAndLazyNestedListsReleaseConstructionCallback(bool lazy)
    {
        await using var seed = new CompletedRasterPlacementCloneTests.PlacementFixture();
        const string source = "- [ ] parent\n  - ordinary child\n  - [x] task child";
        var registry = new MarkdownExtensionRegistry().ConfigureGitHubReadmeRegistry();
        var context = new MarkdownLayoutContext(seed.Device, seed.SourceContext.ThemeSnapshot,
            new MarkdownSourceMap(source), registry, FlowDirection.LeftToRight);
        var document = Markdig.Markdown.Parse(source, registry.BuildPipeline());
        LayoutSnapshot? snapshot = null;
        try
        {
            var builder = new LayoutBuilder(context);
            snapshot = lazy
                ? builder.BuildLazy(document, 640, 0, 480, 0, CancellationToken.None)
                : builder.Build(document, 640, CancellationToken.None);
            StackBox list = Assert.IsType<StackBox>(Assert.Single(snapshot.Blocks));
            ListItemBox parent = Assert.IsType<ListItemBox>(Assert.Single(list.Children));
            StackBox nested = Assert.Single(parent.Content.Children.OfType<StackBox>());
            Assert.Equal(2, nested.Children.Count);
            Assert.All(nested.Children, child => Assert.IsType<ListItemBox>(child));
            Assert.False(context.TryBuildChildBlock(document[0], out BlockBox? result));
            Assert.Null(result);
        }
        finally
        {
            try
            {
                if (snapshot is not null)
                    await snapshot.Retire().WaitAsync(TimeSpan.FromSeconds(10));
            }
            finally
            {
                await context.DrainOwnedRetirementsAsync().WaitAsync(TimeSpan.FromSeconds(10));
            }
        }
    }

    [Fact]
    public async Task NestedScopeRestoresOuterCallbackAndNullResultStillMeansHandled()
    {
        await using var seed = new CompletedRasterPlacementCloneTests.PlacementFixture();
        var context = seed.SourceContext;
        Block node = Markdig.Markdown.Parse("- child")[0];
        int outerCalls = 0;
        using (context.PushChildBlockBuilder(received =>
        {
            Assert.Same(node, received);
            outerCalls++;
            return seed.Source;
        }))
        {
            Assert.True(context.TryBuildChildBlock(node, out BlockBox? first));
            Assert.Same(seed.Source, first);
            using (context.PushChildBlockBuilder(_ => null))
            {
                Assert.True(context.TryBuildChildBlock(node, out BlockBox? suppressed));
                Assert.Null(suppressed);
            }
            Assert.True(context.TryBuildChildBlock(node, out BlockBox? restored));
            Assert.Same(seed.Source, restored);
        }
        Assert.Equal(2, outerCalls);
        Assert.False(context.TryBuildChildBlock(node, out _));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailureAndCancellationReleaseCallback(bool canceled)
    {
        await using var seed = new CompletedRasterPlacementCloneTests.PlacementFixture();
        var context = seed.SourceContext;
        Block node = Markdig.Markdown.Parse("- child")[0];
        using var cancellation = new CancellationTokenSource();
        if (canceled)
            cancellation.Cancel();
        void Invoke()
        {
            using var scope = context.PushChildBlockBuilder(_ =>
            {
                cancellation.Token.ThrowIfCancellationRequested();
                throw new InvalidOperationException("injected nested block failure");
            });
            context.TryBuildChildBlock(node, out _);
        }
        if (canceled)
            Assert.ThrowsAny<OperationCanceledException>(Invoke);
        else
            Assert.Throws<InvalidOperationException>(Invoke);
        Assert.False(context.TryBuildChildBlock(node, out _));
        default(MarkdownLayoutContext.ChildBlockBuildScope).Dispose();
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task NestedListFailureRetiresAlreadyAttachedTaskImage(bool lazy, bool canceled)
    {
        const string imageUrl = "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=";
        await using var seed = new CompletedRasterPlacementCloneTests.PlacementFixture();
        CanvasBitmap bitmap = CanvasBitmap.CreateFromBytes(
            seed.Device, new byte[4], 1, 1, DirectXPixelFormat.B8G8R8A8UIntNormalized);
        SharedCanvasBitmapCache.StoreAndAcquire(seed.Device, imageUrl, bitmap).Dispose();
        using var cancellation = new CancellationTokenSource();
        string source = $"- [ ] label ![sample]({imageUrl})\n  - child";
        var registry = new MarkdownExtensionRegistry().ConfigureGfmRegistry();
        var context = new MarkdownLayoutContext(seed.Device, seed.SourceContext.ThemeSnapshot,
            new MarkdownSourceMap(source), registry, FlowDirection.LeftToRight)
        {
            CancellationToken = cancellation.Token,
        };
        var document = Markdig.Markdown.Parse(source, registry.BuildPipeline());
        var factory = new FailOnNestedList(cancellation, canceled);
        var builder = new LayoutBuilder(context, embedFactory: factory);
        LayoutSnapshot? unexpectedSnapshot = null;
        void Build() => unexpectedSnapshot = lazy
            ? builder.BuildLazy(document, 640, 0, 480, 0, cancellation.Token)
            : builder.Build(document, 640, cancellation.Token);
        try
        {
            if (canceled)
            {
                OperationCanceledException failure = Assert.ThrowsAny<OperationCanceledException>(Build);
                Assert.Equal(cancellation.Token, failure.CancellationToken);
            }
            else
            {
                InvalidOperationException failure = Assert.Throws<InvalidOperationException>(Build);
                Assert.Equal("injected nested list failure", failure.Message);
            }
            Assert.Equal(1, factory.NestedListCalls);
            Assert.False(context.TryBuildChildBlock(document[0], out _));
        }
        finally
        {
            try
            {
                if (unexpectedSnapshot is not null)
                    await unexpectedSnapshot.Retire().WaitAsync(TimeSpan.FromSeconds(10));
                await context.DrainOwnedRetirementsAsync().WaitAsync(TimeSpan.FromSeconds(10));
            }
            finally
            {
                SharedCanvasBitmapCache.ReleaseDevice(seed.Device);
            }
        }

        // The task paragraph acquired this bitmap before its following nested
        // list failed. Removing the LRU reference must release the native bitmap,
        // proving the unpublished task owner did not leave a cache lease alive.
        Assert.Throws<ObjectDisposedException>(() => _ = bitmap.SizeInPixels);
    }

    private sealed class FailOnNestedList(CancellationTokenSource cancellation, bool canceled)
        : IMarkdownEmbedFactory
    {
        internal int NestedListCalls { get; private set; }

        public bool CanCreate(Block block)
        {
            if (block is not ListBlock { Parent: ListItemBlock })
                return false;
            NestedListCalls++;
            if (canceled)
            {
                cancellation.Cancel();
                cancellation.Token.ThrowIfCancellationRequested();
            }
            throw new InvalidOperationException("injected nested list failure");
        }

        public float MeasureHeight(Block block, float availableWidth) => throw new NotSupportedException();
        public FrameworkElement CreateBlock(Block block) => throw new NotSupportedException();
    }
}
