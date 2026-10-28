using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MarkdownRenderer.Document;
using MarkdownRenderer.Extensions;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Parsing;
using MarkdownRenderer.Theming;
using Microsoft.Graphics.Canvas;
using Windows.Foundation;
using Windows.UI;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

[Collection(AllocationSensitiveContractCollection.Name)]
public sealed class ImageBoxAtomicSelectionEndpointTests
{
    [Fact]
    public async Task PromotedImageUsesBeforeAfterEndpointAndReturnsOnlyItsAtomicSlot()
    {
        MarkdownLayoutContext context = CreateContext();
        var image = new ImageBox(
            context,
            string.Empty,
            "final image",
            new SafeHtmlLength(80, IsPercent: false),
            new SafeHtmlLength(48, IsPercent: false))
        {
            BlockIndex = 2,
        };

        try
        {
            image.Measure(240);
            image.Arrange(12, 30, 240);

            Point beforePoint = new(
                image.Bounds.Left + image.Bounds.Width / 2,
                image.Bounds.Top + image.Bounds.Height / 4);
            Point afterPoint = new(
                image.Bounds.Left + image.Bounds.Width / 2,
                image.Bounds.Top + image.Bounds.Height * 3 / 4);

            Assert.True(image.HitTestSelectionEndpoint(beforePoint, out DocumentPosition before));
            Assert.Equal(new DocumentPosition(2, 0, 0), before);
            Assert.True(image.HitTestSelectionEndpoint(afterPoint, out DocumentPosition after));
            Assert.Equal(new DocumentPosition(2, 0, 1), after);

            // Ordinary pointer-down hit testing remains start-only. Only an
            // existing selection's endpoint interprets the atomic image as
            // before/after based on its visual half.
            Assert.True(image.HitTest(afterPoint, out DocumentPosition ordinaryHit));
            Assert.Equal(new DocumentPosition(2, 0, 0), ordinaryHit);

            DocumentPosition previousBlock = new(1, 0, 0);
            DocumentPosition nextBlock = new(3, 0, 0);
            Assert.Empty(image.GetSelectionRects(new DocumentRange(previousBlock, before)));
            Assert.Equal(image.Bounds, Assert.Single(
                image.GetSelectionRects(new DocumentRange(previousBlock, after))));
            Assert.Empty(image.GetSelectionRects(new DocumentRange(after, nextBlock)));
            Assert.Equal(image.Bounds, Assert.Single(image.GetSelectionRects(
                new DocumentRange(new DocumentPosition(2, 0, 0), new DocumentPosition(2, 0, 1)))));

            // This block has one atomic source slot, not arbitrary inline text
            // positions that happen to share its BlockIndex.
            Assert.Empty(image.GetSelectionRects(new DocumentRange(
                new DocumentPosition(2, 1, 0),
                new DocumentPosition(2, 1, 1))));
            Assert.Empty(image.GetSelectionRects(new DocumentRange(
                new DocumentPosition(2, 1, 0),
                nextBlock)));

            Color[] unselectedPixels = Render(image, selection: null);
            Color[] selectionEndingBeforePixels = Render(
                image,
                new DocumentRange(previousBlock, before));
            Assert.True(unselectedPixels.SequenceEqual(selectionEndingBeforePixels));

            Color[] selectionIncludingImagePixels = Render(
                image,
                new DocumentRange(previousBlock, after));
            Assert.False(unselectedPixels.SequenceEqual(selectionIncludingImagePixels));
        }
        finally
        {
            image.Dispose();
            await image.RetirementTask.WaitAsync(System.TimeSpan.FromSeconds(10));
        }
    }

    private static Color[] Render(ImageBox image, DocumentRange? selection)
    {
        const float width = 280;
        const float height = 180;
        using var target = new CanvasRenderTarget(
            CanvasDevice.GetSharedDevice(),
            width,
            height,
            96);
        using (CanvasDrawingSession drawing = target.CreateDrawingSession())
        {
            drawing.Clear(Color.FromArgb(0, 0, 0, 0));
            var viewport = new Rect(0, 0, width, height);
            image.Paint(drawing, viewport);
            if (selection is { } range)
            {
                image.PaintSelectionForeground(
                    drawing,
                    range,
                    Color.FromArgb(255, 0, 102, 204),
                    viewport);
            }
        }

        return target.GetPixelColors().ToArray();
    }

    private static MarkdownLayoutContext CreateContext()
    {
        var body = new ElementStyle
        {
            FontFamily = "Segoe UI",
            FontSize = 16,
            Foreground = Color.FromArgb(255, 32, 32, 32),
        };
        var styles = new Dictionary<string, ElementStyle>(System.StringComparer.Ordinal)
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
            textScaleFactor: 1);
        return new MarkdownLayoutContext(
            CanvasDevice.GetSharedDevice(),
            theme,
            new MarkdownSourceMap("![final image](missing.png)"),
            new MarkdownExtensionRegistry(),
            Microsoft.UI.Xaml.FlowDirection.LeftToRight,
            language: "en-US");
    }
}
