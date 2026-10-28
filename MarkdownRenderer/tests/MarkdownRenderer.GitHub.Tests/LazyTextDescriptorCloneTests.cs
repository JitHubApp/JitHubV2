using System.Reflection;
using System.Threading;
using MarkdownRenderer.Document;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Theming;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

[Collection(SharedCanvasBitmapCacheTestCollection.Name)]
public sealed class LazyTextDescriptorCloneTests
{
    [Fact]
    public async Task ExplicitOptInCopiesUnmeasuredTextAndLinkWithoutBuildingSourceBuffer()
    {
        await using var seed = new MultiRasterPlacementCloneTests.MultiRasterFixture(imageCount: 1);
        const string sourceText = "lazy descriptor link";
        object imagePreparationOwner = new();
        MarkdownLayoutContext sourceContext = CreateContext(seed, sourceText, imagePreparationOwner);
        MarkdownLayoutContext targetContext = sourceContext.CreateRasterPlacementContext(
            CancellationToken.None, imagePreparationOwner, retirementSink: null, imageUnavailable: null);
        InlineContainerBox? source = null;
        InlineContainerBox? clone = null;
        try
        {
            source = new InlineContainerBox(sourceContext, MarkdownElementKeys.Body)
            {
                BlockIndex = sourceContext.NextBlockIndex(),
                UseContainerMargin = false,
            };
            source.Add(new TextRun("lazy descriptor ")
            {
                SourceSpan = new SourceSpan(0, 16),
                StyleAliases = ["lazy-text"],
            });
            source.Add(new LinkRun("link", "https://example.test/lazy", "lazy link")
            {
                SourceSpan = new SourceSpan(16, 4),
                AccessibilityName = "lazy accessible link",
                StyleAliases = ["lazy-link"],
            });

            Assert.False(source.HasMeasuredLayout);
            Assert.True(GetField<bool>(source, "_bufferDirty"));
            Assert.Equal(string.Empty, GetField<string>(source, "_buffer"));
            Assert.False(source.TryGetPrivateRasterPlacementCounts(
                targetContext, 320f, out _, out _));

            var defaultCapture = new PrivateRasterPlacementTree.CaptureState();
            Assert.False(defaultCapture.TryAddRoot(
                source, sourceContext, targetContext, 320f, out _));

            var optedInCapture = new PrivateRasterPlacementTree.CaptureState(allowUnmeasuredTextOnly: true);
            Assert.True(optedInCapture.TryAddRoot(
                source, sourceContext, targetContext, 320f,
                out PrivateRasterPlacementTree.RootCounts counts));
            Assert.Equal(1, counts.NodeCount);
            Assert.Equal(2, counts.RunCount);
            Assert.Equal(0, counts.ImageCount);
            Assert.Equal(sourceText.Length, counts.TextLength);

            Assert.True(source.TryGetPrivateRasterPlacementCounts(
                targetContext, 320f, out int runCount, out int imageCount,
                allowUnmeasuredTextOnly: true));
            Assert.Equal(2, runCount);
            Assert.Equal(0, imageCount);
            Assert.True(PrivateRasterPlacementTree.TryCreateClone(
                source,
                sourceContext,
                targetContext,
                320f,
                out BlockBox? clonedBlock,
                out RasterPlacementSourceFence[]? fences,
                allowUnmeasuredTextOnly: true));
            clone = Assert.IsType<InlineContainerBox>(clonedBlock);
            Assert.Empty(fences!);
            Assert.False(clone.HasMeasuredLayout);
            Assert.True(GetField<bool>(clone, "_bufferDirty"));
            Assert.Equal(string.Empty, GetField<string>(clone, "_buffer"));
            Assert.Null(GetField<object?>(clone, "_resolvedContainerStyle"));
            Assert.NotSame(source.Runs[0], clone.Runs[0]);
            Assert.NotSame(source.Runs[1], clone.Runs[1]);
            Assert.NotSame(source.Runs[0].StyleAliases, clone.Runs[0].StyleAliases);
            Assert.NotSame(source.Runs[1].StyleAliases, clone.Runs[1].StyleAliases);

            _ = clone.Measure(320f);
            clone.Arrange(0f, 0f, 320f);
            Assert.True(clone.HasMeasuredLayout);
            Assert.False(GetField<bool>(clone, "_bufferDirty"));
            Assert.Equal(sourceText, GetField<string>(clone, "_buffer"));

            Assert.False(source.HasMeasuredLayout);
            Assert.True(GetField<bool>(source, "_bufferDirty"));
            Assert.Equal(string.Empty, GetField<string>(source, "_buffer"));
            Assert.Null(GetField<object?>(source, "_resolvedContainerStyle"));
        }
        finally
        {
            try
            {
                if (clone is not null)
                    targetContext.DiscardConstructionBlock(clone);
            }
            finally
            {
                try
                {
                    if (source is not null)
                        sourceContext.DiscardConstructionBlock(source);
                }
                finally
                {
                    try
                    {
                        await targetContext.DrainOwnedRetirementsAsync()
                            .WaitAsync(TimeSpan.FromSeconds(30));
                    }
                    finally
                    {
                        await sourceContext.DrainOwnedRetirementsAsync()
                            .WaitAsync(TimeSpan.FromSeconds(30));
                    }
                }
            }
        }
    }

