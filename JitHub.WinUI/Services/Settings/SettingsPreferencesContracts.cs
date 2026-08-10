namespace JitHub.Services;

public interface ISettingsPreferencesService
{
    string GetTheme();

    void SetTheme(string theme);

    bool IsDeveloperMode { get; set; }

    string GetVersionText();
}
