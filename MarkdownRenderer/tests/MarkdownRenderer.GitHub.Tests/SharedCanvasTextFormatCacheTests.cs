using MarkdownRenderer.Layout;
using MarkdownRenderer.Theming;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI.Xaml;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

[CollectionDefinition("Shared Canvas text format cache", DisableParallelization = true)]
public sealed class SharedCanvasTextFormatCacheCollection
{
}

[Collection("Shared Canvas text format cache")]
public sealed class SharedCanvasTextFormatCacheTests
{
    private readonly MarkdownRenderResources _renderResources = new();

    [Fact]
    public void EqualDescriptor_ReusesFormatAndLeaseSurvivesEviction()
    {
        var style = new ElementStyle
        {
            FontFamily = "Segoe UI",
            FontSize = 17,
        };

        using SharedCanvasTextFormatCache.Lease first =
            _renderResources.TextFormats.Acquire(
                style,
                CanvasWordWrapping.Wrap,
                CanvasHorizontalAlignment.Left,
                FlowDirection.LeftToRight);
        using SharedCanvasTextFormatCache.Lease second =
            _renderResources.TextFormats.Acquire(
                style,
                CanvasWordWrapping.Wrap,
                CanvasHorizontalAlignment.Left,
                FlowDirection.LeftToRight);

        Assert.Same(first.Format, second.Format);
        Assert.Equal(1, _renderResources.TextFormats.Statistics.Count);

        _renderResources.TextFormats.Clear();

        Assert.Equal(17, first.Format.FontSize);
        Assert.Equal(0, _renderResources.TextFormats.Statistics.Count);
    }

    [Fact]
    public void DirectionAndWrapping_PartitionDescriptors()
    {
        var style = new ElementStyle();
        using SharedCanvasTextFormatCache.Lease leftToRight =
            _renderResources.TextFormats.Acquire(
                style,
                CanvasWordWrapping.Wrap,
                CanvasHorizontalAlignment.Left,
                FlowDirection.LeftToRight);
        using SharedCanvasTextFormatCache.Lease rightToLeft =
            _renderResources.TextFormats.Acquire(
                style,
                CanvasWordWrapping.NoWrap,
                CanvasHorizontalAlignment.Left,
                FlowDirection.RightToLeft);

        Assert.NotSame(leftToRight.Format, rightToLeft.Format);
        Assert.Equal(2, _renderResources.TextFormats.Statistics.Count);
    }

    [Fact]
    public void Language_PartitionsDescriptorsAndFlowsToDirectWrite()
    {
        var style = new ElementStyle();
        using SharedCanvasTextFormatCache.Lease english =
            _renderResources.TextFormats.Acquire(
                style,
                CanvasWordWrapping.Wrap,
                CanvasHorizontalAlignment.Left,
                FlowDirection.LeftToRight,
                "en-US");
        using SharedCanvasTextFormatCache.Lease arabic =
            _renderResources.TextFormats.Acquire(
                style,
                CanvasWordWrapping.Wrap,
                CanvasHorizontalAlignment.Left,
                FlowDirection.LeftToRight,
                "ar-SA");
        using SharedCanvasTextFormatCache.Lease englishAlternateCase =
            _renderResources.TextFormats.Acquire(
                style,
                CanvasWordWrapping.Wrap,
                CanvasHorizontalAlignment.Left,
                FlowDirection.LeftToRight,
                "EN-us");

        Assert.NotSame(english.Format, arabic.Format);
        Assert.Same(english.Format, englishAlternateCase.Format);
        Assert.Equal("en-US", english.Format.LocaleName, ignoreCase: true);
        Assert.Equal("ar-SA", arabic.Format.LocaleName, ignoreCase: true);
        Assert.Equal(2, _renderResources.TextFormats.Statistics.Count);
    }

