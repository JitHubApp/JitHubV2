using MarkdownRenderer.Document;
using MarkdownRenderer.Extensions;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Parsing;
using MarkdownRenderer.Theming;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml;
using Windows.UI;
using Windows.UI.Text;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

public sealed class RendererResourceContractTests
{
    [Fact]
    public void DocumentGeometry_IsSanitizedAndPartitionsSharedLayoutFingerprint()
    {
        ThemeSnapshot baseline = CreateSnapshot();
        ThemeSnapshot padded = CreateSnapshot(documentPadding: new Thickness(1, 2, 3, 4));
        ThemeSnapshot spaced = CreateSnapshot(blockSpacing: 5);
        ThemeSnapshot paintOnly = CreateSnapshot(
            overflowIndicatorColor: Color.FromArgb(0xFF, 0x12, 0x34, 0x56));
        ThemeSnapshot invalid = CreateSnapshot(
            documentPadding: new Thickness(-1, double.NaN, double.PositiveInfinity, 5_000),
            blockSpacing: double.PositiveInfinity);
        ThemeSnapshot clamped = CreateSnapshot(blockSpacing: 5_000);

        Assert.NotEqual(baseline.LayoutFingerprint, padded.LayoutFingerprint);
        Assert.NotEqual(baseline.LayoutFingerprint, spaced.LayoutFingerprint);
        Assert.Equal(baseline.LayoutFingerprint, paintOnly.LayoutFingerprint);
        Assert.Equal(new Thickness(0, 0, 0, 4_096), invalid.DocumentPadding);
        Assert.Equal(0, invalid.BlockSpacing);
        Assert.Equal(1_024, clamped.BlockSpacing);
    }

    [Fact]
    public void TextDecorations_AreValidatedAndHighContrastSemanticsRemainMandatory()
    {
        Assert.True(ThemeResolver.TryExtractTextDecorations(
            TextDecorations.Underline | TextDecorations.Strikethrough,
            out TextDecorations typed));
        Assert.Equal(TextDecorations.Underline | TextDecorations.Strikethrough, typed);

        Assert.True(ThemeResolver.TryExtractTextDecorations(
            " underline, strikethrough ",
            out TextDecorations parsed));
        Assert.Equal(TextDecorations.Underline | TextDecorations.Strikethrough, parsed);
        Assert.True(ThemeResolver.TryExtractTextDecorations("None", out TextDecorations none));
        Assert.Equal(TextDecorations.None, none);
        Assert.False(ThemeResolver.TryExtractTextDecorations((TextDecorations)4, out _));
        Assert.False(ThemeResolver.TryExtractTextDecorations(new object(), out _));

        var customized = new ElementStyle { Underline = false, Strikethrough = false };
        var mandatory = new ElementStyle { Underline = true, Strikethrough = true };
        ElementStyle highContrast = ThemeSnapshot.EnforceHighContrast(customized, mandatory);

        Assert.True(highContrast.Underline);
        Assert.True(highContrast.Strikethrough);
    }

    [Fact]
    public void OverflowIndicator_UsesConfiguredBrushAndMandatoryHighContrastRole()
    {
        Color configured = Color.FromArgb(0x80, 0x12, 0x34, 0x56);
        ThemeSnapshot regular = CreateSnapshot(overflowIndicatorColor: configured);

        (Color track, Color thumb) = regular.ResolveOverflowIndicatorColors(
            Color.FromArgb(0xFF, 0xAA, 0xBB, 0xCC));

        Assert.Equal(Color.FromArgb(0x14, 0x12, 0x34, 0x56), track);
        Assert.Equal(Color.FromArgb(0x48, 0x12, 0x34, 0x56), thumb);

        Color mandatoryForeground = Color.FromArgb(0xFF, 0xFF, 0xFF, 0x00);
        ThemeSnapshot highContrast = CreateSnapshot(
            foreground: mandatoryForeground,
            isHighContrast: true,
            overflowIndicatorColor: configured);
        (track, thumb) = highContrast.ResolveOverflowIndicatorColors(
            Color.FromArgb(0xFF, 0xFF, 0, 0));

        Assert.Equal(mandatoryForeground, track);
        Assert.Equal(mandatoryForeground, thumb);
    }

