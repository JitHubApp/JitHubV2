using System.Globalization;
using System.Threading;
using Markdig;
using MarkdownRenderer.Document;
using MarkdownRenderer.Extensions;
using MarkdownRenderer.GitHub;
using MarkdownRenderer.Hosting;
using MarkdownRenderer.Html;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Parsing;
using MarkdownRenderer.Theming;
using Microsoft.Graphics.Canvas;
using Windows.Graphics.DirectX;
using Windows.UI;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

[Collection(SharedCanvasBitmapCacheTestCollection.Name)]
public sealed class MarkdownLayoutConstructionScopeTests
{
    private const string OnePixelPng =
        "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=";

    [Fact]
    public async Task HtmlBuildFailureAfterCachedImageCreationRetiresItsCacheLease()
    {
        using var device = new CanvasDevice();
        CanvasBitmap cachedBitmap = CreateBitmap(device);
        SharedCanvasBitmapCache.Lease cacheLease = SharedCanvasBitmapCache.StoreAndAcquire(
            device,
            OnePixelPng,
            cachedBitmap);
        cacheLease.Dispose(); // Leave only the LRU's reference before the layout creates its lease.

        using var cancellation = new CancellationTokenSource();
        var stringProvider = new CancelOnImageName(cancellation);
        var registry = new MarkdownExtensionRegistry().ConfigureSafeHtmlRegistry(SafeHtmlOptions.Default);
        string source = $"<div><img src=\"{OnePixelPng}\"><span>tail</span></div>";
        var context = CreateContext(device, source, registry, cancellation.Token, stringProvider);

        try
        {
            var builder = new LayoutBuilder(context);
            OperationCanceledException failure = Assert.Throws<OperationCanceledException>(
                () => builder.Build(Markdown.Parse(source, registry.BuildPipeline()), 320, cancellation.Token));

            Assert.Equal(cancellation.Token, failure.CancellationToken);
            Assert.True(stringProvider.Canceled);
            await context.DrainOwnedRetirementsAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            SharedCanvasBitmapCache.ReleaseDevice(device);
        }

        // If the partially-built HTML tree did not release its ImageBox
        // lease, removing the final cache reference would leave the bitmap
        // usable. This proves cleanup reached the cached native resource.
        Assert.Throws<ObjectDisposedException>(() => _ = cachedBitmap.SizeInPixels);
    }

    [Fact]
    public async Task SnapshotTransferKeepsReachableDescendantsAndRetiresUnpublishedRoot()
    {
        using var device = new CanvasDevice();
        var context = CreateContext(
            device,
            "unused",
            new MarkdownExtensionRegistry(),
            CancellationToken.None,
            stringProvider: null);
        var publishedRoot = new StackBox();
        var publishedDescendant = new TrackingBlockBox();
        var unpublishedRoot = new TrackingBlockBox();
        publishedRoot.Add(publishedDescendant);

        MarkdownLayoutConstructionScope scope = context.BeginConstructionScope();
        context.RegisterConstructionBlock(publishedRoot);
        context.RegisterConstructionBlock(publishedDescendant);
        context.RegisterConstructionBlock(unpublishedRoot);
        scope.TransferToSnapshot(new BlockBox[] { publishedRoot });
        scope.Dispose();

        await context.DrainOwnedRetirementsAsync();
        Assert.Equal(0, publishedDescendant.DisposeCount);
        Assert.Equal(1, unpublishedRoot.DisposeCount);

        publishedRoot.Dispose();
        Assert.Equal(1, publishedDescendant.DisposeCount);
    }

