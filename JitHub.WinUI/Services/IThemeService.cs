using Microsoft.UI.Xaml;

namespace JitHub.Services;

public interface IThemeService
{
    void SetTheme(string theme);

    ApplicationTheme GetSystemTheme();

    ApplicationTheme GetApplicationTheme();

    string GetTheme();
}