    [Theory]
    [InlineData(16f, 1.5f, 24f)]
    [InlineData(13.6f, 1.45f, 19.72f)]
    [InlineData(32f, 1.25f, 40f)]
    public void LineHeightMultiplier_ResolvesToAbsoluteDipLineSpacing(
        float fontSize,
        float multiplier,
        float expectedLineSpacing)
    {
        var style = new ElementStyle
        {
            FontFamily = "Segoe UI",
            FontSize = fontSize,
            LineHeightMultiplier = multiplier,
        };

        using SharedCanvasTextFormatCache.Lease lease =
            _renderResources.TextFormats.Acquire(
                style,
                CanvasWordWrapping.Wrap,
                CanvasHorizontalAlignment.Left,
                FlowDirection.LeftToRight);

        Assert.Equal(CanvasLineSpacingMode.Default, lease.Format.LineSpacingMode);
        Assert.Equal(expectedLineSpacing, lease.Format.LineSpacing, precision: 3);
        Assert.Equal(expectedLineSpacing * 0.8f, lease.Format.LineSpacingBaseline, precision: 3);
    }

    [Fact]
    public void LineHeightMultiplier_PartitionsCachedFormats()
    {
        var style = new ElementStyle
        {
            FontFamily = "Segoe UI",
            FontSize = 16,
            LineHeightMultiplier = 1.5f,
        };
        var compactStyle = new ElementStyle
        {
            FontFamily = "Segoe UI",
            FontSize = 16,
            LineHeightMultiplier = 1.25f,
        };

        using SharedCanvasTextFormatCache.Lease spacious =
            _renderResources.TextFormats.Acquire(
                style,
                CanvasWordWrapping.Wrap,
                CanvasHorizontalAlignment.Left,
                FlowDirection.LeftToRight);
        using SharedCanvasTextFormatCache.Lease spaciousAgain =
            _renderResources.TextFormats.Acquire(
                style,
                CanvasWordWrapping.Wrap,
                CanvasHorizontalAlignment.Left,
                FlowDirection.LeftToRight);
        using SharedCanvasTextFormatCache.Lease compact =
            _renderResources.TextFormats.Acquire(
                compactStyle,
                CanvasWordWrapping.Wrap,
                CanvasHorizontalAlignment.Left,
                FlowDirection.LeftToRight);

        Assert.Same(spacious.Format, spaciousAgain.Format);
        Assert.NotSame(spacious.Format, compact.Format);
        Assert.Equal(24f, spacious.Format.LineSpacing);
        Assert.Equal(20f, compact.Format.LineSpacing);
        Assert.Equal(2, _renderResources.TextFormats.Statistics.Count);
    }

    [Fact]
    public void LineHeightMultipliersWithSameResolvedSpacing_ReuseDescriptor()
    {
        var firstStyle = new ElementStyle
        {
            FontFamily = "Segoe UI",
            FontSize = 10,
            LineHeightMultiplier = 1.8f,
        };
        var equivalentStyle = new ElementStyle
        {
            FontFamily = "Segoe UI",
            FontSize = 10,
            // This is the next representable single-precision value after 1.8f,
            // but both multipliers resolve to exactly 18 DIPs at this font size.
            LineHeightMultiplier = 1.8000001f,
        };

        using SharedCanvasTextFormatCache.Lease first =
            _renderResources.TextFormats.Acquire(
                firstStyle,
                CanvasWordWrapping.Wrap,
                CanvasHorizontalAlignment.Left,
                FlowDirection.LeftToRight);
        using SharedCanvasTextFormatCache.Lease equivalent =
            _renderResources.TextFormats.Acquire(
                equivalentStyle,
                CanvasWordWrapping.Wrap,
                CanvasHorizontalAlignment.Left,
                FlowDirection.LeftToRight);

        Assert.Equal(18f, first.Format.LineSpacing);
        Assert.Equal(18f, equivalent.Format.LineSpacing);
        Assert.Same(first.Format, equivalent.Format);
        Assert.Equal(1, _renderResources.TextFormats.Statistics.Count);
    }

