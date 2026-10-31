using Markdig.Syntax.Inlines;
using MarkdownRenderer.Document;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Parsing;
using MarkdownRenderer.Theming;
using Microsoft.UI.Xaml;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

[Collection(SharedCanvasBitmapCacheTestCollection.Name)]
public sealed class ChildInlineConstructionScopeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EagerAndLazyBuildClearChildConstructionCallback(bool lazy)
    {
        await using var seed = new CompletedRasterPlacementCloneTests.PlacementFixture();
        const string source = "| Header |\n| --- |\n| content |\n";
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
            TableBox table = Assert.Single(snapshot.Blocks.OfType<TableBox>());
            InlineContainerBox cell = table.GetCellBoxes().First();
            Assert.False(context.TryAppendChildInlines(cell, new ContainerInline(), null, -1));
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
    public async Task NestedScopeRestoresExactOuterCallbackAndForwardsFilterAndAliases()
    {
        await using var seed = new CompletedRasterPlacementCloneTests.PlacementFixture();
        MarkdownLayoutContext context = seed.SourceContext;
        var inlines = new ContainerInline();
        inlines.AppendChild(new LiteralInline("first"));
        Func<Inline, bool> filter = static inline => inline is LiteralInline;
        int outerCalls = 0;
        int innerCalls = 0;
        using (context.PushChildInlineBuilder((box, received, skip, aliasStart) =>
        {
            Assert.Same(seed.Source, box);
            Assert.Same(inlines, received);
            Assert.Same(filter, skip);
            Assert.Equal(17, aliasStart);
            outerCalls++;
        }))
        {
            Assert.True(context.TryAppendChildInlines(seed.Source, inlines, filter, 17));
            using (context.PushChildInlineBuilder((_, _, _, _) => innerCalls++))
                Assert.True(context.TryAppendChildInlines(seed.Source, inlines, null, -1));
            Assert.True(context.TryAppendChildInlines(seed.Source, inlines, filter, 17));
        }
        Assert.Equal(2, outerCalls);
        Assert.Equal(1, innerCalls);
        Assert.False(context.TryAppendChildInlines(seed.Source, inlines, filter, 17));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExceptionAndCancellationUnwindChildCallback(bool canceled)
    {
        await using var seed = new CompletedRasterPlacementCloneTests.PlacementFixture();
        MarkdownLayoutContext context = seed.SourceContext;
        using var cancellation = new CancellationTokenSource();
        if (canceled)
            cancellation.Cancel();
        void Invoke()
        {
            using var scope = context.PushChildInlineBuilder((_, _, _, _) =>
            {
                cancellation.Token.ThrowIfCancellationRequested();
                throw new InvalidOperationException("injected child construction failure");
            });
            context.TryAppendChildInlines(seed.Source, new ContainerInline(), null, -1);
        }
        if (canceled)
            Assert.ThrowsAny<OperationCanceledException>(Invoke);
        else
            Assert.Throws<InvalidOperationException>(Invoke);
        Assert.False(context.TryAppendChildInlines(seed.Source, new ContainerInline(), null, -1));
    }

    [Fact]
    public async Task ChildBuilderRejectsForeignContextWithoutInvokingCallback()
    {
        await using var seed = new CompletedRasterPlacementCloneTests.PlacementFixture();
        MarkdownLayoutContext foreign = seed.CreateContext();
        var box = new InlineContainerBox(foreign, MarkdownElementKeys.Body);
        int calls = 0;
        using (seed.SourceContext.PushChildInlineBuilder((_, _, _, _) => calls++))
        {
            Assert.Throws<InvalidOperationException>(() =>
                seed.SourceContext.TryAppendChildInlines(box, new ContainerInline(), null, -1));
            Assert.Equal(0, calls);
        }
        Assert.False(seed.SourceContext.TryAppendChildInlines(box, new ContainerInline(), null, -1));
        try
        {
            box.Dispose();
        }
        finally
        {
            await foreign.DrainOwnedRetirementsAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public void DefaultScopeDisposalIsSafe()
    {
        default(MarkdownLayoutContext.ChildInlineBuildScope).Dispose();
    }
}
