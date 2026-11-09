using Microsoft.UI.Xaml;

namespace JitHub.Services;

public interface IThemeService
{
    void SetTheme(string theme);

    void SetPalette(string paletteId);

    ApplicationTheme GetSystemTheme();

    ApplicationTheme GetApplicationTheme();

    string GetTheme();

    string GetPalette();
}
