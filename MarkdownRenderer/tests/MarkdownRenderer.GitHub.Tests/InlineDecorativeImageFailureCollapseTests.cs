using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml;
using MarkdownRenderer.Accessibility;
using MarkdownRenderer.Document;
using MarkdownRenderer.Images;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Parsing;
using MarkdownRenderer.Theming;
using Windows.Foundation;
using Windows.UI;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

[Collection(AllocationSensitiveContractCollection.Name)]
public sealed class InlineDecorativeImageFailureCollapseTests
{
    private const float LayoutWidth = 320f;

    [Fact]
    public async Task FailedDimensionlessEmptyAltImageHasNoWarpPaintButRetainsItsSourceCharacter()
    {
        using var fixture = new Fixture(alt: string.Empty, includeSurroundingText: false);
        await fixture.FailImageAsync();

        InlineContainerBox box = fixture.Box;
        InlineImageRun imageRun = fixture.ImageRun;
        Assert.Equal(1, imageRun.RenderedLength);
        Assert.Equal("\uFFFC", imageRun.Text);
        Assert.Equal(0, imageRun.DesiredWidth);
        Assert.Equal(0, imageRun.DesiredHeight);
        Assert.True(imageRun.Image.IsFailedDecorativeImageCollapsed);

        box.Measure(LayoutWidth);
        box.Arrange(0, 0, LayoutWidth);
        using CanvasRenderTarget target = fixture.Render();

        Assert.All(target.GetPixelColors(), static color => Assert.Equal(0, color.A));
        Assert.Equal(0, imageRun.Image.Bounds.Width);
        Assert.Equal(0, imageRun.Image.Bounds.Height);

        Assert.Equal(fixture.ImageMarkup, fixture.SourceMap.Slice(fixture.ImageRange));
        Assert.Equal(0, box.GetBufferCharOffset(fixture.ImageRange.Start));
        Assert.Equal(1, box.GetBufferCharOffset(fixture.ImageRange.End));
    }

    [Fact]
    public async Task CollapsedImageKeepsSurroundingSourceAndSelectionOffsets()
    {
        using var fixture = new Fixture(alt: string.Empty, includeSurroundingText: true);
        await fixture.FailImageAsync();

        InlineContainerBox box = fixture.Box;
        InlineImageRun imageRun = fixture.ImageRun;
        TextRun suffixRun = Assert.IsType<TextRun>(fixture.SuffixRun);
        Assert.Equal(1, imageRun.RenderedLength);
        Assert.Equal(fixture.ImageMarkup, fixture.SourceMap.Slice(fixture.ImageRange));
        Assert.Equal(Fixture.Prefix.Length, box.GetBufferCharOffset(fixture.ImageRange.Start));
        Assert.Equal(Fixture.Prefix.Length + 1, box.GetBufferCharOffset(fixture.ImageRange.End));
        Assert.Equal(
            new DocumentPosition(box.BlockIndex, suffixRun.InlineIndex, 0),
            box.GetPositionFromBufferOffset(Fixture.Prefix.Length + 1));

        Rect[] suffixSelection = box.GetSelectionRects(fixture.SuffixRange).ToArray();
        Assert.NotEmpty(suffixSelection);
        Assert.Contains(suffixSelection, static rect => rect.Width > 0 && rect.Height > 0);

        Rect suffixBounds = suffixSelection[0];
        var suffixCenter = new Point(
            suffixBounds.X + (suffixBounds.Width / 2),
            suffixBounds.Y + (suffixBounds.Height / 2));
        Assert.True(box.HitTest(suffixCenter, out DocumentPosition hitPosition));
        Assert.Equal(box.BlockIndex, hitPosition.BlockIndex);
        Assert.NotEqual(imageRun.InlineIndex, hitPosition.InlineIndex);
        Assert.InRange(
            box.GetBufferCharOffset(hitPosition),
            Fixture.Prefix.Length + 1,
            Fixture.Prefix.Length + 1 + Fixture.Suffix.Length);
    }

