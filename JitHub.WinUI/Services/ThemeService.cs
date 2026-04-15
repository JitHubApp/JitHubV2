using JitHub.Models;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Windows.UI.ViewManagement;

namespace JitHub.Services;

public class ThemeService : IThemeService
{
    public const string Key = "APPLICATION_KEY";

    private readonly ISettingService _settings;

    public ThemeService(ISettingService settings)
    {
        _settings = settings;
    }

    public void SetTheme(string theme)
    {
        _settings.Save(Key, theme);
    }

    public static ApplicationTheme GetSystemThemeStatic()
    {
        var uiSettings = new UISettings();
        var color = uiSettings.GetColorValue(UIColorType.Background);
        return color == Colors.Black ? ApplicationTheme.Dark : ApplicationTheme.Light;
    }

    public static ApplicationTheme GetApplicationThemeStatic(string theme)
    {
        if (string.IsNullOrWhiteSpace(theme) || theme == ThemeConst.System)
        {
            return GetSystemThemeStatic();
        }

        return theme == ThemeConst.Dark ? ApplicationTheme.Dark : ApplicationTheme.Light;
    }

    public ApplicationTheme GetSystemTheme()
    {
        return GetSystemThemeStatic();
    }

    public ApplicationTheme GetApplicationTheme()
    {
        return GetApplicationThemeStatic(GetTheme());
    }

    public string GetTheme()
    {
        return _settings.Get<string>(Key);
    }
}
