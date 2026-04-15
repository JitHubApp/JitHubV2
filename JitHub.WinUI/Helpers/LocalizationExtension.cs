using JitHub.Services;

namespace JitHub.WinUI.Helpers;

public sealed class LocalizationExtension
{
    public string this[string resourceKey] => ((App)App.Current).GetService<LocalizationService>().GetString(resourceKey);
}

