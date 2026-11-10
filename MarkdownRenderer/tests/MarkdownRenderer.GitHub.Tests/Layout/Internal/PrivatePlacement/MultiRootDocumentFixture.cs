using System.Text;
using MarkdownRenderer.Document;
using MarkdownRenderer.GitHub.Tests.Layout.Internal.Inlines.Placement;
using MarkdownRenderer.Layout.Internal.Context;
using MarkdownRenderer.Layout.Internal.Images;
using MarkdownRenderer.Layout.Internal.Inlines;
using MarkdownRenderer.Layout.Internal.Inlines.Runs;
using MarkdownRenderer.Layout.Internal.Snapshots;
using MarkdownRenderer.Theming;
using Microsoft.UI.Xaml;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests.Layout.Internal.PrivatePlacement;

/// <summary>
/// A measured, eager snapshot of top-level inline roots built directly from
/// run specifications, for the multi-root private raster candidate tests. Each
/// root gets distinct raw and collapsed margin state so a clone that rebuilds
/// margins from public getters would be caught.
/// </summary>
internal sealed class MultiRootDocumentFixture : IAsyncDisposable
{
    internal const float Width = 320f;
    internal const double BlockSpacing = 7.5;
    internal static readonly Thickness DocumentPadding = new(9, 11, 13, 15);

    private readonly MarkdownLayoutContext _sourceContext;
    private bool _retired;

    internal MultiRootDocumentFixture(
        MultiRasterPlacementCloneTests.MultiRasterFixture seed,
        RootSpecification[] specifications)
    {
        ArgumentNullException.ThrowIfNull(seed);
        ArgumentNullException.ThrowIfNull(specifications);
        if (specifications.Length == 0)
            throw new ArgumentException("The multi-root fixture needs at least one block.", nameof(specifications));

        var sourceText = new StringBuilder();
        foreach (RootSpecification root in specifications)
        foreach (RunSpecification run in root.Runs)
            sourceText.Append(run.Kind == RunKind.Image ? "!" : run.Text);

        var sourceMap = new MarkdownSourceMap(sourceText.ToString());
        _sourceContext = CreateSourceContext(seed, sourceMap);

        var roots = new InlineContainerBox[specifications.Length];
        var allImages = new List<ImageBox>();
        int sourceOffset = 0;
        float contentWidth = (float)(Width - DocumentPadding.Left - DocumentPadding.Right);
        float y = (float)DocumentPadding.Top;
        for (int rootIndex = 0; rootIndex < specifications.Length; rootIndex++)
        {
            InlineContainerBox root = CreateRoot(seed, specifications[rootIndex], rootIndex, ref sourceOffset, allImages);
            float height = root.Measure(contentWidth);
            root.Arrange((float)DocumentPadding.Left, y, contentWidth);
            y += height + (float)BlockSpacing;
            roots[rootIndex] = root;
        }

        Roots = roots;
        Images = allImages;
        ImageBearingBlockIndices = roots
            .Where(static root => root.Runs.Any(static run => run is InlineImageRun))
            .Select(static root => root.BlockIndex)
            .ToArray();
        Source = new LayoutSnapshot(
            roots,
            sourceMap,
            Width,
            y - (float)BlockSpacing + (float)DocumentPadding.Bottom,
            documentPadding: DocumentPadding,
            blockSpacing: BlockSpacing);
    }

    internal LayoutSnapshot Source { get; }
    internal LayoutSnapshot Snapshot => Source;
    internal InlineContainerBox[] Roots { get; }
    internal IReadOnlyList<ImageBox> Images { get; }
    internal int[] ImageBearingBlockIndices { get; }
    internal MarkdownLayoutContext SourceContext => _sourceContext;
    internal object ImagePreparationOwner =>
        Assert.IsAssignableFrom<object>(_sourceContext.ImagePreparationOwner);

    public async ValueTask DisposeAsync()
    {
        if (_retired)
            return;
        _retired = true;
        await Source.Retire().WaitAsync(TimeSpan.FromSeconds(10));
        await _sourceContext.DrainOwnedRetirementsAsync();
    }

