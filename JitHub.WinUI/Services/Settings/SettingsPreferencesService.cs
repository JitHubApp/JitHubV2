using System;
using System.Diagnostics;
using JitHub.Models;
using Microsoft.UI.Xaml;
using Windows.ApplicationModel;

namespace JitHub.Services;

public sealed class SettingsPreferencesService : ISettingsPreferencesService
{
    private readonly IThemeService _themeService;
    private readonly GlobalViewModel _globalViewModel;

    public SettingsPreferencesService(IThemeService themeService, GlobalViewModel globalViewModel)
    {
        _themeService = themeService;
        _globalViewModel = globalViewModel;
    }

    public bool IsDeveloperMode
    {
        get => _globalViewModel.DevMode;
        set => _globalViewModel.DevMode = value;
    }

    public string GetTheme()
    {
        string? launchTheme = JitHub.WinUI.Program.CurrentLaunchOptions.Theme;
        if (string.Equals(launchTheme, "dark", StringComparison.OrdinalIgnoreCase))
        {
            return ThemeConst.Dark;
        }

        if (string.Equals(launchTheme, "light", StringComparison.OrdinalIgnoreCase))
        {
            return ThemeConst.Light;
        }

        return _themeService.GetTheme();
    }

    public void SetTheme(string theme)
    {
        if (Application.Current is JitHub.WinUI.App app)
        {
            app.ApplyTheme(theme);
            return;
        }

        _themeService.SetTheme(theme);
    }

    public string GetPalette()
    {
        string? launchPalette = JitHub.WinUI.Program.CurrentLaunchOptions.Palette;
        return string.IsNullOrWhiteSpace(launchPalette)
            ? _themeService.GetPalette()
            : ThemePaletteCatalog.Normalize(launchPalette);
    }

    public bool TrySetPalette(string paletteId)
    {
        if (Application.Current is JitHub.WinUI.App app)
        {
            return app.TryApplyPalette(paletteId);
        }

        _themeService.SetPalette(ThemePaletteCatalog.Normalize(paletteId));
        return true;
    }

    public string GetVersionText()
    {
        try
        {
            PackageVersion version = Package.Current.Id.Version;
            return $"{version.Major}.{version.Minor}.{version.Build}.{version.Revision}";
        }
        catch (Exception)
        {
            try
            {
                string? processPath = Environment.ProcessPath;
                if (!string.IsNullOrWhiteSpace(processPath))
                {
                    FileVersionInfo version = FileVersionInfo.GetVersionInfo(processPath);
                    return $"{version.FileMajorPart}.{version.FileMinorPart}.{version.FileBuildPart} (development)";
                }
            }
            catch (Exception)
            {
            }

            return "Development build";
        }
    }
}
