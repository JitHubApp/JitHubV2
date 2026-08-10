using System;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Windows.ApplicationModel.Resources;

namespace JitHub.Services;

public sealed class LocalizationService
{
    private readonly ResourceLoader? _resourceLoader;

    public LocalizationService()
    {
        try
        {
            _resourceLoader = new ResourceLoader();
        }
        catch (COMException)
        {
            // Unpackaged development and isolated UI automation may not have a
            // PRI resource map. Callers supply the canonical English fallback.
        }
    }

    public string GetString(string resourceKey)
    {
        if (string.IsNullOrWhiteSpace(resourceKey))
        {
            return string.Empty;
        }

        if (_resourceLoader is null)
        {
            return resourceKey;
        }

        try
        {
            string value = _resourceLoader.GetString(NormalizeResourceKey(resourceKey));
            return string.IsNullOrWhiteSpace(value) ? resourceKey : value;
        }
        catch (COMException)
        {
            return resourceKey;
        }
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