    private static MarkdownLayoutContext CreateSourceContext(
        MultiRasterPlacementCloneTests.MultiRasterFixture seed,
        MarkdownSourceMap sourceMap) =>
        new(
            seed.SourceContext.ResourceCreator,
            seed.SourceContext.ThemeSnapshot,
            sourceMap,
            seed.SourceContext.Registry,
            seed.SourceContext.FlowDirection,
            seed.SourceContext.Dispatcher,
            seed.SourceContext.Language)
        {
            ImageCancellationToken = seed.SourceContext.ImageCancellationToken,
            RasterizationScale = seed.SourceContext.RasterizationScale,
            ImagePreparationOwner = seed.SourceContext.ImagePreparationOwner,
            DisclosureStates = seed.SourceContext.DisclosureStates,
            RenderResources = seed.SourceContext.RenderResources,
        };

    private InlineContainerBox CreateRoot(
        MultiRasterPlacementCloneTests.MultiRasterFixture seed,
        RootSpecification specification,
        int rootIndex,
        ref int sourceOffset,
        List<ImageBox> allImages)
    {
        var root = new InlineContainerBox(_sourceContext, specification.ElementKey)
        {
            BlockIndex = rootIndex + 1,
            UseContainerMargin = false,
            Margin = new Thickness(2 + rootIndex, 5 + rootIndex, 3 + rootIndex, 7 + rootIndex),
            CollapsedTopMargin = rootIndex % 2 == 0 ? 0 : 1.25 + rootIndex,
            SuppressTopMargin = rootIndex % 3 == 1,
            SuppressBottomMargin = rootIndex % 3 == 2,
        };

        foreach (RunSpecification runSpecification in specification.Runs)
        {
            string sourceValue = runSpecification.Kind == RunKind.Image ? "!" : runSpecification.Text;
            var sourceSpan = new SourceSpan(sourceOffset, sourceValue.Length);
            InlineRun run = CreateRun(seed, runSpecification, sourceSpan);
            root.Add(run);
            if (run is InlineImageRun imageRun)
                allImages.Add(imageRun.Image);
            sourceOffset += sourceValue.Length;
        }
        return root;
    }

    private InlineRun CreateRun(
        MultiRasterPlacementCloneTests.MultiRasterFixture seed,
        RunSpecification runSpecification,
        SourceSpan sourceSpan) => runSpecification.Kind switch
    {
        RunKind.Text => new TextRun(runSpecification.Text)
        {
            ElementKey = MarkdownElementKeys.Strong,
            StyleAliases = ["multi-root-text"],
            SourceSpan = sourceSpan,
        },
        RunKind.Link => new LinkRun(runSpecification.Text, runSpecification.Url!, "fixture link")
        {
            AccessibilityName = runSpecification.AccessibilityName ?? "accessible fixture link",
            SourceSpan = sourceSpan,
            StyleAliases = ["multi-root-link"],
        },
        RunKind.DisclosureLink => new LinkRun(runSpecification.Text, runSpecification.Url!)
        {
            DisclosureId = "fixture-disclosure",
            SourceSpan = sourceSpan,
        },
        RunKind.Image => new InlineImageRun(
            _sourceContext,
            runSpecification.Text,
            seed.ImageRuns[runSpecification.ImageIndex].Url,
            title: $"title-{runSpecification.ImageIndex}",
            linkUrl: $"https://example.test/image/{runSpecification.ImageIndex}")
        {
            ElementKey = MarkdownElementKeys.Link,
            SourceSpan = sourceSpan,
            StyleAliases = ["multi-root-image"],
        },
        _ => throw new ArgumentOutOfRangeException(nameof(runSpecification)),
    };

    internal enum RunKind
    {
        Text,
        Link,
        Image,
        DisclosureLink,
    }

    internal readonly record struct RunSpecification(
        RunKind Kind,
        string Text,
        string? Url = null,
        int ImageIndex = 0,
        string? AccessibilityName = null)
    {
        internal static RunSpecification PlainText(string text) => new(RunKind.Text, text);
        internal static RunSpecification Link(string text, string url) => new(RunKind.Link, text, url);
        internal static RunSpecification Image(int imageIndex) => new(RunKind.Image, $"raster-{imageIndex}", ImageIndex: imageIndex);
        internal static RunSpecification DisclosureLink(string text) =>
            new(RunKind.DisclosureLink, text, "https://example.test/disclosure");
    }

    internal sealed record RootSpecification(string ElementKey, RunSpecification[] Runs);
}
