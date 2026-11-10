namespace JitHub.Services;

public interface ISettingsPreferencesService
{
    string GetTheme();

    void SetTheme(string theme);

    string GetPalette();

    bool TrySetPalette(string paletteId);

    bool IsDeveloperMode { get; set; }

    string GetVersionText();
}
