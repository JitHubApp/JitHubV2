using System;
using System.Globalization;
using Windows.ApplicationModel.Resources;

namespace JitHub.Services;

public sealed class LocalizationService
{
    private readonly ResourceLoader _resourceLoader = ResourceLoader.GetForViewIndependentUse();

    public string GetString(string resourceKey)
    {
        if (string.IsNullOrWhiteSpace(resourceKey))
        {
            return string.Empty;
        }

        string value = _resourceLoader.GetString(NormalizeResourceKey(resourceKey));
        return string.IsNullOrWhiteSpace(value) ? resourceKey : value;
    }

    public string GetStringOrDefault(string resourceKey, string fallback)
    {
        string value = GetString(resourceKey);
        return string.Equals(value, resourceKey, StringComparison.Ordinal) ? fallback : value;
    }

    public string Format(string resourceKey, params object?[] arguments)
    {
        string format = GetString(resourceKey);
        return string.Format(CultureInfo.CurrentCulture, format, arguments);
    }

    private static string NormalizeResourceKey(string resourceKey)
    {
        return resourceKey.Replace('.', '/');
    }
}

