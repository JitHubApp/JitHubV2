using MarkdownRenderer.Document;
using MarkdownRenderer.GitHub.Tests.TestSupport;
using MarkdownRenderer.Images;
using MarkdownRenderer.Layout.Internal.Context;
using MarkdownRenderer.Layout.Internal.Images;
using MarkdownRenderer.Layout.Internal.Images.Loading;
using MarkdownRenderer.Layout.Internal.Inlines;
using MarkdownRenderer.Layout.Internal.Inlines.Runs;
using MarkdownRenderer.Layout.Internal.RenderResources;
using MarkdownRenderer.Parsing.Internal;
using MarkdownRenderer.Parsing.Internal.SafeHtml;
using MarkdownRenderer.Theming;
using MarkdownRenderer.Theming.Internal;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Windows.UI;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests.Layout.Internal.Images;

/// <summary>
/// How an image's placeholder reacts to a terminal load failure: only an
/// unconstrained inline image relayouts (to show its alt text on one line),
/// block and explicitly sized images keep their geometry, and the placeholder
/// is set in the theme's image-caption typography.
/// </summary>
[Collection(AllocationSensitiveContractCollection.Name)]
public sealed class ImageFailurePlaceholderTests
{
    private const float LayoutWidth = 320f;
    private const string CaptionFontFamily = "Consolas";
    private const float CaptionFontSize = 13f;

    [Fact]
    public async Task UnconstrainedInlineFailureRelayoutsWithItsOneLineAltLabel()
    {
        using var fixture = new Fixture();
        InlineImageRun run = fixture.AddInlineImage("architecture diagram");

        Observation failure = await fixture.FailAsync(run.Image);

        Assert.True(failure.LayoutInvalidated);
        Assert.Equal(CanvasWordWrapping.NoWrap, failure.Wrapping);
        Assert.Equal(CaptionFontFamily, failure.FontFamily);
        Assert.Equal(CaptionFontSize, failure.FontSize);
    }

    [Fact]
    public async Task ExplicitlySizedInlineFailureRepaintsInItsReservedBox()
    {
        using var fixture = new Fixture();
        InlineImageRun run = fixture.AddInlineImage(
            "architecture diagram",
            new SafeHtmlLength(40, IsPercent: false),
            new SafeHtmlLength(20, IsPercent: false));

        Observation failure = await fixture.FailAsync(run.Image);

        Assert.False(failure.LayoutInvalidated);
    }

    [Fact]
    public async Task BlockImageFailureRepaintsInItsMeasuredBoxWithAWrappingLabel()
    {
        using var fixture = new Fixture();
        ImageBox image = fixture.CreateBlockImage("architecture diagram");
        try
        {
            image.Measure(LayoutWidth);
            CanvasTextLayout loading = Assert.IsType<CanvasTextLayout>(image.MeasureCustody.GetLayoutState().Placeholder);
            Assert.Equal(CanvasWordWrapping.Wrap, loading.WordWrapping);
            Assert.Equal(CaptionFontFamily, loading.DefaultFontFamily);
            Assert.Equal(CaptionFontSize, loading.DefaultFontSize);

            Observation failure = await fixture.FailAsync(image);

            Assert.False(failure.LayoutInvalidated);
            Assert.Equal(CanvasWordWrapping.Wrap, failure.Wrapping);
            Assert.Equal(CaptionFontFamily, failure.FontFamily);
            Assert.Equal(CaptionFontSize, failure.FontSize);
        }
        finally
        {
            image.Dispose();
        }
    }

    [Fact]
    public async Task FailedInlineImageGrowsToFitItsAltText()
    {
        using var fixture = new Fixture();
        InlineImageRun shortAlt = fixture.AddInlineImage("map");
        InlineImageRun longAlt = fixture.AddInlineImage("deployment topology map");
        await fixture.FailAsync(shortAlt.Image);
        await fixture.FailAsync(longAlt.Image);

        fixture.Box.Measure(LayoutWidth);

        Assert.True(shortAlt.DesiredWidth > 0);
        Assert.True(
            longAlt.DesiredWidth > shortAlt.DesiredWidth,
            $"A failed inline image is sized to its alt text: {longAlt.DesiredWidth} DIP for the longer alt, {shortAlt.DesiredWidth} DIP for the shorter one.");
    }