    [Theory]
    [InlineData("diagram", false)]
    [InlineData("", true)]
    public async Task FailedNamedOrExplicitlySizedInlineImageKeepsItsPlaceholder(string alt, bool explicitSize)
    {
        using var fixture = new Fixture(
            alt,
            includeSurroundingText: false,
            requestedWidth: explicitSize ? new SafeHtmlLength(40, IsPercent: false) : null,
            requestedHeight: explicitSize ? new SafeHtmlLength(20, IsPercent: false) : null);
        await fixture.FailImageAsync();

        InlineImageRun imageRun = fixture.ImageRun;
        Assert.False(imageRun.Image.IsFailedDecorativeImageCollapsed);
        Assert.True(imageRun.DesiredWidth > 0);
        Assert.True(imageRun.DesiredHeight > 0);

        fixture.Box.Measure(LayoutWidth);
        fixture.Box.Arrange(0, 0, LayoutWidth);
        using CanvasRenderTarget target = fixture.Render();

        Assert.Contains(target.GetPixelColors(), static color => color.A != 0);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly CanvasDevice _device;
        private readonly MarkdownLayoutContext _context;
        private readonly int _imageSourceStart;

        internal const string Prefix = "before ";
        internal const string Suffix = " after";
        private const string Url = "https://example.test/missing.png";

        internal Fixture(
            string alt,
            bool includeSurroundingText,
            SafeHtmlLength? requestedWidth = null,
            SafeHtmlLength? requestedHeight = null)
        {
            string altSource = alt.Length == 0 ? string.Empty : alt;
            ImageMarkup = requestedWidth is null && requestedHeight is null
                ? $"![{altSource}]({Url})"
                : $"<img src=\"{Url}\" alt=\"{altSource}\" width=\"40\" height=\"20\">";
            SourceText = includeSurroundingText ? Prefix + ImageMarkup + Suffix : ImageMarkup;
            SourceMap = new MarkdownSourceMap(SourceText);

            _device = new CanvasDevice(forceSoftwareRenderer: true);
            var style = new ElementStyle
            {
                FontFamily = "Segoe UI",
                FontSize = 16,
                LineHeightMultiplier = 1.5f,
            };
            var styles = new Dictionary<string, ElementStyle>(StringComparer.Ordinal)
            {
                [MarkdownElementKeys.Body] = style,
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
            _context = new MarkdownLayoutContext(
                _device,
                theme,
                SourceMap,
                new MarkdownExtensionRegistry(),
                FlowDirection.LeftToRight,
                language: "en-US")
            {
                ImageResolver = new BlockedResolver(),
                ImageCancellationToken = CancellationToken.None,
            };

            Box = new InlineContainerBox(_context, MarkdownElementKeys.Body)
            {
                BlockIndex = _context.NextBlockIndex(),
                UseContainerMargin = false,
                UseContainerPadding = false,
            };
            PrefixRun = includeSurroundingText
                ? new TextRun(Prefix) { SourceSpan = new SourceSpan(0, Prefix.Length) }
                : null;
            _imageSourceStart = includeSurroundingText ? Prefix.Length : 0;
            ImageRun = new InlineImageRun(
                _context,
                alt,
                Url,
                requestedWidth: requestedWidth,
                requestedHeight: requestedHeight)
            {
                SourceSpan = new SourceSpan(_imageSourceStart, ImageMarkup.Length),
            };
            SuffixRun = includeSurroundingText
                ? new TextRun(Suffix)
                {
                    SourceSpan = new SourceSpan(_imageSourceStart + ImageMarkup.Length, Suffix.Length),
                }
                : null;
            if (PrefixRun is not null)
                Box.Add(PrefixRun);
            Box.Add(ImageRun);
            if (SuffixRun is not null)
                Box.Add(SuffixRun);
            Box.Measure(LayoutWidth);
            Box.Arrange(0, 0, LayoutWidth);

            ImageRange = new DocumentRange(
                new DocumentPosition(Box.BlockIndex, ImageRun.InlineIndex, 0),
                new DocumentPosition(Box.BlockIndex, ImageRun.InlineIndex, 1));
            SuffixRange = SuffixRun is { } suffixRun
                ? new DocumentRange(
                    new DocumentPosition(Box.BlockIndex, suffixRun.InlineIndex, 0),
                    new DocumentPosition(Box.BlockIndex, suffixRun.InlineIndex, Suffix.Length))
                : default;
        }

        internal string SourceText { get; }
        internal string ImageMarkup { get; }
        internal MarkdownSourceMap SourceMap { get; }
        internal InlineContainerBox Box { get; }
        internal TextRun? PrefixRun { get; }
        internal InlineImageRun ImageRun { get; }
        internal InlineRun? SuffixRun { get; }
        internal DocumentRange ImageRange { get; }
        internal DocumentRange SuffixRange { get; }

        internal async Task FailImageAsync()
        {
            var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            EventHandler<LoadCompletedEventArgs> handler = (_, _) => completed.TrySetResult();
            ImageRun.Image.LoadCompleted += handler;
            try
            {
                ImageRun.Image.EnsureLoading();
                await completed.Task.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.Equal(MarkdownImageAccessibilityState.Error, ImageRun.Image.AccessibilityState);
                Box.Measure(LayoutWidth);
                Box.Arrange(0, 0, LayoutWidth);
            }
            finally
            {
                ImageRun.Image.LoadCompleted -= handler;
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
            SharedCanvasBitmapCache.ReleaseDevice(_device);
            _device.Dispose();
        }
    }

    private sealed class BlockedResolver : IMarkdownImageResolver
    {
        public ValueTask<MarkdownImageResolution> ResolveAsync(
            string source,
            MarkdownImageResolveContext context,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(MarkdownImageResolution.Blocked(MarkdownImageUnavailableReason.Unavailable));
    }
}
