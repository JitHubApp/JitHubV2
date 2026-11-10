using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using JitHub.Core.Services.Theming;
using JitHub.Core.Tests.TestSupport;
using JitHub.Shared.Theming;
using Xunit;

namespace JitHub.Core.Tests.Services.Theming;

public sealed class ThemePaletteCatalogTests
{
    [Fact]
    public void Catalog_HasStableUniqueFamiliesAndKeepsJitHubAsDefault()
    {
        Assert.Equal(20, ThemePaletteCatalog.All.Count);
        Assert.Equal(20, ThemePaletteCatalog.All.Select(static palette => palette.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(ThemePaletteIds.JitHub, ThemePaletteCatalog.Default.Id);
        Assert.Equal(ThemePaletteIds.JitHub, ThemePaletteCatalog.Normalize(null));
        Assert.Equal(ThemePaletteIds.JitHub, ThemePaletteCatalog.Normalize("unknown"));
        Assert.Equal(ThemePaletteIds.Windows11, ThemePaletteCatalog.Normalize("win11"));
        Assert.Equal(ThemePaletteIds.VisualStudioCode, ThemePaletteCatalog.Normalize("VS Code"));
    }

    [Fact]
    public void ThemeService_PersistsNormalizedPaletteAndMigratesMissingValuesToJitHub()
    {
        MemorySettingService settings = new();
        ThemeService service = new(settings);

        Assert.Equal(ThemePaletteIds.JitHub, service.GetPalette());

        service.SetPalette("VS Code");

        Assert.Equal(ThemePaletteIds.VisualStudioCode, service.GetPalette());
        Assert.Equal(ThemePaletteIds.VisualStudioCode, settings.Get<string>(ThemeService.PaletteKey));
    }
}