    [Fact]
    public async Task PaintingAVisibleInlineImageStartsItsLoad()
    {
        using var fixture = new Fixture();
        InlineImageRun run = fixture.AddInlineImage("architecture diagram");
        fixture.Box.Measure(LayoutWidth);
        fixture.Box.Arrange(0, 0, LayoutWidth);
        Assert.Equal(0, fixture.Resolver.Calls);

        using CanvasRenderTarget target = fixture.Render();

        await fixture.Resolver.FirstCall.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, fixture.Resolver.Calls);
    }

    [Fact]
    public void ResolverDeadlineOutlastsTheResolversOwnTransportRetries()
    {
        // ImageResolverDeadlineTests covers what the deadline does when it expires.
        Assert.Equal(TimeSpan.FromSeconds(45), ImageSourceLoader.ResolverTimeout);
    }

    private readonly record struct Observation(
        bool LayoutInvalidated,
        CanvasWordWrapping Wrapping,
        string FontFamily,
        float FontSize);

    private sealed class Fixture : IDisposable
    {
        private readonly CanvasDevice _device = new(forceSoftwareRenderer: true);
        private readonly MarkdownRenderResources _renderResources = new();
        private readonly MarkdownLayoutContext _context;
        private bool _hasInlineImages;

        internal Fixture()
        {
            var body = new ElementStyle { FontFamily = "Segoe UI", FontSize = 16f, LineHeightMultiplier = 1.5f };
            var caption = new ElementStyle { FontFamily = CaptionFontFamily, FontSize = CaptionFontSize };
            var styles = new Dictionary<string, ElementStyle>(StringComparer.Ordinal)
            {
                [MarkdownElementKeys.Body] = body,
                [MarkdownElementKeys.ImageCaption] = caption,
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
                textScaleFactor: 1f);
            _context = new MarkdownLayoutContext(
                _device,
                theme,
                new MarkdownSourceMap(string.Empty),
                new MarkdownExtensionRegistry(),
                FlowDirection.LeftToRight,
                language: "en-US")
            {
                RenderResources = _renderResources,
                ImageResolver = Resolver,
                ImageCancellationToken = CancellationToken.None,
            };
            Box = new InlineContainerBox(_context, MarkdownElementKeys.Body)
            {
                BlockIndex = _context.NextBlockIndex(),
                UseContainerMargin = false,
                UseContainerPadding = false,
            };
        }

        internal CountingBlockedResolver Resolver { get; } = new();

        internal InlineContainerBox Box { get; }

        internal InlineImageRun AddInlineImage(
            string alt,
            SafeHtmlLength? requestedWidth = null,
            SafeHtmlLength? requestedHeight = null)
        {
            var run = new InlineImageRun(
                _context,
                alt,
                UniqueUrl(),
                requestedWidth: requestedWidth,
                requestedHeight: requestedHeight);
            Box.Add(run);
            _hasInlineImages = true;
            return run;
        }

        internal ImageBox CreateBlockImage(string alt) =>
            new(_context, _renderResources.Images, UniqueUrl(), alt);

        // A failure is remembered per URL in the image resources; each image gets its own.
        private static string UniqueUrl() => $"https://example.test/missing-{Guid.NewGuid():N}.png";

        /// <summary>
        /// Measures the inline content, fails <paramref name="image"/> and
        /// records the placeholder as it stands when the completion is raised.
        /// </summary>
        internal async Task<Observation> FailAsync(ImageBox image)
        {
            if (_hasInlineImages)
            {
                Box.Measure(LayoutWidth);
                Box.Arrange(0, 0, LayoutWidth);
            }

            var completed = new TaskCompletionSource<Observation>(TaskCreationOptions.RunContinuationsAsynchronously);
            EventHandler<LoadCompletedEventArgs> handler = (_, args) =>
            {
                CanvasTextLayout? placeholder = image.MeasureCustody.GetLayoutState().Placeholder;
                completed.TrySetResult(placeholder is null
                    ? new Observation(args.LayoutInvalidated, default, string.Empty, 0f)
                    : new Observation(
                        args.LayoutInvalidated,
                        placeholder.WordWrapping,
                        placeholder.DefaultFontFamily,
                        placeholder.DefaultFontSize));
            };
            image.LoadCompleted += handler;
            try
            {
                image.EnsureLoading();
                Observation observation = await completed.Task.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.Equal(MarkdownRenderer.Accessibility.Internal.Semantics.MarkdownImageAccessibilityState.Error, image.AccessibilityState);
                return observation;
            }
            finally
            {
                image.LoadCompleted -= handler;
            }
        }

        internal CanvasRenderTarget Render()
        {
            float height = System.Math.Max(64, (float)Box.Bounds.Height);
            var target = new CanvasRenderTarget(_device, LayoutWidth, height, 96);
            using CanvasDrawingSession drawing = target.CreateDrawingSession();
            drawing.Clear(Color.FromArgb(0, 0, 0, 0));
            Box.Paint(drawing, new Rect(0, 0, LayoutWidth, height));
            return target;
        }

        public void Dispose()
        {
            Box.Dispose();
            _renderResources.ReleaseDevice(_device);
            _device.Dispose();
        }
    }

    private sealed class CountingBlockedResolver : IMarkdownImageResolver
    {
        private readonly TaskCompletionSource _firstCall = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _calls;

        internal int Calls => Volatile.Read(ref _calls);

        internal Task FirstCall => _firstCall.Task;

        public ValueTask<MarkdownImageResolution> ResolveAsync(
            string source,
            MarkdownImageResolveContext context,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            _firstCall.TrySetResult();
            return ValueTask.FromResult(MarkdownImageResolution.Blocked(MarkdownImageUnavailableReason.Unavailable));
        }
    }
}
