using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MarkdownRenderer.Document;
using MarkdownRenderer.Extensions;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Parsing;
using MarkdownRenderer.Selection;
using MarkdownRenderer.Theming;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Windows.UI;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>
/// P1-035: a selected block image painted an opaque selection band across its
/// whole arranged bounds (available width, caption and margins), unlike the
/// content-bounded translucent selection that P1-028 gives Math and Mermaid.
/// The full bounds remain the interaction geometry; only the adorner
/// background follows the painted artwork and caption text.
/// </summary>
[Collection(AllocationSensitiveContractCollection.Name)]
public sealed class ImageBoxSelectionBackgroundTests
{
    private const int ImageSize = 96;
    private const int AvailableWidth = 600;
    private const string Alt = "FinalSvgAtomicAltUnique";
    private const string SvgDataUri =
        "data:image/svg+xml;base64,PHN2ZyB4bWxucz0iaHR0cDovL3d3dy53My5vcmcvMjAwMC9zdmciIHdpZHRoPSI5NiIgaGVpZ2h0PSI5NiI+PHJlY3Qgd2lkdGg9Ijk2IiBoZWlnaHQ9Ijk2IiBmaWxsPSIjMDA4OGZmIi8+PC9zdmc+";

    private static readonly Color White = Color.FromArgb(255, 255, 255, 255);
    private static readonly Color Highlight = Color.FromArgb(255, 0, 102, 204);

    [Theory]
    [InlineData(FlowDirection.LeftToRight)]
    [InlineData(FlowDirection.RightToLeft)]
    public async Task SelectedBlockImageBackgroundFollowsArtworkAndCaptionNotTheWholeBlock(FlowDirection flowDirection)
    {
        MarkdownLayoutContext context = CreateContext(flowDirection);
        var image = new ImageBox(
            context,
            SvgDataUri,
            Alt,
            new SafeHtmlLength(ImageSize, IsPercent: false),
            new SafeHtmlLength(ImageSize, IsPercent: false))
        {
            BlockIndex = 1,
        };

        try
        {
            image.Measure(AvailableWidth);
            image.Arrange(0, 0, AvailableWidth);
            int targetHeight = System.Math.Max(1, (int)System.Math.Ceiling(image.Bounds.Bottom));
            Rect artwork = image.AutomationRenderDestination;
            Assert.Equal(ImageSize, artwork.Width);
            Assert.True(image.Bounds.Width > artwork.Width * 4,
                "The fixture must arrange a block much wider than its artwork.");

            using var snapshot = new LayoutSnapshot(
                new BlockBox[] { image },
                context.SourceMap,
                width: AvailableWidth,
                height: targetHeight);
            var selection = new SelectionController();
            selection.SetAnchor(new DocumentPosition(1, 0, 0));
            selection.ExtendTo(new DocumentPosition(1, 0, 1));
            var selectionRects = new List<Rect>();
            var selectionBackgrounds = new List<SelectionBackgroundGeometry>();
            Assert.True(snapshot.TryBeginPaint());
            try
            {
                selection.AppendVisibleHighlightRects(
                    snapshot,
                    new Rect(0, 0, AvailableWidth, targetHeight),
                    selectionRects,
                    selectionBackgrounds);
            }
            finally
            {
                snapshot.EndPaint();
            }

            // Interaction geometry is unchanged: the whole atomic block.
            Assert.Equal(image.Bounds, Assert.Single(selectionRects));

            Assert.NotEmpty(selectionBackgrounds);
            SelectionBackgroundGeometry artworkBackground = Assert.Single(
                selectionBackgrounds,
                background => background.Bounds == artwork);
            Assert.True(artworkBackground.Opacity < 1f,
                "Selected artwork must stay visible through a translucent tint.");
            foreach (SelectionBackgroundGeometry background in selectionBackgrounds)
            {
                Assert.True(background.Bounds.Width < image.Bounds.Width / 2,
                    "No selection background may span the whole block width.");
                Assert.True(image.Bounds.Contains(new Point(background.Bounds.Left, background.Bounds.Top)) &&
                            image.Bounds.Contains(new Point(background.Bounds.Right - 0.5, background.Bounds.Bottom - 0.5)));
            }

            // The wrapped alt caption keeps an opaque text band so the
            // selection-foreground caption remains readable.
            SelectionBackgroundGeometry caption = Assert.Single(
                selectionBackgrounds,
                background => background.Bounds.Top >= artwork.Bottom);
            Assert.Equal(1f, caption.Opacity);

            using var target = new CanvasRenderTarget(context.ResourceCreator, AvailableWidth, targetHeight, 96);
            using (CanvasDrawingSession drawingSession = target.CreateDrawingSession())
            {
                drawingSession.Clear(White);
                foreach (SelectionBackgroundGeometry background in selectionBackgrounds)
                    background.Paint(drawingSession, Highlight, highContrast: false);
                image.PaintSelectionForeground(
                    drawingSession,
                    selection.Range,
                    White,
                    new Rect(0, 0, AvailableWidth, targetHeight));
            }

            Color[] pixels = target.GetPixelColors();
            // A point on the artwork's row but well clear of the artwork must
            // remain the page color.
            double clearX = flowDirection == FlowDirection.LeftToRight
                ? artwork.Right + 120
                : artwork.Left - 120;
            Color beside = ReadPixel(pixels, AvailableWidth, clearX, artwork.Top + artwork.Height / 2);
            Assert.Equal(White, beside);
        }
        finally
        {
            image.Dispose();
            await image.RetirementTask.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public void ImageAndVectorBoxesShareThePaintOnlySelectionBackgroundContract()
    {
        Assert.True(typeof(ISelectionBackgroundGeometrySource).IsAssignableFrom(typeof(ImageBox)));
        Assert.True(typeof(ISelectionBackgroundGeometrySource).IsAssignableFrom(typeof(VectorSceneBox)));
    }

    private static Color ReadPixel(Color[] pixels, int width, double x, double y)
        => pixels[(int)System.Math.Floor(y) * width + (int)System.Math.Floor(x)];

    private static MarkdownLayoutContext CreateContext(FlowDirection flowDirection)
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
        var theme = new ThemeSnapshot(
            styles,
            new Dictionary<string, ElementStyleOverride>(),
            surfaceColor: Color.FromArgb(0, 0, 0, 0),
            selectionHighlightColor: Highlight,
            selectionForegroundColor: White,
            focusVisualColor: body.Foreground,
            isDark: false,
            isHighContrast: false,
            textScaleFactor: 1.0);
        return new MarkdownLayoutContext(
            CanvasDevice.GetSharedDevice(),
            theme,
            new MarkdownSourceMap($"![{Alt}]({SvgDataUri})"),
            new MarkdownExtensionRegistry(),
            flowDirection,
            language: "en-US");
    }
}
