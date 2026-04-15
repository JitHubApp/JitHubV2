using System;
using System.Globalization;
using Windows.ApplicationModel.Resources;

namespace JitHub.WinUI.Helpers;

internal static class LocalizedResourceText
{
    private static readonly ResourceLoader ResourceLoader = ResourceLoader.GetForViewIndependentUse();

    public static string GetString(string resourceKey, string fallback)
    {
        if (string.IsNullOrWhiteSpace(resourceKey))
        {
            return fallback;
        }

        string value = ResourceLoader.GetString(NormalizeResourceKey(resourceKey));
        return string.IsNullOrWhiteSpace(value) ? fallback : value;
    }

    public static string Format(string resourceKey, string fallback, params object?[] arguments)
    {
        string format = GetString(resourceKey, fallback);
        return string.Format(CultureInfo.CurrentCulture, format, arguments);
    }

    private static string NormalizeResourceKey(string resourceKey)
    {
        return resourceKey.Replace('.', '/');
    }
}
