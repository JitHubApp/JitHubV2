using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MarkdownRenderer.Document;
using MarkdownRenderer.Extensions;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Parsing;
using MarkdownRenderer.Theming;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Windows.UI;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

[Collection(AllocationSensitiveContractCollection.Name)]
public sealed class ImageBoxVisibleArtworkSelectionEndpointTests
{
    private const int ImageSize = 96;
    private const int AvailableWidth = 120;
    private const float Left = 31;
    private const float Top = 47;
    private const double HorizontalMargin = 12;
    private const double VerticalMargin = 32;

    private const string SvgDataUri =
        "data:image/svg+xml;base64,PHN2ZyB4bWxucz0iaHR0cDovL3d3dy53My5vcmcvMjAwMC9zdmciIHdpZHRoPSI5NiIgaGVpZ2h0PSI5NiI+PHJlY3Qgd2lkdGg9Ijk2IiBoZWlnaHQ9Ijk2IiBmaWxsPSIjMDA4OGZmIi8+PC9zdmc+";

    [Theory]
    [InlineData(FlowDirection.LeftToRight, 1.0)]
    [InlineData(FlowDirection.RightToLeft, 1.0)]
    [InlineData(FlowDirection.LeftToRight, 2.0)]
    [InlineData(FlowDirection.RightToLeft, 2.0)]
    public async Task AtomicEndpointUsesPaintedArtworkExtentWhenCaptionAndMarginsDominate(
        FlowDirection flowDirection,
        double textScaleFactor)
    {
        string alt = string.Join(" ", Enumerable.Repeat("caption", 80));
        MarkdownLayoutContext context = CreateContext(flowDirection, textScaleFactor, alt);
        var image = new ImageBox(
            context,
            SvgDataUri,
            alt,
            new SafeHtmlLength(ImageSize, IsPercent: false),
            new SafeHtmlLength(ImageSize, IsPercent: false))
        {
            BlockIndex = 7,
            Margin = new Thickness(
                HorizontalMargin,
                VerticalMargin,
                HorizontalMargin,
                VerticalMargin),
        };

        try
        {
            image.Measure(AvailableWidth);
            image.Arrange(Left, Top, AvailableWidth);

            // This expected rectangle is computed from the independently
            // specified placement inputs: outer left + horizontal margin,
            // outer top + vertical margin, and the requested SVG dimensions.
            Rect expectedArtwork = new(
                Left + HorizontalMargin,
                Top + VerticalMargin,
                ImageSize,
                ImageSize);
            Assert.Equal(expectedArtwork, image.AutomationRenderDestination);
            Assert.True(image.Bounds.Height > expectedArtwork.Height * 2,
                "The fixture must place the full-bounds midpoint below the actual SVG artwork.");

            Point beforeArtworkMidpoint = new(
                expectedArtwork.Left + expectedArtwork.Width / 2,
                expectedArtwork.Top + expectedArtwork.Height / 4);
            Point afterArtworkMidpoint = new(
                expectedArtwork.Left + expectedArtwork.Width / 2,
                expectedArtwork.Top + expectedArtwork.Height * 3 / 4);
            Assert.True(image.Bounds.Contains(beforeArtworkMidpoint));
            Assert.True(image.Bounds.Contains(afterArtworkMidpoint));

            Assert.True(image.HitTestSelectionEndpoint(
                beforeArtworkMidpoint,
                out DocumentPosition before));
            Assert.Equal(new DocumentPosition(7, 0, 0), before);

            Assert.True(image.HitTestSelectionEndpoint(
                afterArtworkMidpoint,
                out DocumentPosition after));
            Assert.Equal(new DocumentPosition(7, 0, 1), after);

            // Ordinary pointer-down stays start-only; only endpoint extension
            // interprets the artwork's lower half as the after-image boundary.
            Assert.True(image.HitTest(afterArtworkMidpoint, out DocumentPosition ordinary));
            Assert.Equal(new DocumentPosition(7, 0, 0), ordinary);

            // A point immediately below the artwork still uses the previous
            // full-block midpoint fallback; the margin probes below verify
            // the outer portions of that same fallback region.
            Point postArtworkPoint = new(
                expectedArtwork.Left + expectedArtwork.Width / 2,
                expectedArtwork.Bottom + 1);
            Assert.True(image.Bounds.Contains(postArtworkPoint));
            bool postArtworkIsAfterFullBoundsMidpoint =
                postArtworkPoint.Y >= image.Bounds.Top + image.Bounds.Height / 2;
            Assert.True(image.HitTestSelectionEndpoint(postArtworkPoint, out DocumentPosition postArtwork));
            Assert.Equal(
                new DocumentPosition(7, 0, postArtworkIsAfterFullBoundsMidpoint ? 1 : 0),
                postArtwork);

            Point topMarginPoint = new(
                expectedArtwork.Left + expectedArtwork.Width / 2,
                image.Bounds.Top + 1);
            Assert.True(image.Bounds.Contains(topMarginPoint));
            Assert.True(image.HitTestSelectionEndpoint(topMarginPoint, out DocumentPosition topMargin));
            Assert.Equal(new DocumentPosition(7, 0, 0), topMargin);

            Point bottomMarginPoint = new(
                expectedArtwork.Left + expectedArtwork.Width / 2,
                image.Bounds.Bottom - 1);
            Assert.True(image.Bounds.Contains(bottomMarginPoint));
            Assert.True(image.HitTestSelectionEndpoint(bottomMarginPoint, out DocumentPosition bottomMargin));
            Assert.Equal(new DocumentPosition(7, 0, 1), bottomMargin);
        }
        finally
        {
            image.Dispose();
            await image.RetirementTask.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    private static MarkdownLayoutContext CreateContext(
        FlowDirection flowDirection,
        double textScaleFactor,
        string sourceAlt)
    {
        var body = new ElementStyle
        {
            FontFamily = "Segoe UI",
            FontSize = 16,
            Foreground = Color.FromArgb(255, 32, 32, 32),
        };
        var styles = new Dictionary<string, ElementStyle>(StringComparer.Ordinal)
        {
            [MarkdownElementKeys.Body] = body,
            [MarkdownElementKeys.ImageCaption] = body,
        };
        Color transparent = Color.FromArgb(0, 0, 0, 0);
        var theme = new ThemeSnapshot(
            styles,
            new Dictionary<string, ElementStyleOverride>(),
            surfaceColor: transparent,
            selectionHighlightColor: Color.FromArgb(255, 0, 102, 204),
            selectionForegroundColor: Color.FromArgb(255, 255, 255, 255),
            focusVisualColor: body.Foreground,
            isDark: false,
            isHighContrast: false,
            textScaleFactor: textScaleFactor);
        return new MarkdownLayoutContext(
            Microsoft.Graphics.Canvas.CanvasDevice.GetSharedDevice(),
            theme,
            new MarkdownSourceMap($"![{sourceAlt}]({SvgDataUri})"),
            new MarkdownExtensionRegistry(),
            flowDirection,
            language: "en-US");
    }
}
