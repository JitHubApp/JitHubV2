using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using JitHub.Models;
using JitHub.Services;
using Windows.ApplicationModel;

namespace JitHub.WinUI.ViewModels.Pages;

public sealed partial class SettingsPageViewModel : ViewModelBase
{
    private readonly IAuthService _authService;
    private readonly IThemeService _themeService;
    private string _initialTheme = ThemeConst.System;

    public SettingsPageViewModel()
    {
        _authService = GetService<IAuthService>();
        _themeService = GetService<IThemeService>();

        ThemeOptions =
        [
            new ThemeOption(ThemeConst.System, GetString("Settings.ThemeOptionSystem", "System")),
            new ThemeOption(ThemeConst.Light, GetString("Settings.ThemeOptionLight", "Light")),
            new ThemeOption(ThemeConst.Dark, GetString("Settings.ThemeOptionDark", "Dark"))
        ];
    }

    public List<ThemeOption> ThemeOptions { get; }

    public Uri SourceCodeUri { get; } = new("https://github.com/JitHubApp/JitHubV2");

    public string PageTitle => GetString("pageTitle_SettingsView", "Settings");

    public string ThemeSectionTitle => GetString("theme.Text", "Theme");

    public string ThemeSectionDescription => GetString(
        "Settings.ThemeDescription",
        "Choose how JitHub should match light, dark, or system appearance.");

    public string AboutSectionTitle => GetString("menu_Settings_SubMenu_About", "About");

    public string CloseForThemeButtonText => GetString("Settings.CloseNowButton", "Close JitHub now");

    public string ViewSourceCodeButtonText => GetString("Settings.ViewSourceCodeButton", "View source code");

    public string SignOutButtonText => GetString("signOut.Text", "Sign out");

    [ObservableProperty]
    public partial ThemeOption? SelectedThemeOption { get; set; }

    [ObservableProperty]
    public partial string ThemeStatusText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsCloseForThemeVisible { get; set; }

    [ObservableProperty]
    public partial string VersionText { get; set; } = string.Empty;

    public void Initialize()
    {
        _initialTheme = NormalizeTheme(_themeService.GetTheme());
        SelectedThemeOption = FindThemeOption(_initialTheme);

        PackageVersion version = Package.Current.Id.Version;
        VersionText = FormatString(
            "Settings.VersionFormat",
            "JitHub {0}.{1}.{2}.{3}",
            version.Major,
            version.Minor,
            version.Build,
            version.Revision);
    }

    public void SignOut()
    {
        _authService.SignOut();
    }

    partial void OnSelectedThemeOptionChanged(ThemeOption? value)
    {
        if (value is null)
        {
            return;
        }

        string normalizedTheme = NormalizeTheme(value.Value);
        _themeService.SetTheme(normalizedTheme);

        bool restartRequired = !string.Equals(normalizedTheme, _initialTheme, StringComparison.Ordinal);
        ThemeStatusText = restartRequired
            ? GetString(
                "Settings.ThemeStatusChanged",
                "Theme selection was saved. Close and reopen JitHub to apply it everywhere.")
            : GetString(
                "Settings.ThemeStatusDefault",
                "Theme changes apply on the next app launch.");
        IsCloseForThemeVisible = restartRequired;
    }

    private ThemeOption FindThemeOption(string theme)
    {
        foreach (ThemeOption option in ThemeOptions)
        {
            if (string.Equals(option.Value, theme, StringComparison.Ordinal))
            {
                return option;
            }
        }

        return ThemeOptions[0];
    }

    private static string NormalizeTheme(string? theme)
    {
        if (string.Equals(theme, ThemeConst.Light, StringComparison.Ordinal))
        {
            return ThemeConst.Light;
        }

        if (string.Equals(theme, ThemeConst.Dark, StringComparison.Ordinal))
        {
            return ThemeConst.Dark;
        }

        return ThemeConst.System;
    }
}

public sealed record ThemeOption(string Value, string Label);
