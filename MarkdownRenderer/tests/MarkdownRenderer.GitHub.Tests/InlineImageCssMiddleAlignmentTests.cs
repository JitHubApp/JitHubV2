using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using MarkdownRenderer.Document;
using MarkdownRenderer.Extensions;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Parsing;
using MarkdownRenderer.Theming;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Windows.UI;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

[Collection("Shared Canvas text format cache")]
public sealed class InlineImageCssMiddleAlignmentTests
{
    [Theory]
    [InlineData(48f, true, 1.0, false)]
    [InlineData(4f, true, 1.0, false)]
    [InlineData(4f, false, 1.5, false)]
    [InlineData(48f, true, 2.0, true)]
    [InlineData(4f, true, 1.5, true)]
    public async Task OptInImageCenterAlignsWithTheAdjacentLowercaseXMiddle(
        float imageHeight,
        bool hasAdjacentText,
        double textScaleFactor,
        bool rightToLeft)
    {
        SharedCanvasTextFormatCache.Clear();
        using var device = new CanvasDevice(forceSoftwareRenderer: true);
        FlowDirection flowDirection = rightToLeft
            ? FlowDirection.RightToLeft
            : FlowDirection.LeftToRight;
        MarkdownLayoutContext context = CreateContext(
            device,
            middleAligned: true,
            textScaleFactor,
            flowDirection);
        var box = new InlineContainerBox(context, MarkdownElementKeys.Body)
        {
            BlockIndex = context.NextBlockIndex(),
        };
        var image = new InlineImageRun(
            context,
            "avatar",
            string.Empty,
            requestedWidth: new SafeHtmlLength(24f, IsPercent: false),
            requestedHeight: new SafeHtmlLength(imageHeight, IsPercent: false));

        try
        {
            if (hasAdjacentText)
                box.Add(new TextRun("x"));
            box.Add(image);
            if (hasAdjacentText)
                box.Add(new TextRun("x"));

            _ = box.Measure(300f);
            box.Arrange(11.25f, 17.5f, 300f);

            Rect imageRect = Assert.Single(box.EnumerateInlineImageRects()).Rect;
            CanvasLineMetrics line = Assert.Single(box.GetLineMetricsSnapshot()!);
            float xHeightDip = GetResolvedXHeightDip(context);
            ElementStyle bodyStyle = context.ThemeSnapshot.GetStyle(MarkdownElementKeys.Body);
            Assert.InRange(xHeightDip / bodyStyle.FontSize, 0.25f, 0.9f);
            double expectedCenterY = box.GetTextOriginY() + line.Baseline - xHeightDip / 2d;

            Assert.Equal(imageHeight, imageRect.Height, precision: 3);
            Assert.InRange(
                System.Math.Abs(imageRect.Y + imageRect.Height / 2d - expectedCenterY),
                0d,
                0.05d);
        }
        finally
        {
            box.Dispose();
            await image.Image.RetirementTask.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public async Task DefaultMarkdownProfileRetainsLegacyCenteredImagePlacement()
    {
        SharedCanvasTextFormatCache.Clear();
        using var device = new CanvasDevice(forceSoftwareRenderer: true);
        MarkdownLayoutContext context = CreateContext(device, middleAligned: false, textScaleFactor: 1d);
        var box = new InlineContainerBox(context, MarkdownElementKeys.Body)
        {
            BlockIndex = context.NextBlockIndex(),
        };
        var image = new InlineImageRun(
            context,
            "avatar",
            string.Empty,
            requestedWidth: new SafeHtmlLength(24f, IsPercent: false),
            requestedHeight: new SafeHtmlLength(48f, IsPercent: false));
        box.Add(new TextRun("x"));
        box.Add(image);

        try
        {
            _ = box.Measure(300f);
            box.Arrange(11.25f, 17.5f, 300f);

            FieldInfo layoutField = Assert.IsAssignableFrom<FieldInfo>(
                typeof(InlineContainerBox).GetField(
                    "_layout",
                    BindingFlags.Instance | BindingFlags.NonPublic));
            var layout = Assert.IsType<CanvasTextLayout>(layoutField.GetValue(box));
            CanvasTextLayoutRegion region = Assert.Single(layout.GetCharacterRegions(1, 1)!);
            Rect lineBounds = region.LayoutBounds;
            double legacyCellY = lineBounds.Y + (lineBounds.Height - image.DesiredHeight) / 2d;
            if (legacyCellY < lineBounds.Y)
                legacyCellY = lineBounds.Y;
            Rect imageRect = Assert.Single(box.EnumerateInlineImageRects()).Rect;

            Assert.Equal(box.GetTextOriginY() + legacyCellY, imageRect.Y, precision: 4);
        }
        finally
        {
            box.Dispose();
            await image.Image.RetirementTask.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public async Task MeasuredImageMetricsRemainAvailableToGeometryAndSelectionAfterCancellation()
    {
        SharedCanvasTextFormatCache.Clear();
        using var device = new CanvasDevice(forceSoftwareRenderer: true);
        using var cancellation = new CancellationTokenSource();
        const int imageCount = 40;
        var imageStyles = new Dictionary<string, ElementStyle>(StringComparer.Ordinal);
        for (int index = 0; index < imageCount; index++)
        {
            imageStyles[$"image-metric-{index}"] = new ElementStyle
            {
                FontFamily = "Segoe UI",
                FontSize = 10f + index,
            };
        }

        MarkdownLayoutContext context = CreateContext(
            device,
            middleAligned: true,
            textScaleFactor: 1d,
            cancellationToken: cancellation.Token,
            additionalStyles: imageStyles);
        var box = new InlineContainerBox(context, MarkdownElementKeys.Body)
        {
            BlockIndex = context.NextBlockIndex(),
        };
        var images = new List<InlineImageRun>(imageCount);
        try
        {
            for (int index = 0; index < imageCount; index++)
            {
                var image = new InlineImageRun(
                    context,
                    "avatar",
                    string.Empty,
                    requestedWidth: new SafeHtmlLength(18f, IsPercent: false),
                    requestedHeight: new SafeHtmlLength(6f, IsPercent: false))
                {
                    ElementKey = $"image-metric-{index}",
                };
                images.Add(image);
                box.Add(image);
            }

            box.Measure(120f);
            box.Arrange(0f, 0f, 120f);
            (InlineImageRun Run, Rect Rect)[] imageRects = box.EnumerateInlineImageRects().ToArray();
            Assert.Equal(imageCount, imageRects.Length);
            Assert.True(imageRects.Select(entry => entry.Rect.Y).Distinct().Count() > 1);
            foreach ((InlineImageRun run, Rect rect) in imageRects)
                Assert.Equal(rect, run.Image.Bounds);

            cancellation.Cancel();

            // The selection layout is a paint-time clone. Rebuilding it after
            // the measured pass is canceled must consume the per-run scalar
            // snapshot, not perform font analysis on a cache miss.
            float targetHeight = System.Math.Max(300f, (float)box.Bounds.Height + 20f);
            using var target = new CanvasRenderTarget(device, 120f, targetHeight, 96f);
            using CanvasDrawingSession drawing = target.CreateDrawingSession();
            box.PaintSelectionForeground(
                drawing,
                new DocumentRange(
                    new DocumentPosition(box.BlockIndex, 0, 0),
                    new DocumentPosition(box.BlockIndex, 0, 1)),
                Color.FromArgb(255, 0, 120, 215),
                new Rect(0, 0, 120, targetHeight));

            (InlineImageRun Run, Rect Rect)[] selectedImageRects = box.EnumerateInlineImageRects().ToArray();
            Assert.Equal(imageCount, selectedImageRects.Length);
            foreach ((InlineImageRun run, Rect rect) in selectedImageRects)
                Assert.Equal(rect, run.Image.Bounds);
        }
        finally
        {
            box.Dispose();
            await Task.WhenAll(images.Select(image =>
                image.Image.RetirementTask.WaitAsync(TimeSpan.FromSeconds(10))));
        }
    }

    [Theory]
    [InlineData(72f, 1d, false)]
    [InlineData(130f, 1.5d, false)]
    [InlineData(72f, 2d, true)]
    [InlineData(130f, 1d, true)]
    public async Task WrappedImagesUseTheirOwnLineBaselineAndRemainInsideMeasuredContent(
        float width,
        double textScaleFactor,
        bool rightToLeft)
    {
        SharedCanvasTextFormatCache.Clear();
        using var device = new CanvasDevice(forceSoftwareRenderer: true);
        MarkdownLayoutContext context = CreateContext(
            device,
            middleAligned: true,
            textScaleFactor,
            rightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight);
        var box = new InlineContainerBox(context, MarkdownElementKeys.Body)
        {
            BlockIndex = context.NextBlockIndex(),
        };
        var images = new List<(InlineImageRun Run, int CharacterIndex)>();
        int characterIndex = 0;
        try
        {
            foreach (float height in new[] { 4f, 48f, 12f })
            {
                // Different object heights give successive wrapped lines
                // different ascents. Derive the expected absolute baseline
                // independently from line metrics, rather than accepting
                // agreement between the renderer's two placement snapshots.
                string prefix = rightToLeft ? "שלום שלום שלום " : "alpha beta gamma ";
                box.Add(new TextRun(prefix));
                characterIndex += prefix.Length;
                var image = new InlineImageRun(
                    context,
                    "avatar",
                    string.Empty,
                    requestedWidth: new SafeHtmlLength(24f, IsPercent: false),
                    requestedHeight: new SafeHtmlLength(height, IsPercent: false));
                box.Add(image);
                images.Add((image, characterIndex));
                characterIndex += image.TextLength;
            }

            box.Measure(width);
            box.Arrange(11.25f, 17.5f, width);
            CanvasLineMetrics[] lines = box.GetLineMetricsSnapshot()!;
            Assert.True(lines.Length >= 3, "The fixture must place objects on later wrapped lines.");
            float xHeight = GetResolvedXHeightDip(context);
            var rectangles = box.EnumerateInlineImageRects().ToDictionary(item => item.Run, item => item.Rect);
            var usedLines = new HashSet<int>();
            foreach ((InlineImageRun image, int offset) in images)
            {
                int lineIndex = 0;
                int lineEnd = lines[0].CharacterCount;
                double lineTop = 0;
                while (offset >= lineEnd && lineIndex + 1 < lines.Length)
                {
                    lineTop += lines[lineIndex].Height;
                    lineIndex++;
                    lineEnd += lines[lineIndex].CharacterCount;
                }
                usedLines.Add(lineIndex);
                Rect rect = rectangles[image];
                double expectedCenter = box.GetTextOriginY() + lineTop + lines[lineIndex].Baseline - xHeight / 2d;
                Assert.InRange(System.Math.Abs(rect.Y + rect.Height / 2d - expectedCenter), 0d, 0.05d);
                Assert.Equal(image.DesiredHeight, rect.Height, precision: 3);
                Assert.InRange(rect.Top, box.Bounds.Top - 0.05d, box.Bounds.Bottom + 0.05d);
                Assert.InRange(rect.Bottom, box.Bounds.Top - 0.05d, box.Bounds.Bottom + 0.05d);
            }
            Assert.Equal(images.Count, usedLines.Count);
        }
        finally
        {
            box.Dispose();
            await Task.WhenAll(images.Select(image =>
                image.Run.Image.RetirementTask.WaitAsync(TimeSpan.FromSeconds(10))));
        }
    }

    private static float GetResolvedXHeightDip(MarkdownLayoutContext context)
    {
        ElementStyle style = context.ThemeSnapshot.GetStyle(MarkdownElementKeys.Body);
        using SharedCanvasTextFormatCache.Lease format = SharedCanvasTextFormatCache.Acquire(
            style,
            CanvasWordWrapping.NoWrap,
            CanvasHorizontalAlignment.Left,
            context.FlowDirection,
            context.Language);
        CanvasTextDirection direction = context.FlowDirection == FlowDirection.RightToLeft
            ? CanvasTextDirection.RightToLeftThenTopToBottom
            : CanvasTextDirection.LeftToRightThenTopToBottom;
        // The pinned Win2D runtime's CanvasTextAnalyzer projection advertises
        // IDisposable but its native object does not support that cast.
        // Keep the analyzer local and let the WinRT projection release it.
        var analyzer = new CanvasTextAnalyzer("x", direction);
        IReadOnlyList<KeyValuePair<CanvasCharacterRange, CanvasScaledFont>> fonts =
            analyzer.GetFonts(format.Format);
        KeyValuePair<CanvasCharacterRange, CanvasScaledFont> span = Assert.Single(fonts);
        CanvasFontFace face = span.Value.FontFace;
        try
        {
            return format.Format.FontSize * span.Value.ScaleFactor * face.LowercaseLetterHeight;
        }
        finally
        {
            face.Dispose();
        }
    }

    private static MarkdownLayoutContext CreateContext(
        CanvasDevice device,
        bool middleAligned,
        double textScaleFactor,
        FlowDirection flowDirection = FlowDirection.LeftToRight,
        CancellationToken cancellationToken = default,
        IReadOnlyDictionary<string, ElementStyle>? additionalStyles = null)
    {
        var styles = new Dictionary<string, ElementStyle>(StringComparer.Ordinal)
        {
            [MarkdownElementKeys.Body] = new() { FontFamily = "Segoe UI", FontSize = 16f },
        };
        if (additionalStyles is not null)
        {
            foreach ((string key, ElementStyle style) in additionalStyles)
                styles[key] = style;
        }
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
            textScaleFactor: textScaleFactor,
            styleSheet: middleAligned
                ? MarkdownStyleSheet.Empty.WithCssMiddleAlignedInlineImages()
                : MarkdownStyleSheet.Empty);

        return new MarkdownLayoutContext(
            device,
            theme,
            new MarkdownSourceMap(new string(' ', 32)),
            new MarkdownExtensionRegistry(),
            flowDirection,
            language: "en-US")
        {
            CancellationToken = cancellationToken,
        };
    }
}
