using MarkdownRenderer.Document;
using MarkdownRenderer.Images;
using MarkdownRenderer.Layout.Internal.Context;
using MarkdownRenderer.Layout.Internal.Inlines;
using MarkdownRenderer.Layout.Internal.Inlines.Runs;
using MarkdownRenderer.Layout.Internal.RenderResources;
using MarkdownRenderer.Layout.Internal.Snapshots;
using MarkdownRenderer.Parsing.Internal.SafeHtml;
using MarkdownRenderer.Performance;
using MarkdownRenderer.Theming;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Windows.Graphics.DirectX;
using Windows.UI;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>Idle (unloaded) inline image runs in a private source context, for idle placement clone tests.</summary>
internal sealed class IdleImagePlacementFixture : IAsyncDisposable
{
    private readonly MarkdownRenderResources _renderResources = new();

    internal MarkdownRenderResources RenderResources => _renderResources;

    private readonly MultiRasterPlacementCloneTests.MultiRasterFixture _seed;
    private readonly object _imagePreparationOwner = new();
    private readonly List<(MarkdownLayoutContext Context, InlineContainerBox Root)> _candidates = new();
    private bool _sourceRetired;
    private LayoutSnapshot? _sourceSnapshot;

    internal IdleImagePlacementFixture(
        MultiRasterPlacementCloneTests.MultiRasterFixture seed,
        IReadOnlyList<string> urls,
        IMarkdownImageResolver? resolver,
        MarkdownPerformanceSession? session = null)
    {
        _seed = seed;
        string sourceText = new string('!', urls.Count);
        SourceContext = new MarkdownLayoutContext(
            seed.SourceContext.ResourceCreator,
            seed.SourceContext.ThemeSnapshot,
            new MarkdownSourceMap(sourceText),
            seed.SourceContext.Registry,
            seed.SourceContext.FlowDirection,
            seed.SourceContext.Dispatcher,
            seed.SourceContext.Language)
        {
            RenderResources = _renderResources,
            CancellationToken = CancellationToken.None,
            ImageCancellationToken = CancellationToken.None,
            RasterizationScale = seed.SourceContext.RasterizationScale,
            ImagePreparationOwner = _imagePreparationOwner,
            DisclosureStates = seed.SourceContext.DisclosureStates,
            ImageResolver = resolver,
            PerformanceSession = session,
            SvgRenderer = seed.SourceContext.SvgRenderer,
            ImageBaseUri = seed.SourceContext.ImageBaseUri,
            ImageDocumentPath = seed.SourceContext.ImageDocumentPath,
            ImageDocumentSource = seed.SourceContext.ImageDocumentSource,
            AllowThirdPartyRemoteImages = seed.SourceContext.AllowThirdPartyRemoteImages,
        };

        Source = new InlineContainerBox(SourceContext, MarkdownElementKeys.Body)
        {
            BlockIndex = SourceContext.NextBlockIndex(),
            UseContainerMargin = false,
        };
        ImageRuns = new List<InlineImageRun>(urls.Count);
        for (int index = 0; index < urls.Count; index++)
        {
            var run = new InlineImageRun(
                SourceContext,
                $"idle-alt-{index}",
                urls[index],
                title: $"idle-title-{index}",
                linkUrl: $"https://example.test/link-{index}",
                linkTitle: $"idle-link-title-{index}",
                requestedWidth: new SafeHtmlLength(37f + index, IsPercent: false),
                requestedHeight: new SafeHtmlLength(19f + index, IsPercent: false))
            {
                ElementKey = MarkdownElementKeys.Link,
                SourceSpan = new SourceSpan(index, 1),
                StyleAliases = [$"idle-alias-{index}"],
                StyleModifierKeys = [$"idle-modifier-{index}"],
                SemanticHeadingKey = MarkdownElementKeys.Heading2,
            };
            Source.Add(run);
            run.Image.Margin = new Thickness(2 + index, 3 + index, 4 + index, 5 + index);
            run.Image.CollapsedTopMargin = 1 + index;
            run.Image.SuppressTopMargin = index % 2 != 0;
            run.Image.SuppressBottomMargin = index % 2 == 0;
            run.Image.SafeHtmlInlineFlowScopeId = 70 + index;
            ImageRuns.Add(run);
        }

        _ = Source.Measure(320f);
        Source.Arrange(0f, 0f, 320f);
    }