    [Fact]
    public async Task GitHubDetailsSummaryImageAliasRemainsOwnedByPublishedSnapshot()
    {
        const string source = """
            <details open>
            <summary><img src="data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=" alt="summary image"></summary>

            body

            </details>
            """;
        using var device = new CanvasDevice();
        var registry = new MarkdownExtensionRegistry().ConfigureGitHubReadmeRegistry(SafeHtmlOptions.Default);
        var context = CreateContext(device, source, registry, CancellationToken.None, stringProvider: null);
        LayoutSnapshot snapshot = new LayoutBuilder(context).Build(
            Markdown.Parse(source, registry.BuildPipeline()),
            availableWidth: 360);
        ImageBox image = Assert.Single(
            snapshot.Blocks
                .SelectMany(EnumerateOwnedBlocks)
                .OfType<InlineContainerBox>()
                .SelectMany(static box => box.InlineImageRuns)
                .Select(static entry => entry.Run.Image));

        try
        {
            Assert.False(image.ImageLifetimeDisposedForTests);
        }
        finally
        {
            await snapshot.Retire().WaitAsync(TimeSpan.FromSeconds(5));
        }

        Assert.True(image.ImageLifetimeDisposedForTests);
    }

    [Fact]
    public async Task InlineImageTransferDisposesDiscardedVectorSceneWithoutRetiringImage()
    {
        using var device = new CanvasDevice();
        var context = CreateContext(
            device,
            "unused",
            new MarkdownExtensionRegistry(),
            CancellationToken.None,
            stringProvider: null);
        var staging = new InlineContainerBox(context, MarkdownElementKeys.Body)
        {
            BlockIndex = context.NextBlockIndex(),
        };
        var destination = new InlineContainerBox(context, MarkdownElementKeys.Body)
        {
            BlockIndex = context.NextBlockIndex(),
        };
        var image = new InlineImageRun(context, "summary image", OnePixelPng);
        var scene = new InlineVectorSceneRun(
            context,
            new MarkdownVectorScene(1, 1, 1, Array.Empty<MarkdownVectorCommand>()),
            semanticText: "x",
            accessibilityName: "temporary math",
            accessibilityDescription: null,
            elementKey: MarkdownElementKeys.Body);
        staging.Add(new TextRun("before "));
        staging.Add(image);
        staging.Add(scene);
        staging.Add(new TextRun(" after"));

        InlineImageRun[] transferred = staging.DetachInlineImagesForTransfer();
        Assert.Same(image, Assert.Single(transferred));
        Assert.Equal(3, staging.Runs.Count);
        Assert.Equal(new[] { 0, 1, 2 }, staging.Runs.Select(static run => run.InlineIndex));
        Assert.False(staging.HasInlineImages);
        destination.Add(image);

        try
        {
            staging.Dispose();

            Assert.Throws<ObjectDisposedException>(() => scene.Paint(
                drawingSession: null!,
                baseX: 0,
                baseY: 0,
                semanticForeground: Color.FromArgb(0xFF, 0, 0, 0),
                highContrast: false));
            Assert.False(image.Image.ImageLifetimeDisposedForTests);
        }
        finally
        {
            destination.Dispose();
        }

        await image.Image.RetirementTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(image.Image.ImageLifetimeDisposedForTests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StagedDeclarativeInlineSceneIsRetiredWhenConstructionAborts(bool canceled)
    {
        using var device = new CanvasDevice();
        using var cancellation = new CancellationTokenSource();
        var creator = new FailingResourceCreator(device);
        MarkdownLayoutContext context = CreateContext(
            creator,
            "x",
            new MarkdownExtensionRegistry(),
            cancellation.Token,
            stringProvider: null);
        var builder = new LayoutBuilder(context);
        MarkdownVectorScene sourceScene = CreateInlineScene();
        MarkdownContent content = CreateInlineSceneContent(sourceScene);
        MarkdownLayoutConstructionScope scope = context.BeginConstructionScope();
        var runs = new List<InlineRun>();
        var throwingResource = new ThrowingDisposable();

        try
        {
            InlineVectorSceneRun run = AppendDeclarativeInlineScene(builder, content, runs);
            Assert.Same(sourceScene, run.Scene);
            Assert.True(run.IsNativeResourcesPrepared);

            // The appender has staged the actual run, but no InlineContainerBox
            // has accepted it yet. A later cancellation/failure must therefore
            // leave disposal to the construction ledger.
            context.RegisterConstructionResource(throwingResource);
            context.RegisterConstructionResource(throwingResource);
            if (canceled)
            {
                cancellation.Cancel();
            }
            else
            {
                creator.Fail = true;
            }
            var failure = Assert.Throws<System.Reflection.TargetInvocationException>(() =>
                AppendDeclarativeInlineScene(builder, content, runs));
            if (canceled)
                Assert.IsType<OperationCanceledException>(failure.InnerException);
            else
                Assert.IsType<InvalidOperationException>(failure.InnerException);
            Assert.Same(run, Assert.Single(runs));

            scope.Dispose();
            scope.Dispose();

            Assert.Equal(1, throwingResource.DisposeCount);
            await Assert.ThrowsAsync<MarkdownLayoutRetirementException>(
                () => context.DrainOwnedRetirementsAsync());
            Assert.Throws<ObjectDisposedException>(() => _ = run.Scene);
            run.Dispose();
            run.Dispose();
        }
        finally
        {
            scope.Dispose();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StagedDeclarativeInlineSceneTransfersToSnapshotOwner(bool useExplicitAdoption)
    {
        using var device = new CanvasDevice();
        MarkdownLayoutContext context = CreateContext(
            device,
            "x",
            new MarkdownExtensionRegistry(),
            CancellationToken.None,
            stringProvider: null);
        var builder = new LayoutBuilder(context);
        MarkdownVectorScene sourceScene = CreateInlineScene();
        MarkdownContent content = CreateInlineSceneContent(sourceScene);
        MarkdownLayoutConstructionScope scope = context.BeginConstructionScope();
        var runs = new List<InlineRun>();
        InlineContainerBox parent = new(context, MarkdownElementKeys.Body)
        {
            BlockIndex = context.NextBlockIndex(),
        };
        LayoutSnapshot? snapshot = null;
        bool ownerDisposed = false;

        try
        {
            InlineVectorSceneRun run = AppendDeclarativeInlineScene(builder, content, runs);
            if (useExplicitAdoption)
            {
                typeof(LayoutBuilder).GetMethod(
                        "AddInlineRun",
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                    .Invoke(builder, new object[] { parent, run });
                scope.Dispose();
            }
            else
            {
                // Exercise TransferToSnapshot's reachable-inline traversal as
                // a safety net for an attachment path without explicit adoption.
                parent.Add(run);
                snapshot = new LayoutSnapshot([parent], context.SourceMap, width: 1, height: 1);
                scope.TransferToSnapshot(snapshot.Blocks);
                scope.Dispose();
            }

            Assert.Same(sourceScene, run.Scene);
            if (useExplicitAdoption)
            {
                parent.Dispose();
                ownerDisposed = true;
            }
            else
            {
                snapshot!.Dispose();
                snapshot = null;
                ownerDisposed = true;
            }

            Assert.Throws<ObjectDisposedException>(() => _ = run.Scene);
            run.Dispose();
            run.Dispose();
        }
        finally
        {
            scope.Dispose();
            snapshot?.Dispose();
            if (!ownerDisposed && snapshot is null)
                parent.Dispose();
        }
    }

    private static MarkdownLayoutContext CreateContext(
        ICanvasResourceCreator device,
        string source,
        MarkdownExtensionRegistry registry,
        CancellationToken cancellationToken,
        IMarkdownStringProvider? stringProvider)
    {
        var style = new ElementStyle { FontFamily = "Segoe UI", FontSize = 16f };
        var styles = new Dictionary<string, ElementStyle>(StringComparer.Ordinal)
        {
            [MarkdownElementKeys.Body] = style,
            [MarkdownElementKeys.Strong] = style,
            [MarkdownElementKeys.Link] = style,
            [MarkdownElementKeys.ImageCaption] = style,
        };
        Color transparent = Color.FromArgb(0, 0, 0, 0);
        var theme = new ThemeSnapshot(
            styles,
            new Dictionary<string, ElementStyleOverride>(),
            transparent,
            transparent,
            transparent,
            transparent,
            isDark: false,
            isHighContrast: false,
            textScaleFactor: 1);
        return new MarkdownLayoutContext(
            device,
            theme,
            new MarkdownSourceMap(source),
            registry,
            Microsoft.UI.Xaml.FlowDirection.LeftToRight)
        {
            CancellationToken = cancellationToken,
            StringProvider = stringProvider,
        };
    }

    private static MarkdownVectorScene CreateInlineScene()
        => new(8, 6, 4,
            [MarkdownVectorCommand.FillPath(
                [MarkdownVectorPathOperation.MoveTo(new MarkdownVectorPoint(0, 0)),
                 MarkdownVectorPathOperation.LineTo(new MarkdownVectorPoint(8, 0)),
                 MarkdownVectorPathOperation.LineTo(new MarkdownVectorPoint(8, 6)),
                 MarkdownVectorPathOperation.Close()])]);

    private static MarkdownContent CreateInlineSceneContent(MarkdownVectorScene scene)
    {
        var content = new MarkdownContentBuilder();
        content.AddVectorScene(
            scene,
            new SourceSpan(0, 1),
            MarkdownStyleRole.Math,
            MarkdownAccessibilityRole.Math,
            accessibilityName: "staged scene",
            semanticText: "x");
        return Assert.Single(content.Build().Items);
    }

    private static InlineVectorSceneRun AppendDeclarativeInlineScene(
        LayoutBuilder builder,
        MarkdownContent content,
        List<InlineRun> destination)
    {
        typeof(LayoutBuilder).GetMethod(
                "AppendDeclarativeInline",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(builder, new object[] { content, destination });
        return Assert.IsType<InlineVectorSceneRun>(Assert.Single(destination));
    }

    private static CanvasBitmap CreateBitmap(CanvasDevice device) => CanvasBitmap.CreateFromBytes(
        device,
        new byte[4],
        1,
        1,
        DirectXPixelFormat.B8G8R8A8UIntNormalized);

    private sealed class TrackingBlockBox : BlockBox
    {
        private int _disposeCount;

        internal int DisposeCount => Volatile.Read(ref _disposeCount);

        public override float Measure(float availableWidth) => 1;

        public override void Paint(Microsoft.Graphics.Canvas.CanvasDrawingSession ds, Windows.Foundation.Rect viewport) { }

        public override void Dispose() => Interlocked.Increment(ref _disposeCount);
    }

    private sealed class ThrowingDisposable : IDisposable
    {
        private int _disposeCount;

        internal int DisposeCount => Volatile.Read(ref _disposeCount);

        public void Dispose()
        {
            Interlocked.Increment(ref _disposeCount);
            throw new InvalidOperationException("Expected staged cleanup failure.");
        }
    }

    private sealed class FailingResourceCreator(CanvasDevice device) : ICanvasResourceCreator
    {
        internal bool Fail { get; set; }

        public CanvasDevice Device => Fail
            ? throw new InvalidOperationException("Expected second inline scene construction failure.")
            : device;
    }

    private static IEnumerable<BlockBox> EnumerateOwnedBlocks(BlockBox root)
    {
        yield return root;
        switch (root)
        {
            case StackBox stack:
                foreach (BlockBox child in stack.Children)
                foreach (BlockBox descendant in EnumerateOwnedBlocks(child))
                    yield return descendant;
                break;
            case ListItemBox listItem:
                foreach (BlockBox descendant in EnumerateOwnedBlocks(listItem.Marker))
                    yield return descendant;
                foreach (BlockBox descendant in EnumerateOwnedBlocks(listItem.Content))
                    yield return descendant;
                break;
            case CodeBlockBox codeBlock:
                foreach (InlineContainerBox chunk in codeBlock.Chunks)
                foreach (BlockBox descendant in EnumerateOwnedBlocks(chunk))
                    yield return descendant;
                break;
            case TableBox table:
                foreach (InlineContainerBox cell in table.GetCellBoxes())
                foreach (BlockBox descendant in EnumerateOwnedBlocks(cell))
                    yield return descendant;
                break;
        }
    }

    private sealed class CancelOnImageName(CancellationTokenSource cancellation) : IMarkdownStringProvider
    {
        internal bool Canceled { get; private set; }

        public string? GetString(string key, CultureInfo culture)
        {
            if (key == MarkdownStringKeys.ImageName)
            {
                Canceled = true;
                cancellation.Cancel();
            }

            return null;
        }
    }
}
