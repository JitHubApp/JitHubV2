using JitHub.Core.Models.Theming;
using JitHub.Core.Services.Startup;
using JitHub.Shared.Theming;
using Xunit;

namespace JitHub.Core.Tests.Services.Startup;

public sealed class StartupSplashPaletteTests
{
    [Theory]
    [InlineData(null, null, true, true)]
    [InlineData(null, null, false, false)]
    [InlineData(null, ThemeConst.System, true, true)]
    [InlineData(null, ThemeConst.Dark, false, true)]
    [InlineData(null, ThemeConst.Light, true, false)]
    [InlineData("light", ThemeConst.Dark, true, false)]
    [InlineData("dark", ThemeConst.Light, false, true)]
    public void UsesDarkTheme_FollowsAppPrecedence(
        string? launchTheme,
        string? storedTheme,
        bool systemUsesDark,
        bool expected)
    {
        Assert.Equal(expected, StartupSplashPalette.UsesDarkTheme(launchTheme, storedTheme, systemUsesDark));
    }

    [Theory]
    [InlineData(ThemePaletteIds.JitHub, false, 0x00F4F4F3u)]
    [InlineData(ThemePaletteIds.JitHub, true, 0x00161916u)]
    [InlineData(ThemePaletteIds.GitHub, true, 0x0017110Du)]
    [InlineData("not-a-palette", true, 0x00161916u)]
    public void ResolveCanvasColorRef_ReturnsPaletteCanvasAsColorRef(string paletteId, bool useDark, uint expected)
    {
        Assert.Equal(expected, StartupSplashPalette.ResolveCanvasColorRef(paletteId, useDark));
    }

    [Fact]
    public void ResolveCanvasColorRef_SupportsEveryPalette()
    {
        foreach (ThemePaletteDefinition palette in ThemePaletteCatalog.All)
        {
            Assert.InRange(StartupSplashPalette.ResolveCanvasColorRef(palette.Id, useDarkTheme: false), 0u, 0x00FFFFFFu);
            Assert.InRange(StartupSplashPalette.ResolveCanvasColorRef(palette.Id, useDarkTheme: true), 0u, 0x00FFFFFFu);
        }
    }
}
