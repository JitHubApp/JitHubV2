using Microsoft.UI.Xaml;
using Windows.UI;
using MarkdownRenderer.Theming;
using MarkdownRenderer.Gfm.Renderers;
using Xunit;
using static MarkdownRenderer.GitHub.Tests.ThemeSnapshotFixture;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>
/// Checks how renderer snapshots follow the host and platform theme: an
/// opposite application theme, High Contrast enforcement, and task marker
/// size and colors.
/// </summary>
[Collection(AllocationSensitiveContractCollection.Name)]
public sealed class ThemeSnapshotPlatformThemeTests
{
    [Theory]
    [InlineData(ElementTheme.Light, ApplicationTheme.Dark)]
    [InlineData(ElementTheme.Dark, ApplicationTheme.Light)]
    public void OppositeApplicationTheme_PerControlSnapshotUsesHostThemeColors(
        ElementTheme hostTheme,
        ApplicationTheme applicationTheme)
    {
        Assert.NotEqual(
            hostTheme == ElementTheme.Dark,
            applicationTheme == ApplicationTheme.Dark);

        PlatformThemeColors resolved = PlatformThemeColors.Resolve(
            hostTheme,
            scoped: default);
        ThemeSnapshot snapshot = CreatePlatformColorSnapshot(hostTheme, resolved);

        PlatformThemeColorOverrides expected = hostTheme == ElementTheme.Light
            ? LightPlatformColors()
            : DarkPlatformColors();
        Assert.Equal(expected.PrimaryText, snapshot.GetStyle(MarkdownElementKeys.Body).Foreground);
        Assert.Equal(expected.SecondaryText, snapshot.GetStyle(MarkdownElementKeys.Heading6).Foreground);
        Assert.Equal(expected.AccentText, snapshot.GetStyle(MarkdownElementKeys.Link).Foreground);
        Assert.Equal(expected.Surface, snapshot.SurfaceColor);
        Assert.Equal(hostTheme == ElementTheme.Dark, snapshot.IsDark);
    }

    [Theory]
    [InlineData(ElementTheme.Light, ApplicationTheme.Dark)]
    [InlineData(ElementTheme.Dark, ApplicationTheme.Light)]
    public void OppositeApplicationTheme_ScopedPlatformOverridesStillWin(
        ElementTheme hostTheme,
        ApplicationTheme applicationTheme)
    {
        Assert.NotEqual(
            hostTheme == ElementTheme.Dark,
            applicationTheme == ApplicationTheme.Dark);
        var scoped = new PlatformThemeColorOverrides(
            PrimaryText: Color.FromArgb(0xFF, 0x11, 0x22, 0x33),
            SecondaryText: Color.FromArgb(0xFF, 0x44, 0x55, 0x66),
            AccentText: Color.FromArgb(0xFF, 0x77, 0x88, 0x99),
            Surface: Color.FromArgb(0xFF, 0xAA, 0xBB, 0xCC));

        PlatformThemeColors resolved = PlatformThemeColors.Resolve(
            hostTheme,
            scoped);
        ThemeSnapshot snapshot = CreatePlatformColorSnapshot(hostTheme, resolved);

        Assert.Equal(scoped.PrimaryText, snapshot.GetStyle(MarkdownElementKeys.Body).Foreground);
        Assert.Equal(scoped.SecondaryText, snapshot.GetStyle(MarkdownElementKeys.Heading6).Foreground);
        Assert.Equal(scoped.AccentText, snapshot.GetStyle(MarkdownElementKeys.Link).Foreground);
        Assert.Equal(scoped.Surface, snapshot.SurfaceColor);
    }

