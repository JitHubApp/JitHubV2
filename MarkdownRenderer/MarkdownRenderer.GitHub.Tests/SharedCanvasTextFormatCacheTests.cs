using MarkdownRenderer.Layout;
using MarkdownRenderer.Theming;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI.Xaml;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

public sealed class SharedCanvasTextFormatCacheTests
{
    [Fact]
    public void EqualDescriptor_ReusesFormatAndLeaseSurvivesEviction()
    {
        SharedCanvasTextFormatCache.Clear();
        var style = new ElementStyle
        {
            FontFamily = "Segoe UI",
            FontSize = 17,
        };

        using SharedCanvasTextFormatCache.Lease first =
            SharedCanvasTextFormatCache.Acquire(
                style,
                CanvasWordWrapping.Wrap,
                CanvasHorizontalAlignment.Left,
                FlowDirection.LeftToRight);
        using SharedCanvasTextFormatCache.Lease second =
            SharedCanvasTextFormatCache.Acquire(
                style,
                CanvasWordWrapping.Wrap,
                CanvasHorizontalAlignment.Left,
                FlowDirection.LeftToRight);

        Assert.Same(first.Format, second.Format);
        Assert.Equal(1, SharedCanvasTextFormatCache.Statistics.Count);

        SharedCanvasTextFormatCache.Clear();

        Assert.Equal(17, first.Format.FontSize);
        Assert.Equal(0, SharedCanvasTextFormatCache.Statistics.Count);
    }

    [Fact]
    public void DirectionAndWrapping_PartitionDescriptors()
    {
        SharedCanvasTextFormatCache.Clear();
        var style = new ElementStyle();
        using SharedCanvasTextFormatCache.Lease leftToRight =
            SharedCanvasTextFormatCache.Acquire(
                style,
                CanvasWordWrapping.Wrap,
                CanvasHorizontalAlignment.Left,
                FlowDirection.LeftToRight);
        using SharedCanvasTextFormatCache.Lease rightToLeft =
            SharedCanvasTextFormatCache.Acquire(
                style,
                CanvasWordWrapping.NoWrap,
                CanvasHorizontalAlignment.Left,
                FlowDirection.RightToLeft);

        Assert.NotSame(leftToRight.Format, rightToLeft.Format);
        Assert.Equal(2, SharedCanvasTextFormatCache.Statistics.Count);
    }

    [Fact]
    public void Language_PartitionsDescriptorsAndFlowsToDirectWrite()
    {
        SharedCanvasTextFormatCache.Clear();
        var style = new ElementStyle();
        using SharedCanvasTextFormatCache.Lease english =
            SharedCanvasTextFormatCache.Acquire(
                style,
                CanvasWordWrapping.Wrap,
                CanvasHorizontalAlignment.Left,
                FlowDirection.LeftToRight,
                "en-US");
        using SharedCanvasTextFormatCache.Lease arabic =
            SharedCanvasTextFormatCache.Acquire(
                style,
                CanvasWordWrapping.Wrap,
                CanvasHorizontalAlignment.Left,
                FlowDirection.LeftToRight,
                "ar-SA");

        Assert.NotSame(english.Format, arabic.Format);
        Assert.Equal("en-US", english.Format.LocaleName);
        Assert.Equal("ar-SA", arabic.Format.LocaleName);
        Assert.Equal(2, SharedCanvasTextFormatCache.Statistics.Count);
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

        SharedCanvasTextFormatCache.Clear();
        using SharedCanvasTextFormatCache.Lease lease =
            SharedCanvasTextFormatCache.Acquire(
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