    [Fact]
    public void ContentAwareLineSpacing_OmitsNumericSpacingAndPartitionsFromCssTextFormat()
    {
        var style = new ElementStyle
        {
            FontFamily = "Segoe UI",
            FontSize = 14f,
            LineHeightMultiplier = 1.4f,
        };

        using SharedCanvasTextFormatCache.Lease textOnly =
            _renderResources.TextFormats.Acquire(
                style,
                CanvasWordWrapping.Wrap,
                CanvasHorizontalAlignment.Left,
                FlowDirection.LeftToRight);
        using SharedCanvasTextFormatCache.Lease atomicInline =
            _renderResources.TextFormats.Acquire(
                style,
                CanvasWordWrapping.Wrap,
                CanvasHorizontalAlignment.Left,
                FlowDirection.LeftToRight,
                useContentAwareLineSpacing: true);
        using SharedCanvasTextFormatCache.Lease atomicInlineAgain =
            _renderResources.TextFormats.Acquire(
                style,
                CanvasWordWrapping.Wrap,
                CanvasHorizontalAlignment.Left,
                FlowDirection.LeftToRight,
                useContentAwareLineSpacing: true);

        Assert.Equal(19.6f, textOnly.Format.LineSpacing, precision: 3);
        Assert.Equal(-1f, atomicInline.Format.LineSpacing);
        Assert.Equal(1f, atomicInline.Format.LineSpacingBaseline);
        Assert.Same(atomicInline.Format, atomicInlineAgain.Format);
        Assert.NotSame(textOnly.Format, atomicInline.Format);
        Assert.Equal(2, _renderResources.TextFormats.Statistics.Count);
    }

    [Fact]
    public void LineHeightMultiplier_ChangesMeasuredMultilineLayoutHeight()
    {
        var compactStyle = new ElementStyle
        {
            FontFamily = "Segoe UI",
            FontSize = 16,
            LineHeightMultiplier = 1.25f,
        };
        var spaciousStyle = new ElementStyle
        {
            FontFamily = "Segoe UI",
            FontSize = 16,
            LineHeightMultiplier = 1.5f,
        };
        const string text = "first line\nsecond line\nthird line\nfourth line";

        using SharedCanvasTextFormatCache.Lease compactFormat =
            _renderResources.TextFormats.Acquire(
                compactStyle,
                CanvasWordWrapping.NoWrap,
                CanvasHorizontalAlignment.Left,
                FlowDirection.LeftToRight);
        using SharedCanvasTextFormatCache.Lease spaciousFormat =
            _renderResources.TextFormats.Acquire(
                spaciousStyle,
                CanvasWordWrapping.NoWrap,
                CanvasHorizontalAlignment.Left,
                FlowDirection.LeftToRight);
        using var compactLayout = new CanvasTextLayout(
            CanvasDevice.GetSharedDevice(), text, compactFormat.Format, 1_000, float.MaxValue);
        using var spaciousLayout = new CanvasTextLayout(
            CanvasDevice.GetSharedDevice(), text, spaciousFormat.Format, 1_000, float.MaxValue);

        double compactHeight = compactLayout.LayoutBounds.Height;
        double spaciousHeight = spaciousLayout.LayoutBounds.Height;

        Assert.True(
            spaciousHeight > compactHeight + 8,
            $"Expected 1.5 line-height layout ({spaciousHeight} DIPs) to exceed the 1.25 layout ({compactHeight} DIPs). ");
    }

    [Fact]
    public void LineHeightMultiplier_IsUnscaledByFlowDirectionAndInvalidValuesUseDefault()
    {
        var styled = new ElementStyle
        {
            FontFamily = "Segoe UI",
            FontSize = 16,
            LineHeightMultiplier = 1.5f,
        };
        var invalid = new ElementStyle
        {
            FontFamily = "Segoe UI",
            FontSize = 16,
            LineHeightMultiplier = float.NaN,
        };
        var overflowing = new ElementStyle
        {
            FontFamily = "Segoe UI",
            FontSize = 16,
            LineHeightMultiplier = float.MaxValue,
        };

        using SharedCanvasTextFormatCache.Lease ltr =
            _renderResources.TextFormats.Acquire(
                styled,
                CanvasWordWrapping.Wrap,
                CanvasHorizontalAlignment.Left,
                FlowDirection.LeftToRight);
        using SharedCanvasTextFormatCache.Lease rtl =
            _renderResources.TextFormats.Acquire(
                styled,
                CanvasWordWrapping.Wrap,
                CanvasHorizontalAlignment.Left,
                FlowDirection.RightToLeft);
        using SharedCanvasTextFormatCache.Lease defaultSpacing =
            _renderResources.TextFormats.Acquire(
                invalid,
                CanvasWordWrapping.Wrap,
                CanvasHorizontalAlignment.Left,
                FlowDirection.LeftToRight);
        using SharedCanvasTextFormatCache.Lease overflowSpacing =
            _renderResources.TextFormats.Acquire(
                overflowing,
                CanvasWordWrapping.Wrap,
                CanvasHorizontalAlignment.Left,
                FlowDirection.LeftToRight);

        Assert.Equal(24f, ltr.Format.LineSpacing);
        Assert.Equal(24f, rtl.Format.LineSpacing);
        Assert.Equal(-1f, defaultSpacing.Format.LineSpacing);
        Assert.Equal(-1f, overflowSpacing.Format.LineSpacing);
    }

