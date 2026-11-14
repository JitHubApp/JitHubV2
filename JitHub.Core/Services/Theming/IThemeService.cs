using Microsoft.UI.Xaml;

namespace JitHub.Core.Services.Theming;

public interface IThemeService
{
    void SetTheme(string theme);

    void SetPalette(string paletteId);

    ApplicationTheme GetSystemTheme();

    ApplicationTheme GetApplicationTheme();

    string GetTheme();

    string GetPalette();
}
