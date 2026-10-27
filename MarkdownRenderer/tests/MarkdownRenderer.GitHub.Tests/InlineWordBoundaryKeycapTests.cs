using MarkdownRenderer.Document;
using MarkdownRenderer.Extensions;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Parsing;
using MarkdownRenderer.Theming;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml;
using Windows.UI;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

[Collection("Shared Canvas text format cache")]
public sealed class InlineWordBoundaryKeycapTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void FormattedRunBoundariesDoNotMergeWordsWithAnAdjacentKeycap(
        bool splitKeycap, bool rightToLeft)
    {
        using var device = new CanvasDevice(forceSoftwareRenderer: true);
        MarkdownLayoutContext context = CreateContext(device, rightToLeft);
        var box = new InlineContainerBox(context, MarkdownElementKeys.Body)
        {
            BlockIndex = context.NextBlockIndex(),
        };
        try
        {
            box.Add(new StrongRun("left"));
            box.Add(new TextRun(splitKeycap ? "1" : "1\uFE0F\u20E3"));
            if (splitKeycap)
                box.Add(new EmphasisRun("\uFE0F\u20E3"));
            box.Add(new EmphasisRun("right"));
            _ = box.Measure(180f);
            box.Arrange(0, 0, 180f);

            int rightRun = splitKeycap ? 3 : 2;
            var leftStart = new DocumentPosition(box.BlockIndex, 0, 0);
            var keycapStart = new DocumentPosition(box.BlockIndex, 1, 0);
            var rightStart = new DocumentPosition(box.BlockIndex, rightRun, 0);
            var rightEnd = new DocumentPosition(box.BlockIndex, rightRun, 5);
            Assert.Equal((leftStart, keycapStart), box.GetWordBoundaries(
                new DocumentPosition(box.BlockIndex, 0, 2)));
            Assert.Equal((rightStart, rightEnd), box.GetWordBoundaries(
                new DocumentPosition(box.BlockIndex, rightRun, 1)));
            Assert.Equal((keycapStart, rightStart), box.GetWordBoundaries(keycapStart));
            // A combining part in another formatted run still maps to the whole
            // grapheme, not to its run or either neighboring word.
            var insideKeycap = new DocumentPosition(box.BlockIndex, splitKeycap ? 2 : 1, 1);
            Assert.Equal((keycapStart, rightStart), box.GetWordBoundaries(insideKeycap));
        }
        finally
        {
            box.Dispose();
        }
    }

    [Theory]
    [InlineData(false, "\u20E3")]
    [InlineData(true, "\u20E3")]
    [InlineData(false, "\u0661\u20E3")]
    [InlineData(true, "\u0661\u20E3")]
    public void IneligibleKeycapBaseDoesNotSplitAWordAcrossFormattedRuns(
        bool rightToLeft, string middle)
    {
        using var device = new CanvasDevice(forceSoftwareRenderer: true);
        MarkdownLayoutContext context = CreateContext(device, rightToLeft);
        var box = new InlineContainerBox(context, MarkdownElementKeys.Body)
        {
            BlockIndex = context.NextBlockIndex(),
        };
        try
        {
            box.Add(new StrongRun("left"));
            box.Add(new TextRun(middle));
            box.Add(new EmphasisRun("right"));
            _ = box.Measure(180f);
            box.Arrange(0, 0, 180f);

            var expected = (new DocumentPosition(box.BlockIndex, 0, 0),
                new DocumentPosition(box.BlockIndex, 2, 5));
            Assert.Equal(expected, box.GetWordBoundaries(new DocumentPosition(box.BlockIndex, 0, 2)));
            Assert.Equal(expected, box.GetWordBoundaries(new DocumentPosition(box.BlockIndex, 1, 0)));
            Assert.Equal(expected, box.GetWordBoundaries(new DocumentPosition(box.BlockIndex, 2, 1)));
        }
        finally
        {
            box.Dispose();
        }
    }

    private static MarkdownLayoutContext CreateContext(CanvasDevice device, bool rightToLeft)
    {
        Color transparent = Color.FromArgb(0, 0, 0, 0);
        var theme = new ThemeSnapshot(
            new Dictionary<string, ElementStyle>
            {
                [MarkdownElementKeys.Body] = new() { FontFamily = "Segoe UI", FontSize = 16f },
            },
            new Dictionary<string, ElementStyleOverride>(),
            transparent, transparent, transparent, transparent,
            isDark: false, isHighContrast: false, textScaleFactor: 1d,
            styleSheet: MarkdownStyleSheet.Empty);
        return new MarkdownLayoutContext(device, theme,
            new MarkdownSourceMap("left1\uFE0F\u20E3right"),
            new MarkdownExtensionRegistry(),
            rightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight,
            language: "en-US");
    }
}