    [Fact]
    public void LineHeightMultiplier_ExcessiveFiniteSpacingFallsBackAndSharesDefaultDescriptor()
    {
        var invalidStyle = new ElementStyle
        {
            FontFamily = "Segoe UI",
            FontSize = 16,
            LineHeightMultiplier = float.NaN,
        };
        var excessiveFiniteStyle = new ElementStyle
        {
            FontFamily = "Segoe UI",
            FontSize = 16,
            // 1,600,000 DIPs is finite, but exceeds DirectWrite's text-layout
            // dimension envelope. This must take the same safe fallback as an
            // invalid multiplier instead of reaching CanvasTextFormat.
            LineHeightMultiplier = 100_000f,
        };

        using SharedCanvasTextFormatCache.Lease defaultSpacing =
            _renderResources.TextFormats.Acquire(
                invalidStyle,
                CanvasWordWrapping.Wrap,
                CanvasHorizontalAlignment.Left,
                FlowDirection.LeftToRight);
        using SharedCanvasTextFormatCache.Lease excessiveSpacing =
            _renderResources.TextFormats.Acquire(
                excessiveFiniteStyle,
                CanvasWordWrapping.Wrap,
                CanvasHorizontalAlignment.Left,
                FlowDirection.LeftToRight);

        Assert.Same(defaultSpacing.Format, excessiveSpacing.Format);
        Assert.Equal(-1f, excessiveSpacing.Format.LineSpacing);
        Assert.Equal(1, _renderResources.TextFormats.Statistics.Count);
    }

    [Fact]
    public void LineHeightMultiplier_AtDimensionCeilingRemainsExplicit()
    {
        var style = new ElementStyle
        {
            FontFamily = "Segoe UI",
            FontSize = 10,
            LineHeightMultiplier = 100_000f,
        };

        using SharedCanvasTextFormatCache.Lease lease =
            _renderResources.TextFormats.Acquire(
                style,
                CanvasWordWrapping.Wrap,
                CanvasHorizontalAlignment.Left,
                FlowDirection.LeftToRight);

        Assert.Equal(1_000_000f, lease.Format.LineSpacing);
        Assert.Equal(800_000f, lease.Format.LineSpacingBaseline);
    }

    [Fact]
    public void PackagedFontUri_DropsXamlFamilyFragmentForCanvas()
    {
        const string xamlFontFamily =
            "ms-appx:///Assets/Fonts/JetBrainsMono-Variable.ttf#JetBrains Mono";
        var style = new ElementStyle
        {
            FontFamily = xamlFontFamily,
            FontSize = 13,
        };

        using SharedCanvasTextFormatCache.Lease lease =
            _renderResources.TextFormats.Acquire(
                style,
                CanvasWordWrapping.NoWrap,
                CanvasHorizontalAlignment.Left,
                FlowDirection.LeftToRight);

        Assert.Equal(
            "ms-appx:///Assets/Fonts/JetBrainsMono-Variable.ttf",
            lease.Format.FontFamily);
        Assert.Equal(
            "ms-appx:///Assets/Fonts/JetBrainsMono-Variable.ttf",
            SharedCanvasTextFormatCache.NormalizeFontFamilyForCanvas(xamlFontFamily));
    }
}