    internal MarkdownLayoutContext SourceContext { get; }
    internal InlineContainerBox Source { get; }
    internal List<InlineImageRun> ImageRuns { get; }
    internal object ImagePreparationOwner => _imagePreparationOwner;

    /// <summary>Creates a measured second root, placed below <see cref="Source"/>, whose image has not started loading.</summary>
    internal (InlineContainerBox Root, InlineImageRun Run, float Height) CreatePendingRoot()
    {
        var pendingRoot = new InlineContainerBox(SourceContext, MarkdownElementKeys.Body)
        {
            BlockIndex = SourceContext.NextBlockIndex(),
            UseContainerMargin = false,
        };
        var pendingRun = new InlineImageRun(
            SourceContext,
            "pending-root-image",
            $"https://images.example.test/pending-{Guid.NewGuid():N}.png")
        {
            ElementKey = MarkdownElementKeys.Link,
            SourceSpan = new SourceSpan(0, 1),
        };
        pendingRoot.Add(pendingRun);
        float pendingHeight = pendingRoot.Measure(320f);
        pendingRoot.Arrange(0f, (float)Source.Bounds.Height, 320f);
        return (pendingRoot, pendingRun, pendingHeight);
    }

    internal MarkdownLayoutContext CreateTargetContext(CancellationToken imageCancellationToken = default) =>
        SourceContext.CreateRasterPlacementContext(
            imageCancellationToken,
            _imagePreparationOwner,
            retirementSink: null,
            imageUnavailable: null);

    internal void SetCandidate(MarkdownLayoutContext context, InlineContainerBox? candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        _candidates.Add((context, candidate));
    }

    internal void TransferSourceOwnershipToSnapshot(LayoutSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (_sourceRetired || _sourceSnapshot is not null ||
            !snapshot.Blocks.Any(block => ReferenceEquals(block, Source)))
            throw new InvalidOperationException("The fixture source snapshot ownership is invalid.");
        _sourceSnapshot = snapshot;
    }

    internal async Task RetireCandidateAsync(MarkdownLayoutContext context, InlineContainerBox candidate)
    {
        context.DiscardConstructionBlock(candidate);
        await Task.WhenAll(candidate.Runs.OfType<InlineImageRun>()
                .Select(static run => run.Image.RetirementTask))
            .WaitAsync(TimeSpan.FromSeconds(10));
        await context.DrainOwnedRetirementsAsync().WaitAsync(TimeSpan.FromSeconds(10));
        _candidates.RemoveAll(item => ReferenceEquals(item.Context, context) && ReferenceEquals(item.Root, candidate));
    }

    internal async Task RetireSourceAsync()
    {
        if (_sourceRetired)
            return;
        _sourceRetired = true;
        if (_sourceSnapshot is { } snapshot)
        {
            await snapshot.Retire().WaitAsync(TimeSpan.FromSeconds(10));
        }
        else
        {
            SourceContext.DiscardConstructionBlock(Source);
            await Task.WhenAll(ImageRuns.Select(static run => run.Image.RetirementTask))
                .WaitAsync(TimeSpan.FromSeconds(10));
        }
        await SourceContext.DrainOwnedRetirementsAsync().WaitAsync(TimeSpan.FromSeconds(10));
    }

    public async ValueTask DisposeAsync()
    {
        foreach ((MarkdownLayoutContext context, InlineContainerBox root) in _candidates.ToArray())
            await RetireCandidateAsync(context, root);
        await RetireSourceAsync();
        _renderResources.Images.BitmapCache.ReleaseDevice(_seed.Device);
    }
}