    [Fact]
    public void HighContrast_EnforcesColorsAndPreservesConsumerTypographyAndGeometry()
    {
        var mandatory = new ElementStyle
        {
            FontSize = 14,
            Foreground = Color.FromArgb(255, 255, 255, 255),
            Background = Color.FromArgb(255, 0, 0, 0),
            Margin = new Thickness(1),
        };
        var consumerOverride = new ElementStyleOverride
        {
            FontSize = 22,
            Foreground = Color.FromArgb(255, 255, 0, 0),
            Background = Color.FromArgb(255, 0, 255, 0),
            Margin = new Thickness(99),
        };
        var styleSheet = new MarkdownStyleSheet(
        [
            new MarkdownStyleRule(
                new MarkdownStyleSelector(MarkdownStyleRole.Body),
                new ElementStyleOverride
                {
                    FontSize = 27,
                    Foreground = Color.FromArgb(255, 0, 0, 255),
                    Padding = new Thickness(7),
                }),
        ]);
        var snapshot = CreateSnapshot(
            mandatory,
            new Dictionary<string, ElementStyleOverride>
            {
                [MarkdownElementKeys.Body] = consumerOverride,
            },
            isHighContrast: true,
            styleSheet);

        ElementStyle actual = snapshot.GetStyle(MarkdownElementKeys.Body);

        Assert.Equal(mandatory.Foreground, actual.Foreground);
        Assert.Equal(mandatory.Background, actual.Background);
        Assert.Equal(consumerOverride.Margin, actual.Margin);
        Assert.Equal(new Thickness(7), actual.Padding);
        Assert.Equal(27, actual.FontSize);
    }

    [Theory]
    [InlineData(1.0, false, 20)]
    [InlineData(1.5, false, 30)]
    [InlineData(2.0, false, 40)]
    [InlineData(2.25, false, 45)]
    [InlineData(1.0, true, 40)]
    [InlineData(2.25, true, 45)]
    public void TaskMarkerSize_TracksTextScaleAndInteractiveMinimum(
        double textScaleFactor,
        bool editable,
        float expected)
    {
        ThemeSnapshot snapshot = CreateSnapshot(
            new ElementStyle(),
            new Dictionary<string, ElementStyleOverride>(),
            isHighContrast: false,
            MarkdownStyleSheet.Empty,
            textScaleFactor,
            minimumInteractiveSize: 40);

        Assert.Equal(expected, TaskListItemRenderer.GetTaskMarkerSize(snapshot, editable));
    }

    [Fact]
    public void TaskMarkerColors_UseTheResolvedSelectionContract()
    {
        Color selectionAccent = Color.FromArgb(0xFF, 0x12, 0x34, 0x56);
        Color selectionForeground = Color.FromArgb(0xFF, 0xFE, 0xDC, 0xBA);
        ThemeSnapshot snapshot = CreateSnapshot(
            new ElementStyle(),
            new Dictionary<string, ElementStyleOverride>(),
            isHighContrast: false,
            MarkdownStyleSheet.Empty,
            selectionHighlightColor: selectionAccent,
            selectionForegroundColor: selectionForeground);

        (Color accent, Color foreground) = TaskListItemRenderer.GetTaskMarkerColors(snapshot);

        Assert.Equal(selectionAccent, accent);
        Assert.Equal(selectionForeground, foreground);
    }

    private static ThemeSnapshot CreatePlatformColorSnapshot(
        ElementTheme hostTheme,
        PlatformThemeColors colors)
    {
        var styles = new Dictionary<string, ElementStyle>
        {
            [MarkdownElementKeys.Body] = new ElementStyle { Foreground = colors.PrimaryText },
            [MarkdownElementKeys.Heading6] = new ElementStyle { Foreground = colors.SecondaryText },
            [MarkdownElementKeys.Link] = new ElementStyle { Foreground = colors.AccentText },
        };
        Color transparent = Color.FromArgb(0, 0, 0, 0);
        return new ThemeSnapshot(
            styles,
            new Dictionary<string, ElementStyleOverride>(),
            colors.Surface,
            transparent,
            transparent,
            transparent,
            isDark: hostTheme == ElementTheme.Dark,
            isHighContrast: false,
            textScaleFactor: 1,
            MarkdownStyleSheet.Empty);
    }

    private static PlatformThemeColorOverrides LightPlatformColors()
        => new(
            PrimaryText: Color.FromArgb(0xFF, 0x1A, 0x1A, 0x1A),
            SecondaryText: Color.FromArgb(0xFF, 0x7A, 0x7A, 0x7A),
            AccentText: Color.FromArgb(0xFF, 0x00, 0x5F, 0xB8),
            Surface: Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF));

    private static PlatformThemeColorOverrides DarkPlatformColors()
        => new(
            PrimaryText: Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF),
            SecondaryText: Color.FromArgb(0xFF, 0x9D, 0x9D, 0x9D),
            AccentText: Color.FromArgb(0xFF, 0x60, 0xCD, 0xFF),
            Surface: Color.FromArgb(0xFF, 0x20, 0x20, 0x20));
}
