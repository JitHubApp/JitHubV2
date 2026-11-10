using MarkdownRenderer.Controls.Embeds;
using MarkdownRenderer.Document;
using MarkdownRenderer.Extensions;
using MarkdownRenderer.Layout.Internal.Boxes;
using MarkdownRenderer.Layout.Internal.Context;
using MarkdownRenderer.Layout.Internal.Images;
using MarkdownRenderer.Layout.Internal.Inlines;
using MarkdownRenderer.Layout.Internal.Inlines.Runs;
using MarkdownRenderer.Layout.Internal.Snapshots;
using MarkdownRenderer.Parsing.Internal;
using MarkdownRenderer.Parsing.Internal.SafeHtml;
using MarkdownRenderer.Theming;
using MarkdownRenderer.Theming.Internal;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml;
using Windows.UI;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>
/// A paragraph's inline images are planned before DirectWrite is asked where
/// they are, so an image without a character region yet still loads and its
/// completion still reflows the paragraph; image plans subscribe once.
/// </summary>
public sealed class InlineContainerPlansTests
{
    [Fact]
    public async Task InlineImagesArePlannedBeforeTheirGeometryIsReadAndThenPlaced()
    {
        using var device = new CanvasDevice(forceSoftwareRenderer: true);
        MarkdownLayoutContext context = CreateContext(device);
        var box = new InlineContainerBox(context, MarkdownElementKeys.Body) { BlockIndex = context.NextBlockIndex() };
        var image = new InlineImageRun(
            context, "avatar", string.Empty,
            requestedWidth: new SafeHtmlLength(24f, IsPercent: false),
            requestedHeight: new SafeHtmlLength(12f, IsPercent: false));
        using var fixture = new ImageRelayoutFixture();
        LayoutSnapshot snapshot = fixture.Snapshot;
        try
        {
            box.Add(new TextRun("x"));
            box.Add(image);
            _ = box.Measure(300f);
            box.Arrange(11.25f, 17.5f, 300f);
            var collector = new RecordingCollector();

            InlineContainerPlans.Collect(collector, snapshot, box, box);

            ImageBox planned = Assert.Single(collector.Images);
            Assert.Same(image.Image, planned);
            // The plan was added while the image still had no document bounds.
            Assert.Equal([0d], collector.TopsWhenPlanned);
            Assert.True(image.Image.Bounds.Y >= 17.5);
            Assert.Equal(0, collector.EmbedCollections);
        }
        finally
        {
            box.Dispose();
            await image.Image.RetirementTask.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public async Task InlineImageWithoutAnyGeometryIsStillPlanned()
    {
        using var device = new CanvasDevice(forceSoftwareRenderer: true);
        MarkdownLayoutContext context = CreateContext(device);
        var box = new InlineContainerBox(context, MarkdownElementKeys.Body) { BlockIndex = context.NextBlockIndex() };
        var image = new InlineImageRun(context, "avatar", string.Empty);
        using var fixture = new ImageRelayoutFixture();
        LayoutSnapshot snapshot = fixture.Snapshot;
        try
        {
            box.Add(image);
            var collector = new RecordingCollector();

            // Never measured: DirectWrite has no layout to report rectangles from.
            InlineContainerPlans.Collect(collector, snapshot, box, box);

            Assert.Same(image.Image, Assert.Single(collector.Images));
            Assert.Empty(box.EnumerateInlineImageRects());
        }
        finally
        {
            box.Dispose();
            await image.Image.RetirementTask.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public void TextOnlyParagraphProducesNoPlans()
    {
        using var device = new CanvasDevice(forceSoftwareRenderer: true);
        MarkdownLayoutContext context = CreateContext(device);
        var box = new InlineContainerBox(context, MarkdownElementKeys.Body) { BlockIndex = context.NextBlockIndex() };
        using var fixture = new ImageRelayoutFixture();
        LayoutSnapshot snapshot = fixture.Snapshot;
        box.Add(new TextRun("text"));
        var collector = new RecordingCollector();

        InlineContainerPlans.Collect(collector, snapshot, box, box);

        Assert.Empty(collector.Images);
        Assert.Equal(0, collector.EmbedCollections);
        box.Dispose();
    }

    [Fact]
    public void ImagePlanSubscribesOnceAndIsPlannedOnce()
    {
        using var fixture = new ImageRelayoutFixture();
        ImageBox image = fixture.CreateImage(blockIndex: 1);

        fixture.Subscriptions.AddPlan(image);
        fixture.Subscriptions.AddPlan(image);

        Assert.True(fixture.Subscriptions.Contains(image));
        Assert.Same(image, Assert.Single(fixture.Plans.Items));
    }

    [Fact]
    public void AlreadySubscribedImageIsNotPlannedAgain()
    {
        using var fixture = new ImageRelayoutFixture();
        ImageBox image = fixture.SubscribeImage(blockIndex: 1);

        fixture.Subscriptions.AddPlan(image);

        Assert.Empty(fixture.Plans.Items);
    }

    private static MarkdownLayoutContext CreateContext(CanvasDevice device)
    {
        var styles = new Dictionary<string, ElementStyle>(StringComparer.Ordinal)
        {
            [MarkdownElementKeys.Body] = new() { FontFamily = "Segoe UI", FontSize = 16f },
        };
        Color transparent = Color.FromArgb(0, 0, 0, 0);
        var theme = new ThemeSnapshot(
            styles, new Dictionary<string, ElementStyleOverride>(),
            transparent, transparent, transparent, transparent,
            isDark: false, isHighContrast: false, textScaleFactor: 1);
        return new MarkdownLayoutContext(
            device, theme, new MarkdownSourceMap(new string(' ', 32)),
            new MarkdownExtensionRegistry(), FlowDirection.LeftToRight, language: "en-US");
    }

    private sealed class RecordingCollector : IInlinePlanCollector
    {
        internal List<ImageBox> Images { get; } = [];

        internal List<double> TopsWhenPlanned { get; } = [];

        internal int EmbedCollections { get; private set; }

        public void AddImagePlan(ImageBox image)
        {
            Images.Add(image);
            TopsWhenPlanned.Add(image.Bounds.Y);
        }

        public void CollectInlineEmbeds(LayoutSnapshot snapshot, BlockBox topLevelRoot, InlineContainerBox box) =>
            EmbedCollections++;
    }
}
