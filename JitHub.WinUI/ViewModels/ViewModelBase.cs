using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using JitHub.Services;

namespace JitHub.WinUI.ViewModels;

public abstract partial class ViewModelBase : ObservableObject
{
    protected T GetService<T>()
        where T : notnull
    {
        return ((App)App.Current).GetService<T>();
    }

    protected LocalizationService Strings => GetService<LocalizationService>();

    protected string GetString(string resourceKey, string fallback)
    {
        return Strings.GetStringOrDefault(resourceKey, fallback);
    }

    protected string FormatString(string resourceKey, string fallback, params object?[] arguments)
    {
        string format = GetString(resourceKey, fallback);
        return string.Format(CultureInfo.CurrentCulture, format, arguments);
    }
}