    [Fact]
    public async Task DocumentGeometry_IsConsistentAcrossEagerLazyAndRelayout()
    {
        using MarkdownEngine engine = new MarkdownEngineBuilder().Build();
        MarkdownRenderer.Document.MarkdownDocument document =
            await engine.ParseAsync("first\n\nsecond");
        ThemeSnapshot theme = CreateSnapshot(
            documentPadding: new Thickness(11, 13, 17, 19),
            blockSpacing: 23);

        using LayoutSnapshot eager = Build(document, theme, availableWidth: 200, lazy: false);
        AssertGeometry(eager, expectedWidth: 172, expectedTop: 13, expectedSpacing: 23, expectedBottom: 19);

        using LayoutSnapshot lazy = Build(document, theme, availableWidth: 200, lazy: true);
        Assert.Equal(0, lazy.MeasuredTopLevelBlockCount);
        AssertGeometry(lazy, expectedWidth: 172, expectedTop: 13, expectedSpacing: 23, expectedBottom: 19);

        _ = lazy.EnsureMeasuredViewport(0, lazy.Size.Height + 100, 0, CancellationToken.None);
        Assert.Equal(lazy.TopLevelBlockCount, lazy.MeasuredTopLevelBlockCount);
        AssertGeometry(lazy, expectedWidth: 172, expectedTop: 13, expectedSpacing: 23, expectedBottom: 19);

        lazy.RelayoutMeasuredBlocks(240, CancellationToken.None);
        AssertGeometry(lazy, expectedWidth: 212, expectedTop: 13, expectedSpacing: 23, expectedBottom: 19);
    }

    private static LayoutSnapshot Build(
        MarkdownRenderer.Document.MarkdownDocument document,
        ThemeSnapshot theme,
        float availableWidth,
        bool lazy)
    {
        var context = new MarkdownLayoutContext(
            CanvasDevice.GetSharedDevice(),
            theme,
            new MarkdownSourceMap(document.Source),
            new MarkdownExtensionRegistry(),
            FlowDirection.LeftToRight);
        var builder = new LayoutBuilder(
            context,
            enableDeclarativeHostedElements: false,
            semanticDocument: document);
        return lazy
            ? builder.BuildLazy(
                document.ParsedDocument!,
                availableWidth,
                viewportTop: 0,
                viewportHeight: 1,
                overscan: 0,
                CancellationToken.None)
            : builder.Build(document.ParsedDocument!, availableWidth);
    }

    private static void AssertGeometry(
        LayoutSnapshot snapshot,
        double expectedWidth,
        double expectedTop,
        double expectedSpacing,
        double expectedBottom)
    {
        Assert.Equal(2, snapshot.Blocks.Count);
        AssertClose(11, snapshot.Blocks[0].Bounds.X);
        AssertClose(expectedWidth, snapshot.Blocks[0].Bounds.Width);
        AssertClose(expectedTop, snapshot.Blocks[0].Bounds.Top);
        AssertClose(11, snapshot.Blocks[1].Bounds.X);
        AssertClose(expectedWidth, snapshot.Blocks[1].Bounds.Width);
        AssertClose(expectedSpacing, snapshot.Blocks[1].Bounds.Top - snapshot.Blocks[0].Bounds.Bottom);
        AssertClose(snapshot.Blocks[1].Bounds.Bottom + expectedBottom, snapshot.Size.Height);
    }

    private static void AssertClose(double expected, double actual) =>
        Assert.InRange(actual, expected - 0.001, expected + 0.001);

    private static ThemeSnapshot CreateSnapshot(
        Thickness documentPadding = default,
        double blockSpacing = 0,
        Color? overflowIndicatorColor = null,
        Color? foreground = null,
        bool isHighContrast = false)
    {
        Color text = foreground ?? Color.FromArgb(0xFF, 0x11, 0x22, 0x33);
        var style = new ElementStyle
        {
            FontSize = 14,
            Foreground = text,
            Margin = default,
            Padding = default,
        };
        var styles = new Dictionary<string, ElementStyle>(StringComparer.Ordinal)
        {
            [MarkdownElementKeys.Body] = style,
        };
        Color transparent = Color.FromArgb(0, 0, 0, 0);
        return new ThemeSnapshot(
            styles,
            new Dictionary<string, ElementStyleOverride>(),
            transparent,
            transparent,
            transparent,
            transparent,
            isDark: false,
            isHighContrast,
            textScaleFactor: 1,
            documentPadding: documentPadding,
            blockSpacing: blockSpacing,
            overflowIndicatorColor: overflowIndicatorColor);
    }
}