    [Fact]
    public async Task ExplicitOptInAdmitsOnlyPristineUnmeasuredEmptyCell()
    {
        await using var seed = new MultiRasterPlacementCloneTests.MultiRasterFixture(imageCount: 1);
        object imagePreparationOwner = new();
        MarkdownLayoutContext sourceContext = CreateContext(seed, string.Empty, imagePreparationOwner);
        MarkdownLayoutContext targetContext = sourceContext.CreateRasterPlacementContext(
            CancellationToken.None, imagePreparationOwner, retirementSink: null, imageUnavailable: null);
        InlineContainerBox? source = null;
        InlineContainerBox? clone = null;
        try
        {
            source = new InlineContainerBox(sourceContext, MarkdownElementKeys.TableCell)
            {
                BlockIndex = sourceContext.NextBlockIndex(),
                UseContainerMargin = false,
            };

            Assert.False(source.TryGetPrivateRasterPlacementCounts(
                targetContext, 320f, out _, out _));
            Assert.True(source.TryGetPrivateRasterPlacementCounts(
                targetContext, 320f, out int runCount, out int imageCount,
                allowUnmeasuredTextOnly: true));
            Assert.Equal(0, runCount);
            Assert.Equal(0, imageCount);

            Assert.True(source.TryCreatePrivateInlinePlacementClone(
                targetContext,
                320f,
                out clone,
                out RasterPlacementSourceFence[]? fences,
                allowUnmeasuredTextOnly: true));
            Assert.NotNull(clone);
            Assert.Empty(fences!);
            Assert.False(clone!.HasMeasuredLayout);

            _ = clone.Measure(320f);
            clone.Arrange(0f, 0f, 320f);
            Assert.True(clone.HasMeasuredLayout);
            Assert.False(source.HasMeasuredLayout);
        }
        finally
        {
            try
            {
                if (clone is not null)
                    targetContext.DiscardConstructionBlock(clone);
            }
            finally
            {
                try
                {
                    if (source is not null)
                        sourceContext.DiscardConstructionBlock(source);
                }
                finally
                {
                    try
                    {
                        await targetContext.DrainOwnedRetirementsAsync()
                            .WaitAsync(TimeSpan.FromSeconds(30));
                    }
                    finally
                    {
                        await sourceContext.DrainOwnedRetirementsAsync()
                            .WaitAsync(TimeSpan.FromSeconds(30));
                    }
                }
            }
        }
    }

    private static MarkdownLayoutContext CreateContext(
        MultiRasterPlacementCloneTests.MultiRasterFixture seed,
        string sourceText,
        object imagePreparationOwner) =>
        new(
            seed.SourceContext.ResourceCreator,
            seed.SourceContext.ThemeSnapshot,
            new MarkdownSourceMap(sourceText),
            seed.SourceContext.Registry,
            seed.SourceContext.FlowDirection,
            seed.SourceContext.Dispatcher,
            seed.SourceContext.Language)
        {
            ImageCancellationToken = CancellationToken.None,
            RasterizationScale = seed.SourceContext.RasterizationScale,
            ImagePreparationOwner = imagePreparationOwner,
            DisclosureStates = seed.SourceContext.DisclosureStates,
            ImageResolver = seed.SourceContext.ImageResolver,
            SvgRenderer = seed.SourceContext.SvgRenderer,
        };

    private static T GetField<T>(object instance, string name)
    {
        FieldInfo field = instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
        object? value = field.GetValue(instance);
        return value is null ? default! : (T)value;
    }
}
