using JitHub.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI;

namespace JitHub.Services;

public class ThemeService : IThemeService
{
    private ISettingService _settings;
    public static string Key = "APPLICATION_KEY";
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
        var uiSettings = new Windows.UI.ViewManagement.UISettings();
        var color = uiSettings.GetColorValue(Windows.UI.ViewManagement.UIColorType.Background);
        return color == Colors.Black ? ApplicationTheme.Dark : ApplicationTheme.Light;
    }

    public static ApplicationTheme GetApplicationThemeStatic(string theme)
    {
        if (theme != ThemeConst.System)
        {
            return theme == ThemeConst.Dark ? ApplicationTheme.Dark : ApplicationTheme.Light;
        }
        else
        {
            return GetSystemThemeStatic();
        }
    }

    public ApplicationTheme GetSystemTheme()
    {
        return GetSystemThemeStatic();
    }

    public string GetTheme()
    {
        return _settings.Get<string>(Key);
    }

    public ApplicationTheme GetApplicationTheme()
    {

        return GetApplicationThemeStatic(GetTheme());
    }
}
